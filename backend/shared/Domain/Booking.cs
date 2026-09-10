using System.Security.Cryptography;
using Nestly.BuildingBlocks.Primitives;
using Nestly.Domain.Events;

namespace Nestly.Domain;

/// <summary>
/// A customer's booking (SRS 13, 23.3). The aggregate root for the booking
/// domain - owns its line items, their add-ons, and its status history as a
/// single consistency boundary, since none of those have any meaning or
/// lifecycle outside their parent booking.
///
/// Every customer/address/slot/price field here is a snapshot taken at
/// booking time, not a live join - SRS 14.1 ("booking stores full price
/// snapshot") and the note on <see cref="CustomerAddress"/> both require a
/// booking to keep reading the same values forever, even after the source
/// catalog price, address, or profile changes or is deleted.
/// </summary>
public class Booking : AggregateRoot<Guid>
{
    private readonly List<BookingItem> _items = [];
    private readonly List<BookingStatusHistory> _statusHistory = [];

    public Guid CustomerId { get; private set; }
    public string CustomerNameSnapshot { get; private set; } = string.Empty;
    public string CustomerMobileSnapshot { get; private set; } = string.Empty;

    /// <summary>Traceability only - not a foreign key. See <see cref="CustomerAddress"/>'s doc comment.</summary>
    public Guid? SourceAddressId { get; private set; }

    public string AddressLabelSnapshot { get; private set; } = string.Empty;
    public string AddressLine1Snapshot { get; private set; } = string.Empty;
    public string? AddressLine2Snapshot { get; private set; }
    public string? AddressLandmarkSnapshot { get; private set; }
    public string AddressPincodeSnapshot { get; private set; } = string.Empty;
    public string AddressCitySnapshot { get; private set; } = string.Empty;
    public string AddressStateSnapshot { get; private set; } = string.Empty;
    public decimal AddressLatitudeSnapshot { get; private set; }
    public decimal AddressLongitudeSnapshot { get; private set; }
    public string AddressContactNameSnapshot { get; private set; } = string.Empty;
    public string AddressContactMobileSnapshot { get; private set; } = string.Empty;

    /// <summary>Traceability only - not a foreign key. The window's own config can change or be removed without affecting a placed booking.</summary>
    public Guid SlotWindowId { get; private set; }

    public DateOnly SlotDate { get; private set; }
    public string SlotWindowNameSnapshot { get; private set; } = string.Empty;
    public TimeSpan SlotStartTimeSnapshot { get; private set; }
    public TimeSpan SlotEndTimeSnapshot { get; private set; }

    /// <summary>The service's booked duration at the moment this booking was created (minutes). Snapshotted so a later change to the service's duration never alters this booking's provider commitment.</summary>
    public int ServiceDurationMinutesSnapshot { get; private set; }

    /// <summary>Whether the service was time-based (see <see cref="Service.IsDurationBased"/>) when this booking was created. Snapshotted, so toggling the service later never changes whether the provider on an existing booking may be released early.</summary>
    public bool IsDurationBasedSnapshot { get; private set; }

    public decimal BasePriceSnapshot { get; private set; }
    public int QuantitySnapshot { get; private set; }
    public decimal BaseTotalSnapshot { get; private set; }
    public decimal AddOnTotalSnapshot { get; private set; }
    public decimal VisitChargeSnapshot { get; private set; }
    public decimal SubtotalSnapshot { get; private set; }
    public decimal TaxPercentageSnapshot { get; private set; }
    public decimal TaxAmountSnapshot { get; private set; }
    public decimal PlatformFeeSnapshot { get; private set; }
    public decimal TotalPayableSnapshot { get; private set; }

    /// <summary>Null until Phase 4's coupon module exists - no coupon domain to validate against yet.</summary>
    public string? CouponCodeSnapshot { get; private set; }
    public decimal? CouponDiscountAmountSnapshot { get; private set; }

    /// <summary>
    /// Wallet balance applied at checkout (SRS 11.7.2, task 310), debited from
    /// the customer's <see cref="WalletLedgerEntry"/> ledger in the same
    /// transaction as this booking's insert - see <c>BookingService.CreateAsync</c>.
    /// Null when no wallet credit was applied, same "null means not used"
    /// convention as <see cref="CouponDiscountAmountSnapshot"/>. Stacks with a
    /// coupon or subscription benefit (unlike those two, which are mutually
    /// exclusive with each other) - wallet is applied last, against whatever
    /// remains payable after any other discount.
    /// </summary>
    public decimal? WalletCreditAppliedSnapshot { get; private set; }

