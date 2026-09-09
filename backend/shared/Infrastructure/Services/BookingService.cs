using System.Data;
using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Application.Abstractions.Observability;
using Nestly.Application.Bookings;
using Nestly.Application.Coupons;
using Nestly.Application.Reviews;
using Nestly.Application.Slots;
using Nestly.Application.Subscriptions;
using Nestly.Application.Wallet;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;

namespace Nestly.Infrastructure.Services;

/// <summary>Booking creation and reads (SRS 13, tasks 58-61).</summary>
public class BookingService : IBookingService
{
    /// <summary>
    /// Recorded on the auto-transition to PaymentPending.
    ///
    /// Every status reason on a booking is rendered verbatim in the customer's
    /// own timeline (see the customer booking detail page), so this has to read
    /// as customer copy - it previously carried an internal note about the
    /// payment gateway not being integrated, which told customers the platform
    /// had no working payment integration. Keep the sibling
    /// <see cref="NothingPayableReason"/>'s register.
    /// </summary>
    private const string AwaitingPaymentReason = "Awaiting payment to confirm this booking.";

    /// <summary>
    /// Task 331: recorded on the auto-transition to Confirmed taken by a
    /// booking with nothing payable, so the one thing a booking-payment
    /// mapping would otherwise have explained - why this booking was never
    /// charged - stays on the record, in the booking's own status timeline,
    /// for anyone reading it later.
    /// </summary>
    private const string NothingPayableReason = "Nothing payable on this booking - confirmed without a payment.";

    /// <summary>Task 137b: the specific error code SlotAvailabilityService.ReserveSlotAsync returns when a slot has no remaining per-day capacity.</summary>
    private const string SlotCapacityReachedErrorCode = "Booking.SlotCapacityReached";

    private readonly IBookingSummaryService _summaryService;
    private readonly IBookingRepository _bookingRepository;
    private readonly ICustomerRepository _customerRepository;
    private readonly ICouponService _couponService;
    private readonly ISlotAvailabilityService _slotAvailabilityService;
    private readonly IMetricsService _metricsService;
    private readonly IBookingProviderAssignmentRepository _assignmentRepository;
    // Task 275: read-only, and only ever for the assigned provider's own
    // display identity. Booking deliberately has no Provider navigation
    // (PROVIDER.md scope boundary), so the one place that needs to name the
    // provider resolves it through the repository instead.
    private readonly IProviderRepository _providerRepository;
    // Task 293: the per-provider rating aggregate behind the same summary.
    // Read-only, and only for the assigned provider - this service does not
    // otherwise know about reviews.
    private readonly IReviewRepository _reviewRepository;
    private readonly ICustomerSubscriptionRepository _customerSubscriptionRepository;
    // Task 310: applying wallet credit at checkout goes through the same
    // atomic DebitAsync the standalone wallet-spend paths use (NestlyCoinsService's
    // clawback, RefundService's wallet payout) - see WalletService.DebitAsync's
    // doc comment for how it detects and reuses this method's own ambient
    // transaction instead of nesting one.
    private readonly IWalletService _walletService;
    // Same reasoning as RefundService: the slot-capacity reservation, coupon
    // reservation, subscription free-visit consumption and the booking write
    // itself are each their own SaveChangesAsync against repositories that
    // all share this same scoped context - without an explicit transaction
    // around them, an exception after the slot reservation commits (e.g. the
    // booking insert itself failing) leaves that reservation's capacity
    // consumed forever with no booking to show for it, instead of rolling
    // back cleanly. Observed directly in local testing: a failed booking
    // attempt left slot_booking_counter incremented with zero matching rows
    // in booking for that (slotWindowId, date).
    private readonly NestlyDbContext _context;

