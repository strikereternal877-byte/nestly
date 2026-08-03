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

    public BookingStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

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
        decimal? subscriptionDiscountAmount = null)
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

        SubscriptionId = subscriptionId;
        SubscriptionFreeVisitApplied = subscriptionFreeVisitApplied;
        SubscriptionDiscountAmountSnapshot = subscriptionDiscountAmount;

        Status = BookingStatus.Initiated;
        CreatedAtUtc = DateTime.UtcNow;
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
        RaiseDomainEvent(new BookingStatusChangedEvent(Id, previousStatus, newStatus));
    }

    /// <summary>
    /// Moves the booking to a new slot (SRS 11.15, tasks 82a-d, 83). Goes
    /// through the transient <see cref="BookingStatus.Rescheduled"/> status
    /// on its way to <see cref="BookingStatus.AwaitingFulfilment"/> - both
    /// hops are recorded in <see cref="StatusHistory"/>, and any provider
    /// assignment the booking had is implicitly cleared by landing back on
    /// AwaitingFulfilment rather than Assigned, since that assignment was
    /// made against the old slot. Eligibility (status/window/count-limit)
    /// and the new slot's own availability are the caller's
    /// responsibility (<c>IRescheduleService</c>) - this method only
    /// enforces the lifecycle transition itself.
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
}