    /// <summary>
    /// Traceability only, same convention as <see cref="SlotWindowId"/> - not
    /// a foreign key (PRODUCT-ENHANCEMENTS.md #1, task 179). Null when no
    /// active subscription benefit was applied at booking time. A coupon and
    /// a subscription benefit are mutually exclusive per booking: a supplied
    /// coupon always takes precedence (see the booking-summary service's doc
    /// comment), so at most one of the coupon/subscription snapshot pairs is
    /// ever populated on a given booking.
    /// </summary>
    public Guid? SubscriptionId { get; private set; }

    /// <summary>Whether this booking consumed one of the subscription's free-visit credits (task 179) rather than applying its standing percentage discount.</summary>
    public bool SubscriptionFreeVisitApplied { get; private set; }

    public decimal? SubscriptionDiscountAmountSnapshot { get; private set; }

    /// <summary>
    /// Task 296 (Phase 17): the <see cref="RecurringBookingPlan"/> that
    /// generated this booking, or null for an ordinary one-off booking. This
    /// is a REAL foreign key (see <c>BookingConfiguration</c>), unlike
    /// <see cref="SourceAddressId"/>/<see cref="SlotWindowId"/>/<see cref="SubscriptionId"/>,
    /// which are traceability-only precisely because the rows they point at
    /// are mutable catalog/config that may be edited or deleted after the
    /// snapshot was taken. A plan is different: it is never hard-deleted - it
    /// is Cancelled or Completed and kept - so a Restrict FK here can never
    /// block a legitimate operation, and it guarantees the join the admin
    /// (task 299) and provider (task 300) read models make is never dangling.
    ///
    /// The column carries no snapshot of the plan's own fields (frequency,
    /// day-of-week, ...) on purpose: a customer who changes their plan's
    /// frequency expects the badge on their upcoming jobs to say the new
    /// frequency, so those must be read live through this key rather than
    /// frozen at generation time.
    /// </summary>
    public Guid? RecurringBookingPlanId { get; private set; }

    /// <summary>
    /// docs/AMC.md: the <see cref="CustomerAmcContract"/> a visit-redemption
    /// booking was created against, or null for an ordinary booking. A REAL
    /// foreign key, for the identical reason <see cref="RecurringBookingPlanId"/>
    /// is one rather than traceability-only like <see cref="SubscriptionId"/>:
    /// a contract is never hard-deleted, only moved to a terminal status and
    /// kept, so a Restrict FK here can never block a legitimate operation and
    /// guarantees the join the admin AMC report makes is never dangling.
    /// </summary>
    public Guid? AmcContractId { get; private set; }

    /// <summary>Task 241: the client-minted key from POST /bookings' idempotencyKey - see BookingService.CreateAsync. Null for callers that don't supply one (e.g. RecurringBookingSchedulerService), which simply get no dedup protection. BookingConfiguration puts a unique index on (CustomerId, IdempotencyKey); Postgres treats every NULL as distinct from every other, so only two of the same customer's rows sharing the same non-null key ever collide.</summary>
    public string? IdempotencyKey { get; private set; }

    public BookingStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>
    /// Short human-facing booking code (e.g. "GLX-260825-K7F3M"), distinct
    /// from <see cref="AggregateRoot{TId}.Id"/> - the GUID stays the real
    /// primary key everywhere internally (URLs, foreign keys, API calls);
    /// this is only ever what a customer reads over the phone, a support
    /// agent searches by, or an invoice prints. Generated once here, at
    /// construction, from the same instant as <see cref="CreatedAtUtc"/> -
    /// never regenerated, never exposed as editable.
    ///
    /// The brand prefix (see <see cref="GenerateReference"/>) changed from
    /// "NST-" to "GLX-" as part of the Glavyx rebrand (matching
    /// JwtOptions.Issuer). Only new bookings get the new prefix - a booking
    /// created before the rebrand keeps its original "NST-..." reference
    /// forever (never regenerated), and every lookup here matches on the
    /// stored string, not on a parsed/assumed prefix, so historical
    /// references keep resolving exactly as before.
    /// </summary>
    public string BookingReference { get; private set; } = string.Empty;