    public BookingService(
        IBookingSummaryService summaryService,
        IBookingRepository bookingRepository,
        ICustomerRepository customerRepository,
        ICouponService couponService,
        ISlotAvailabilityService slotAvailabilityService,
        IMetricsService metricsService,
        IBookingProviderAssignmentRepository assignmentRepository,
        IProviderRepository providerRepository,
        IReviewRepository reviewRepository,
        ICustomerSubscriptionRepository customerSubscriptionRepository,
        IWalletService walletService,
        NestlyDbContext context)
    {
        _summaryService = summaryService;
        _bookingRepository = bookingRepository;
        _customerRepository = customerRepository;
        _couponService = couponService;
        _slotAvailabilityService = slotAvailabilityService;
        _metricsService = metricsService;
        _assignmentRepository = assignmentRepository;
        _providerRepository = providerRepository;
        _reviewRepository = reviewRepository;
        _customerSubscriptionRepository = customerSubscriptionRepository;
        _walletService = walletService;
        _context = context;
    }

    public async Task<Result<BookingDetailResponse>> CreateAsync(
        Guid customerId,
        BookingSummaryRequest request,
        Guid? recurringBookingPlanId = null,
        Guid? amcContractId = null)
    {
        // Task 241: checked before anything else reserves - a retried
        // request carrying the same key must not take a second slot seat,
        // coupon redemption, or subscription free-visit credit before
        // discovering the booking already exists. Scoped to this customer:
        // BookingConfiguration's unique index is global, but there is no
        // reason a key from one customer's session should ever resolve
        // another's booking even if it somehow collided.
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var existingByKey = await _bookingRepository.GetByIdempotencyKeyAsync(customerId, request.IdempotencyKey);
            if (existingByKey is not null)
            {
                var activeAssignment = await _assignmentRepository.GetActiveByBookingAsync(existingByKey.Id);
                return Result.Success(ToDetailResponse(existingByKey, activeAssignment?.Status, await ProviderSummaryFor(activeAssignment)));
            }
        }

        // Re-validates every precondition (58a-f) through the same code path
        // the preview uses, so creation can never succeed on a combination
        // the preview would have rejected.
        var summaryResult = await _summaryService.GetSummaryAsync(customerId, request);
        if (summaryResult.IsFailure)
        {
            // Slot availability now filters out windows that are already at
            // capacity, so most contention is caught here rather than by
            // ReserveSlotAsync below. The conflict counter has to be fed from
            // both places or "slot-conflict rate" only counts the narrow race
            // and reads as near-zero while customers are being turned away.
            if (summaryResult.Error.Code == SlotCapacityReachedErrorCode)
            {
                _metricsService.RecordSlotConflict();
            }

            _metricsService.RecordBookingCreated(succeeded: false, summaryResult.Error.Code);
            return summaryResult.Error;
        }

        var customer = await _customerRepository.GetByIdAsync(customerId);
        if (customer is null)
        {
            const string errorCode = "Booking.CustomerNotFound";
            _metricsService.RecordBookingCreated(succeeded: false, errorCode);
            return Error.NotFound(errorCode, "The specified customer does not exist.");
        }

        var summary = summaryResult.Value;

        // docs/AMC.md: resolved and validated here, before anything reserves,
        // the same "fail before reserving" discipline the idempotency and
        // summary checks above already follow. Read directly off the shared
        // context rather than through a repository abstraction - the same
        // "cross-aggregate lookup, no single repository owns this join"
        // pattern ProviderScheduleConflictService and
        // BookingAssignmentConflictService already use, chosen here
        // specifically to avoid adding a new constructor dependency to a
        // class this widely constructed directly in tests.
        CustomerAmcContract? amcContract = null;
        if (amcContractId is { } contractId)
        {
            amcContract = await _context.CustomerAmcContracts.FirstOrDefaultAsync(c => c.Id == contractId);
            if (amcContract is null || amcContract.CustomerId != customerId)
            {
                const string errorCode = "Amc.ContractNotFound";
                _metricsService.RecordBookingCreated(succeeded: false, errorCode);
                return Error.NotFound(errorCode, "The specified AMC contract does not exist.");
            }

            if (!amcContract.CanRedeem(DateTime.UtcNow))
            {
                const string errorCode = "Amc.ContractNotRedeemable";
                _metricsService.RecordBookingCreated(succeeded: false, errorCode);
                return Error.Conflict(errorCode, "This AMC contract has no entitlement remaining or its term has ended.");
            }

            Guid? serviceCategoryId = await _context.Set<Service>()
                .Where(s => s.Id == summary.Service.Id)
                .Select(s => (Guid?)s.CategoryId)
                .FirstOrDefaultAsync();

            if (serviceCategoryId != amcContract.CategoryIdSnapshot)
            {
                const string errorCode = "Amc.CategoryMismatch";
                _metricsService.RecordBookingCreated(succeeded: false, errorCode);
                return Error.Validation(errorCode, "This AMC contract does not cover the selected service's category.");
            }
        }