    /// <summary>
    /// Denormalized display field only (PROVIDER.md SCOPE BOUNDARY: "one
    /// denormalized display field (assigned_provider_id) on booking"). The
    /// authoritative record of who was assigned, by whom, and how they
    /// responded lives in <see cref="BookingProviderAssignment"/> (task 147) -
    /// this field is only ever set through <see cref="AssignProvider"/> by
    /// <c>IBookingProviderAssignmentService</c>, never mutated directly, and
    /// carries no invariant of its own (unlike <see cref="Status"/>).
    /// </summary>
    public Guid? AssignedProviderId { get; private set; }

    public IReadOnlyList<BookingItem> Items => _items;
    public IReadOnlyList<BookingStatusHistory> StatusHistory => _statusHistory;

    protected Booking() { }

    public Booking(
        Guid id,
        Guid customerId,
        CustomerSnapshot customer,
        Guid? sourceAddressId,
        AddressSnapshot address,
        SlotSnapshot slot,
        PriceSnapshot price,
        string? couponCode = null,
        decimal? couponDiscountAmount = null,
        Guid? subscriptionId = null,
        bool subscriptionFreeVisitApplied = false,
        decimal? subscriptionDiscountAmount = null,
        string? idempotencyKey = null,
        Guid? recurringBookingPlanId = null,
        decimal? walletCreditApplied = null,
        Guid? amcContractId = null,
        int serviceDurationMinutes = 0,
        bool isDurationBased = false)
        : base(id)
    {
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(price);

        CustomerId = customerId;
        CustomerNameSnapshot = customer.Name ?? throw new ArgumentException("Customer name is required.", nameof(customer));
        CustomerMobileSnapshot = customer.Mobile ?? throw new ArgumentException("Customer mobile is required.", nameof(customer));

        SourceAddressId = sourceAddressId;
        AddressLabelSnapshot = address.Label ?? string.Empty;
        AddressLine1Snapshot = address.Line1 ?? throw new ArgumentException("Address line 1 is required.", nameof(address));
        AddressLine2Snapshot = address.Line2;
        AddressLandmarkSnapshot = address.Landmark;
        AddressPincodeSnapshot = address.Pincode ?? throw new ArgumentException("Address pincode is required.", nameof(address));
        AddressCitySnapshot = address.City ?? throw new ArgumentException("Address city is required.", nameof(address));
        AddressStateSnapshot = address.State ?? throw new ArgumentException("Address state is required.", nameof(address));
        AddressLatitudeSnapshot = address.Latitude;
        AddressLongitudeSnapshot = address.Longitude;
        AddressContactNameSnapshot = address.ContactName ?? throw new ArgumentException("Address contact name is required.", nameof(address));
        AddressContactMobileSnapshot = address.ContactMobile ?? throw new ArgumentException("Address contact mobile is required.", nameof(address));

        SlotWindowId = slot.SlotWindowId;
        SlotDate = slot.Date;
        SlotWindowNameSnapshot = slot.WindowName ?? string.Empty;
        SlotStartTimeSnapshot = slot.StartTime;
        SlotEndTimeSnapshot = slot.EndTime;

        ServiceDurationMinutesSnapshot = serviceDurationMinutes;
        IsDurationBasedSnapshot = isDurationBased;

        BasePriceSnapshot = price.BasePrice;
        QuantitySnapshot = price.Quantity > 0 ? price.Quantity : throw new ArgumentOutOfRangeException(nameof(price), "Quantity must be positive.");
        BaseTotalSnapshot = price.BaseTotal;
        AddOnTotalSnapshot = price.AddOnTotal;
        VisitChargeSnapshot = price.VisitCharge;
        SubtotalSnapshot = price.Subtotal;
        TaxPercentageSnapshot = price.TaxPercentage;
        TaxAmountSnapshot = price.TaxAmount;
        PlatformFeeSnapshot = price.PlatformFee;
        TotalPayableSnapshot = price.TotalPayable;

        CouponCodeSnapshot = couponCode;
        CouponDiscountAmountSnapshot = couponDiscountAmount;
        WalletCreditAppliedSnapshot = walletCreditApplied is > 0 ? walletCreditApplied : null;

        SubscriptionId = subscriptionId;
        SubscriptionFreeVisitApplied = subscriptionFreeVisitApplied;
        SubscriptionDiscountAmountSnapshot = subscriptionDiscountAmount;

        IdempotencyKey = idempotencyKey;
        RecurringBookingPlanId = recurringBookingPlanId;
        AmcContractId = amcContractId;

        Status = BookingStatus.Initiated;
        CreatedAtUtc = DateTime.UtcNow;
        BookingReference = GenerateReference(CreatedAtUtc);
        RecordStatusHistory(null, BookingStatus.Initiated, reason: null);
        RaiseDomainEvent(new BookingCreatedEvent(Id, CustomerId));
    }

    /// <summary>
    /// Adds a booked service line (task 57's single-service cart today; the
    /// schema allows more). Only while the booking is still <see cref="BookingStatus.Initiated"/> -
    /// SRS 14.1's price snapshot must stop moving the instant payment starts.
    /// </summary>
    public BookingItem AddItem(Guid id, Guid serviceId, string nameSnapshot, string slugSnapshot, decimal unitPriceSnapshot, int quantity)
    {
        EnsureStillMutable();

        var item = new BookingItem(id, Id, serviceId, nameSnapshot, slugSnapshot, unitPriceSnapshot, quantity);
        _items.Add(item);
        return item;
    }

    /// <summary>
    /// Same as <see cref="AddItem(Guid,Guid,string,string,decimal,int)"/>,
    /// plus the variant snapshot fields (Phase 3 catalog redesign) - used
    /// when the booked service was booked against a specific
    /// <see cref="ServiceVariant"/> rather than the service's flat price.
    /// </summary>
    public BookingItem AddItem(
        Guid id, Guid serviceId, string nameSnapshot, string slugSnapshot, decimal unitPriceSnapshot, int quantity,
        Guid? serviceVariantId, string? variantNameSnapshot, int? variantDurationMinutesSnapshot)
    {
        EnsureStillMutable();

        var item = new BookingItem(
            id, Id, serviceId, nameSnapshot, slugSnapshot, unitPriceSnapshot, quantity,
            serviceVariantId, variantNameSnapshot, variantDurationMinutesSnapshot);
        _items.Add(item);
        return item;
    }

    /// <summary>
    /// Same as <see cref="AddItem(Guid,Guid,string,string,decimal,int,Guid?,string?,int?)"/>,
    /// plus the service-group snapshot fields (Appliance/Service Group
    /// catalog redesign) - used when the booked service currently belongs to
    /// a <see cref="ServiceGroup"/>. Independent of the variant fields: a
    /// service can be grouped with or without also having variants.
    /// </summary>
    public BookingItem AddItem(
        Guid id, Guid serviceId, string nameSnapshot, string slugSnapshot, decimal unitPriceSnapshot, int quantity,
        Guid? serviceVariantId, string? variantNameSnapshot, int? variantDurationMinutesSnapshot,
        Guid? serviceGroupId, string? serviceGroupNameSnapshot)
    {
        EnsureStillMutable();

        var item = new BookingItem(
            id, Id, serviceId, nameSnapshot, slugSnapshot, unitPriceSnapshot, quantity,
            serviceVariantId, variantNameSnapshot, variantDurationMinutesSnapshot,
            serviceGroupId, serviceGroupNameSnapshot);
        _items.Add(item);
        return item;
    }

    /// <summary>
    /// Adds an add-on to a previously added item (task 59d). Routed through
    /// the aggregate root - which owns <see cref="EnsureStillMutable"/> - and
    /// not called as <c>BookingItem.AddAddOn</c> directly, so the same
    /// Initiated-only lock task 56d put on <see cref="AddItem"/> also covers
    /// add-ons: SRS 14.1's price/item snapshot must stop moving the instant
    /// payment starts, and a caller holding a <see cref="BookingItem"/>
    /// reference from before that moment must not be able to keep appending
    /// to it afterwards.
    /// </summary>
    public BookingAddOnItem AddAddOnToItem(Guid bookingItemId, Guid id, Guid serviceAddOnId, string nameSnapshot, decimal unitPriceSnapshot, int quantity)
    {
        EnsureStillMutable();

        var item = _items.SingleOrDefault(i => i.Id == bookingItemId)
            ?? throw new InvalidOperationException($"Booking item {bookingItemId} was not found on this booking.");

        return item.AddAddOn(id, serviceAddOnId, nameSnapshot, unitPriceSnapshot, quantity);
    }