        // Everything below writes: slot capacity, coupon usage, subscription
        // free-visit credit, wallet credit, and the booking itself each go
        // through their own SaveChangesAsync on this same scoped context.
        // Without an explicit transaction, an exception partway through (e.g.
        // the booking insert itself failing) would leave an earlier
        // reservation - most dangerously the slot capacity, since nothing
        // else ever frees it - committed with no booking to show for it. Same
        // pattern as RefundService for the same reason.
        //
        // Task 310: Serializable only when wallet credit is actually being
        // applied - WalletService.DebitAsync's read-check-write needs that
        // isolation level to catch two concurrent debits that would otherwise
        // both read the same stale balance (see its doc comment); an ordinary
        // booking that never touches the wallet ledger does not pay for it.
        var isolationLevel = summary.Wallet.AppliedAmount > 0 ? IsolationLevel.Serializable : IsolationLevel.Unspecified;
        await using var dbTransaction = await _context.Database.BeginTransactionAsync(isolationLevel);
        try
        {
            // Reserve the slot's per-day capacity (SRS 12.10.1, task 135c)
            // before anything else: an atomic conditional update, same shape as
            // the coupon reservation below, so two customers racing for the
            // last seat on a promoted slot cannot both succeed. Checked first
            // because under promotion-level traffic the slot - not the coupon -
            // is the more likely contended resource.
            var slotReservation = await _slotAvailabilityService.ReserveSlotAsync(summary.Slot.SlotWindowId, summary.Slot.Date);
            if (slotReservation.IsFailure)
            {
                // Task 137b: tracked as its own counter (not just a
                // RecordBookingCreated failure reason) so "slot-conflict rate"
                // can be graphed directly as this counter's rate against total
                // booking-creation attempts.
                if (slotReservation.Error.Code == SlotCapacityReachedErrorCode)
                {
                    _metricsService.RecordSlotConflict();
                }

                _metricsService.RecordBookingCreated(succeeded: false, slotReservation.Error.Code);
                return slotReservation.Error;
            }

            // Reserve the coupon's usage slot before the booking exists (task
            // 72c/73): CouponRedemption has a foreign key to the booking, so it
            // cannot be inserted until the booking is, but the atomic usage-cap
            // check must happen before persisting anything - otherwise a lost
            // race would create a booking with a discount that was never really
            // available. Reserves against both the coupon's global cap and
            // customerId's per-customer cap (NESTLY-009).
            //
            // docs/AMC.md: skipped entirely when redeeming AMC entitlement -
            // the contract already fully covers the visit, so a coupon on the
            // same request is never reserved or applied (see IBookingService.CreateAsync's
            // amcContractId doc comment).
            if (summary.Coupon is not null && amcContract is null)
            {
                var reserveResult = await _couponService.ReserveAsync(summary.Coupon.CouponId, customerId);
                if (reserveResult.IsFailure)
                {
                    _metricsService.RecordBookingCreated(succeeded: false, reserveResult.Error.Code);
                    return reserveResult.Error;
                }
            }

            // Task 179: consume the free-visit credit before the booking exists,
            // same reasoning as the coupon reservation above - an atomic
            // conditional UPDATE (ICustomerSubscriptionRepository.TryConsumeFreeVisitAsync)
            // so two bookings racing for a subscriber's last free visit cannot
            // both win. A percentage-discount benefit (FreeVisitApplied false)
            // has no counter to consume - it's a standing benefit, not a
            // per-cycle credit - so nothing to reserve in that branch.
            //
            // docs/AMC.md: skipped when redeeming AMC entitlement, same
            // reasoning as the coupon guard above - AMC takes precedence over
            // a subscription benefit for this one booking rather than
            // stacking with it.
            if (summary.SubscriptionBenefit is { FreeVisitApplied: true } benefit && amcContract is null)
            {
                bool consumed = await _customerSubscriptionRepository.TryConsumeFreeVisitAsync(benefit.SubscriptionId);
                if (!consumed)
                {
                    const string errorCode = "Subscription.FreeVisitNoLongerAvailable";
                    _metricsService.RecordBookingCreated(succeeded: false, errorCode);
                    return Error.Conflict(errorCode, "Your subscription's free visit is no longer available. Please retry.");
                }
            }

            // Task 310: applied last, after every other reservation above -
            // matching the order the summary itself computed it in (against
            // whatever remains payable once coupon/subscription have already
            // been taken off). bookingId is minted here, ahead of the Booking
            // constructor below, purely so the wallet ledger entry can carry
            // a real SourceReferenceId rather than being written first and
            // patched afterwards.
            // docs/AMC.md: wallet credit is skipped too when redeeming AMC
            // entitlement - there is nothing left payable to apply it
            // against (see the finalPayable override below).
            var bookingId = Guid.NewGuid();
            if (summary.Wallet.AppliedAmount > 0 && amcContract is null)
            {
                var debitResult = await _walletService.DebitAsync(
                    customerId, summary.Wallet.AppliedAmount, WalletSourceType.BookingWalletCredit, bookingId,
                    "Wallet credit applied at checkout");
                if (debitResult.IsFailure)
                {
                    // Lost the race against another concurrent spend of the
                    // same balance (or it genuinely changed since the preview
                    // was fetched) - same "stale preview, fail cleanly"
                    // handling as a coupon or slot that went stale underneath
                    // the customer.
                    _metricsService.RecordBookingCreated(succeeded: false, debitResult.Error.Code);
                    return debitResult.Error;
                }
            }

            // docs/AMC.md: the contract already fully covers the visit, so the
            // final payable is forced to zero here rather than trusting
            // whatever summary.FinalPayable computed (which never knew an AMC
            // redemption was in play) - the price BREAKDOWN above is left
            // untouched so the customer/admin can still see what the visit
            // would have cost, only the amount actually due is zeroed.
            decimal finalPayable = amcContract is not null ? 0m : summary.FinalPayable;

            var booking = new Booking(
                bookingId,
                customerId,
                new CustomerSnapshot(customer.Name, customer.Mobile),
                summary.Address.Id,
                new AddressSnapshot(
                    summary.Address.Label, summary.Address.Line1, summary.Address.Line2, summary.Address.Landmark,
                    summary.Address.Pincode, summary.Address.City, summary.Address.State,
                    summary.Address.Latitude, summary.Address.Longitude,
                    summary.Address.ContactName, summary.Address.ContactMobile),
                new SlotSnapshot(summary.Slot.SlotWindowId, summary.Slot.Date, summary.Slot.Name, summary.Slot.StartTime, summary.Slot.EndTime),
                new PriceSnapshot(
                    summary.Price.BasePrice, summary.Price.Quantity, summary.Price.BaseTotal, summary.Price.AddOnTotal,
                    summary.Price.VisitCharge, summary.Price.Subtotal, summary.Price.TaxPercentage,
                    summary.Price.TaxAmount, summary.Price.PlatformFee, finalPayable),
                amcContract is null ? summary.Coupon?.Code : null,
                amcContract is null ? summary.Coupon?.DiscountAmount : null,
                amcContract is null ? summary.SubscriptionBenefit?.SubscriptionId : null,
                amcContract is null && (summary.SubscriptionBenefit?.FreeVisitApplied ?? false),
                amcContract is null ? summary.SubscriptionBenefit?.DiscountAmount : null,
                string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey,
                // Task 297: the occurrence's link back to the plan that
                // generated it. Set here rather than by the scheduler
                // afterwards so it is part of the same insert - a booking that
                // exists without its plan id, even briefly, is one the admin
                // (task 299) and provider (task 300) views would show as a
                // one-off.
                recurringBookingPlanId,
                amcContract is null ? summary.Wallet.AppliedAmount : null,
                amcContract?.Id,
                // Provider-commitment snapshot: the variant's duration when one
                // is selected, else the service's own, plus whether the service
                // is time-based - frozen here so later catalog edits never
                // change this booking's commitment (see Booking's snapshots).
                summary.Service.VariantDurationMinutes ?? summary.Service.DurationMinutes,
                summary.Service.IsDurationBased);

            // Add-on line items come from the price breakdown, not summary.AddOns:
            // the breakdown already carries each selection's quantity and
            // resolved unit price, exactly what the snapshot needs, whereas
            // summary.AddOns is a plain catalog projection for display.
            //
            // Phase 3 catalog redesign: the variant/group overloads are used
            // only when the summary actually carries a selection - a service
            // with no variants and an add-on with no group produce exactly
            // the same BookingItem/BookingAddOnItem rows as before this field
            // existed.
            var item = summary.Service.VariantId is Guid variantId
                ? booking.AddItem(
                    Guid.NewGuid(), summary.Service.Id, summary.Service.Name, summary.Service.Slug,
                    summary.Price.BasePrice, summary.Price.Quantity,
                    variantId, summary.Service.VariantName, summary.Service.VariantDurationMinutes,
                    summary.Service.GroupId, summary.Service.GroupName)
                : summary.Service.GroupId is Guid serviceGroupId
                    ? booking.AddItem(
                        Guid.NewGuid(), summary.Service.Id, summary.Service.Name, summary.Service.Slug,
                        summary.Price.BasePrice, summary.Price.Quantity,
                        null, null, null,
                        serviceGroupId, summary.Service.GroupName)
                    : booking.AddItem(
                        Guid.NewGuid(), summary.Service.Id, summary.Service.Name, summary.Service.Slug,
                        summary.Price.BasePrice, summary.Price.Quantity);

            foreach (var addOnLine in summary.Price.AddOnLineItems)
            {
                if (addOnLine.GroupId is Guid groupId)
                {
                    booking.AddAddOnToItem(
                        item.Id, Guid.NewGuid(), addOnLine.AddOnId, addOnLine.Name, addOnLine.UnitPrice, addOnLine.Quantity,
                        groupId, addOnLine.GroupName);
                }
                else
                {
                    booking.AddAddOnToItem(item.Id, Guid.NewGuid(), addOnLine.AddOnId, addOnLine.Name, addOnLine.UnitPrice, addOnLine.Quantity);
                }
            }

            // Task 331: a booking with nothing left to pay is confirmed here
            // and never enters PaymentPending. Stated as "is anything
            // payable", not "is this an AMC redemption", because three
            // different features already produce a zero payable - AMC
            // entitlement (finalPayable forced to zero above), wallet credit
            // covering the whole remainder, and a coupon/subscription
            // discount that takes the total to zero (BookingSummaryService
            // floors each at zero) - and none of them has, or should need,
            // its own confirmation path. PaymentPending is not merely
            // unnecessary for these: it is a dead end, since
            // PaymentTransaction rejects a non-positive amount and
            // PaymentService therefore cannot produce the gateway round trip
            // that would confirm them.
            if (finalPayable <= 0)
            {
                booking.TransitionTo(BookingStatus.Confirmed, NothingPayableReason);
            }
            else
            {
                booking.TransitionTo(BookingStatus.PaymentPending, AwaitingPaymentReason);
            }

            if (!await _bookingRepository.TryAddAsync(booking))
            {
                // Task 241: lost a genuine concurrent race against another
                // request carrying the same key (the check at the top of this
                // method only catches a *sequential* retry, since it runs before
                // this one's own insert exists to be found) - hand back the
                // winner's booking rather than a duplicate. Unlike before this
                // method wrapped everything in a transaction, this branch does
                // not leak the slot/coupon/subscription reservations this
                // losing request just took above: not committing here lets the
                // `await using` disposal below roll the whole transaction back.
                var winner = await _bookingRepository.GetByIdempotencyKeyAsync(customerId, request.IdempotencyKey!);
                if (winner is null)
                {
                    _metricsService.RecordBookingCreated(succeeded: false, "Booking.IdempotencyRaceUnresolved");
                    return Error.Infrastructure(
                        "Booking.IdempotencyRaceUnresolved",
                        "Could not create or locate a booking for this request. Please retry.");
                }

                var winnerAssignment = await _assignmentRepository.GetActiveByBookingAsync(winner.Id);
                return Result.Success(ToDetailResponse(winner, winnerAssignment?.Status, await ProviderSummaryFor(winnerAssignment)));
            }

            // Task 357: the `amcContract is null` half of this condition has to
            // match the ReserveAsync guard above exactly - the two calls are the
            // two halves of one redemption (reserve the cap, then link the
            // reservation to the booking), so a redemption record written
            // without a matching reservation records a redemption that never
            // consumed a usage slot and never discounted anything (the booking's
            // CouponCodeSnapshot/discount are nulled for an AMC redemption too).
            // Skipped rather than rejected: IBookingService.CreateAsync's
            // amcContractId doc comment and docs/AMC.md both define a coupon on
            // an AMC redemption as *ignored*, and the request reaching here is
            // the customer's own checkout request forwarded by
            // AmcCustomerService.RedeemVisitAsync - failing it would turn a
            // stale coupon still sitting in the cart into a blocked redemption.
            if (summary.Coupon is not null && amcContract is null)
            {
                await _couponService.CreateRedemptionRecordAsync(summary.Coupon.CouponId, customerId, booking.Id, summary.Coupon.DiscountAmount);
            }

            await dbTransaction.CommitAsync();

            _metricsService.RecordBookingCreated(succeeded: true);
            return Result.Success(ToDetailResponse(booking, providerAssignmentStatus: null, provider: null));
        }
        catch
        {
            await dbTransaction.RollbackAsync();
            throw;
        }
    }

    public async Task<Result<BookingListResponse>> ListAsync(Guid customerId, BookingStatusBucket? bucket, int page = 1, int pageSize = 20)
    {
        var statuses = bucket is null
            ? Enum.GetValues<BookingStatus>()
            : BookingStatusMapper.StatusesInBucket(bucket.Value);

        (page, pageSize) = PagedQueryExtensions.Normalize(page, pageSize);

        var (bookings, totalCount) = await _bookingRepository.ListByCustomerPagedAsync(customerId, statuses, page, pageSize);

        IReadOnlyList<BookingListItemResponse> items = bookings.Select(ToListItem).ToList();
        return Result.Success(new BookingListResponse(items, totalCount, page, pageSize));
    }

    public async Task<Result<BookingDetailResponse>> GetDetailAsync(Guid customerId, Guid bookingId)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null || booking.CustomerId != customerId)
        {
            return Error.NotFound("Booking.NotFound", "The specified booking does not exist.");
        }

        // GetCurrentByBookingAsync, not GetActiveByBookingAsync: this is a read
        // path (the customer's own view of their booking), and a just-finished
        // job's assignment is still the right one to show "who did this" - the
        // fallback for a null assignment ("Not assigned yet") would otherwise
        // wrongly apply to a job that already completed.
        var currentAssignment = await _assignmentRepository.GetCurrentByBookingAsync(bookingId);
        return Result.Success(ToDetailResponse(booking, currentAssignment?.Status, await ProviderSummaryFor(currentAssignment)));
    }

    private static BookingListItemResponse ToListItem(Booking booking) => new(
        booking.Id,
        booking.Items.Count > 0 ? booking.Items[0].NameSnapshot : string.Empty,
        booking.SlotDate,
        booking.TotalPayableSnapshot,
        booking.Status,
        BookingStatusMapper.LabelFor(booking.Status),
        booking.CreatedAtUtc,
        booking.BookingReference);

    /// <summary>
    /// Who is coming, for a booking that has a live assignment (task 275).
    /// Keyed off the assignment the caller already loaded rather than
    /// <see cref="Booking.AssignedProviderId"/>: the two agree today, but the
    /// assignment is the row that goes away on a reject/reassign, and the
    /// customer must stop seeing a provider the moment they are off the job.
    /// One extra read by primary key, and only when a provider is actually
    /// assigned - an unassigned booking's detail costs exactly what it did.
    /// Task 293 adds a second read, the rating aggregate, under the same
    /// condition and for the same reason.
    /// </summary>
    private async Task<BookingProviderSummary?> ProviderSummaryFor(BookingProviderAssignment? assignment)
    {
        if (assignment is null)
        {
            return null;
        }

        var provider = await _providerRepository.GetByIdAsync(assignment.ProviderId);
        if (provider is null)
        {
            return null;
        }

        var rating = await _reviewRepository.GetProviderRatingAsync(provider.Id);
        return BookingProviderSummary.From(provider, rating);
    }

    private static BookingDetailResponse ToDetailResponse(
        Booking booking,
        BookingProviderAssignmentStatus? providerAssignmentStatus,
        BookingProviderSummary? provider)
    {
        var item = booking.Items.Count > 0 ? booking.Items[0] : null;

        var addOns = item?.AddOns
            .Select(a => new Application.Catalog.ServiceAddOnSummaryResponse(a.ServiceAddOnId, a.NameSnapshot, null, a.UnitPriceSnapshot))
            .ToList()
            ?? [];

        return new BookingDetailResponse(
            booking.Id,
            new BookingServiceSummary(
                item?.ServiceId ?? Guid.Empty, item?.NameSnapshot ?? string.Empty, item?.SlugSnapshot ?? string.Empty,
                item?.ServiceVariantId, item?.VariantNameSnapshot, item?.VariantDurationMinutesSnapshot,
                item?.ServiceGroupId, item?.ServiceGroupNameSnapshot,
                booking.ServiceDurationMinutesSnapshot, booking.IsDurationBasedSnapshot),
            addOns,
            new BookingAddressSummary(
                booking.SourceAddressId ?? Guid.Empty, booking.AddressLabelSnapshot, booking.AddressLine1Snapshot,
                booking.AddressLine2Snapshot, booking.AddressLandmarkSnapshot, booking.AddressPincodeSnapshot,
                booking.AddressCitySnapshot, booking.AddressStateSnapshot, booking.AddressLatitudeSnapshot,
                booking.AddressLongitudeSnapshot, booking.AddressContactNameSnapshot, booking.AddressContactMobileSnapshot),
            new BookingSlotSummary(booking.SlotWindowId, booking.SlotWindowNameSnapshot, booking.SlotDate, booking.SlotStartTimeSnapshot, booking.SlotEndTimeSnapshot),
            new Application.Pricing.PriceBreakdownResponse(
                booking.BasePriceSnapshot, booking.QuantitySnapshot, booking.BaseTotalSnapshot,
                item?.AddOns.Select(a => new Application.Pricing.AddOnLineItem(
                    a.ServiceAddOnId, a.NameSnapshot, a.UnitPriceSnapshot, a.Quantity, a.LineTotalSnapshot, a.AddOnGroupId, a.GroupNameSnapshot)).ToList() ?? [],
                booking.AddOnTotalSnapshot, booking.VisitChargeSnapshot, booking.SubtotalSnapshot,
                booking.TaxPercentageSnapshot, booking.TaxAmountSnapshot, booking.PlatformFeeSnapshot, booking.TotalPayableSnapshot,
                item?.ServiceVariantId, item?.VariantNameSnapshot, item?.VariantDurationMinutesSnapshot),
            booking.Status,
            BookingStatusMapper.LabelFor(booking.Status),
            booking.StatusHistory
                .OrderBy(h => h.ChangedAtUtc)
                .Select(h => new BookingStatusTimelineEntry(h.FromStatus, h.ToStatus, BookingStatusMapper.LabelFor(h.ToStatus), h.Reason, h.ChangedAtUtc))
                .ToList(),
            booking.CreatedAtUtc,
            booking.CouponCodeSnapshot,
            booking.CouponDiscountAmountSnapshot,
            booking.TotalPayableSnapshot,
            providerAssignmentStatus,
            provider,
            booking.WalletCreditAppliedSnapshot,
            booking.BookingReference);
    }
}