    /// <summary>
    /// Same as <see cref="AddAddOnToItem(Guid,Guid,Guid,string,decimal,int)"/>,
    /// plus the add-on-group snapshot fields (Phase 3 catalog redesign) -
    /// used when the selected add-on belonged to a <see cref="ServiceAddOnGroup"/>.
    /// </summary>
    public BookingAddOnItem AddAddOnToItem(
        Guid bookingItemId, Guid id, Guid serviceAddOnId, string nameSnapshot, decimal unitPriceSnapshot, int quantity,
        Guid? addOnGroupId, string? groupNameSnapshot)
    {
        EnsureStillMutable();

        var item = _items.SingleOrDefault(i => i.Id == bookingItemId)
            ?? throw new InvalidOperationException($"Booking item {bookingItemId} was not found on this booking.");

        return item.AddAddOn(id, serviceAddOnId, nameSnapshot, unitPriceSnapshot, quantity, addOnGroupId, groupNameSnapshot);
    }

    /// <summary>
    /// Advances the booking to <paramref name="newStatus"/> if
    /// <see cref="BookingLifecycle"/> allows it from the current status, and
    /// appends a status history row - the only way either ever changes (SRS
    /// 13.2: "invalid transitions must be blocked").
    /// </summary>
    public void TransitionTo(BookingStatus newStatus, string? reason = null)
    {
        if (!BookingLifecycle.IsValidTransition(Status, newStatus))
        {
            throw new InvalidOperationException($"Cannot transition a booking from {Status} to {newStatus}.");
        }

        var previousStatus = Status;
        Status = newStatus;
        RecordStatusHistory(previousStatus, newStatus, reason);
        // Task 359: the "was anything ever charged for this?" fact travels with
        // the event, because the notification planner that needs it is pure by
        // construction and cannot read the booking back.
        RaiseDomainEvent(new BookingStatusChangedEvent(Id, previousStatus, newStatus, TotalPayableSnapshot > 0m));
        RaiseTrackingEvent(newStatus);
    }

    /// <summary>
    /// Raises the tracking-specific companion event for the two fulfilment
    /// states task 264 added (task 272). Raised from the transition itself
    /// rather than from task 270's en-route/arrived endpoints, which do not
    /// exist yet: the transition is the fact, and putting the raise here means
    /// every future caller - the provider endpoints, an admin correction, a
    /// test - produces the same signal without having to know to.
    ///
    /// <see cref="BookingStatusChangedEvent"/> is still raised for these two
    /// transitions as well; a handler subscribes to one stream or the other,
    /// never both.
    /// </summary>
    private void RaiseTrackingEvent(BookingStatus newStatus)
    {
        switch (newStatus)
        {
            case BookingStatus.ProviderEnRoute:
                RaiseDomainEvent(new ProviderEnRouteEvent(Id, AssignedProviderId));
                break;
            case BookingStatus.ProviderArrived:
                RaiseDomainEvent(new ProviderArrivedEvent(Id, AssignedProviderId));
                break;
        }
    }

    /// <summary>
    /// Moves the booking to a new slot (SRS 11.15, tasks 82a-d, 83). Goes
    /// through the transient <see cref="BookingStatus.Rescheduled"/> status
    /// on its way to <see cref="BookingStatus.AwaitingFulfilment"/> - both
    /// hops are recorded in <see cref="StatusHistory"/>.
    ///
    /// <para>
    /// <b>This method does NOT touch <see cref="AssignedProviderId"/> or the
    /// live <c>BookingProviderAssignment</c> row</b> (corrected task 290 -
    /// an earlier version of this comment claimed the assignment was
    /// "implicitly cleared by landing back on AwaitingFulfilment", which was
    /// false on both counts: landing on AwaitingFulfilment is a status label,
    /// not a write to either the display field or the assignment table, and
    /// this method's own slot-field mutations happen with the old assignment
    /// still fully in place). A caller that reschedules an Assigned booking
    /// must decide separately whether the same provider stays on the job for
    /// the new slot - see
    /// <c>RescheduleService.ReconcileProviderAssignmentAfterRescheduleAsync</c>,
    /// which reuses <c>IProviderScheduleConflictService</c> (task 288) to
    /// keep the provider when they are still free, or withdraw them
    /// (<c>BookingProviderAssignment.Withdraw</c> plus <c>AssignProvider(null)</c>)
    /// when the new slot now conflicts with another job.
    /// </para>
    ///
    /// Eligibility (status/window/count-limit) and the new slot's own
    /// availability are the caller's responsibility (<c>IRescheduleService</c>)
    /// - this method only enforces the lifecycle transition itself.
    /// </summary>
    public void Reschedule(
        Guid newSlotWindowId, DateOnly newSlotDate, string newSlotWindowName, TimeSpan newSlotStartTime, TimeSpan newSlotEndTime, string? reason)
    {
        if (!BookingLifecycle.IsValidTransition(Status, BookingStatus.Rescheduled))
        {
            throw new InvalidOperationException($"Cannot reschedule a booking in status {Status}.");
        }

        SlotWindowId = newSlotWindowId;
        SlotDate = newSlotDate;
        SlotWindowNameSnapshot = newSlotWindowName;
        SlotStartTimeSnapshot = newSlotStartTime;
        SlotEndTimeSnapshot = newSlotEndTime;

        TransitionTo(BookingStatus.Rescheduled, reason);
        TransitionTo(BookingStatus.AwaitingFulfilment, "Rescheduled to new slot.");
    }

    /// <summary>
    /// Sets or clears the denormalized <see cref="AssignedProviderId"/> display
    /// field (task 147). The one and only writer of this property - kept as a
    /// dedicated method rather than a public setter so nothing outside
    /// <c>IBookingProviderAssignmentService</c> can mutate it directly (SCOPE
    /// BOUNDARY: Booking must not read Provider internals). Deliberately does
    /// not touch <see cref="Status"/> - the caller decides separately whether
    /// the status transition to/from <see cref="BookingStatus.Assigned"/> is
    /// also warranted, since a reassignment while already Assigned changes
    /// only this field.
    /// </summary>
    public void AssignProvider(Guid? providerId) => AssignedProviderId = providerId;

    private void EnsureStillMutable()
    {
        if (Status != BookingStatus.Initiated)
        {
            throw new InvalidOperationException(
                $"Booking items can only be added while the booking is Initiated (current status: {Status}).");
        }
    }

    private void RecordStatusHistory(BookingStatus? from, BookingStatus to, string? reason) =>
        _statusHistory.Add(new BookingStatusHistory(Guid.NewGuid(), Id, from, to, reason, DateTime.UtcNow));

    // Excludes 0/O/1/I - the four glyphs that get misread over a phone call or
    // a low-res screenshot, the two situations this reference exists for.
    private const string ReferenceAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    /// <summary>
    /// "GLX-YYMMDD-XXXXX": brand prefix, creation date (sorts and reads
    /// chronologically at a glance), then a 5-character random suffix from a
    /// 32-symbol alphabet - roughly 33.5M combinations per day, which makes a
    /// same-day collision astronomically unlikely at this product's volume
    /// without needing a DB round-trip to check. No retry-on-collision here
    /// for the same reason IdempotencyKey's doc comment gives for not
    /// worrying about it: the risk this is guarding against is negligible,
    /// and BookingConfiguration's unique index is the actual backstop if it
    /// is ever wrong.
    ///
    /// "GLX-" (Glavyx rebrand) replaces the earlier "NST-" prefix for every
    /// reference generated from here on; a booking already carrying an
    /// "NST-..." reference is never touched, and BookingReference's own doc
    /// comment covers why that's safe.
    /// </summary>
    private static string GenerateReference(DateTime createdAtUtc)
    {
        Span<byte> randomBytes = stackalloc byte[5];
        RandomNumberGenerator.Fill(randomBytes);
        Span<char> suffix = stackalloc char[5];
        for (int i = 0; i < suffix.Length; i++)
        {
            suffix[i] = ReferenceAlphabet[randomBytes[i] % ReferenceAlphabet.Length];
        }
        return $"GLX-{createdAtUtc:yyMMdd}-{new string(suffix)}";
    }
}
