using Nestly.Application;
using Nestly.Application.Abstractions.Observability;
using Nestly.Application.Bookings;
using Nestly.Application.Coupons;
using Nestly.Application.Slots;
using Nestly.Application.Subscriptions;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>Booking creation and reads (SRS 13, tasks 58-61).</summary>
public class BookingService : IBookingService
{
    /// <summary>Recorded on the auto-transition to PaymentPending, since there is no real payment gateway integration yet to explain it instead (Phase 4).</summary>
    private const string NoPaymentGatewayReason = "No payment gateway integrated yet - booking moves directly to awaiting payment.";

    /// <summary>Task 137b: the specific error code SlotAvailabilityService.ReserveSlotAsync returns when a slot has no remaining per-day capacity.</summary>
    private const string SlotCapacityReachedErrorCode = "Booking.SlotCapacityReached";

    private readonly IBookingSummaryService _summaryService;
    private readonly IBookingRepository _bookingRepository;
    private readonly ICustomerRepository _customerRepository;
    private readonly ICouponService _couponService;
    private readonly ISlotAvailabilityService _slotAvailabilityService;
    private readonly IMetricsService _metricsService;
    private readonly IBookingProviderAssignmentRepository _assignmentRepository;
    private readonly ICustomerSubscriptionRepository _customerSubscriptionRepository;

    public BookingService(
        IBookingSummaryService summaryService,
        IBookingRepository bookingRepository,
        ICustomerRepository customerRepository,
        ICouponService couponService,
        ISlotAvailabilityService slotAvailabilityService,
        IMetricsService metricsService,
        IBookingProviderAssignmentRepository assignmentRepository,
        ICustomerSubscriptionRepository customerSubscriptionRepository)
    {
        _summaryService = summaryService;
        _bookingRepository = bookingRepository;
        _customerRepository = customerRepository;
        _couponService = couponService;
        _slotAvailabilityService = slotAvailabilityService;
        _metricsService = metricsService;
        _assignmentRepository = assignmentRepository;
        _customerSubscriptionRepository = customerSubscriptionRepository;
    }

    public async Task<Result<BookingDetailResponse>> CreateAsync(Guid customerId, BookingSummaryRequest request)
    {
        // Re-validates every precondition (58a-f) through the same code path
        // the preview uses, so creation can never succeed on a combination
        // the preview would have rejected.
        var summaryResult = await _summaryService.GetSummaryAsync(customerId, request);
        if (summaryResult.IsFailure)
        {
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
        // available.
        if (summary.Coupon is not null)
        {
            var reserveResult = await _couponService.ReserveAsync(summary.Coupon.CouponId);
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
        if (summary.SubscriptionBenefit is { FreeVisitApplied: true } benefit)
        {
            bool consumed = await _customerSubscriptionRepository.TryConsumeFreeVisitAsync(benefit.SubscriptionId);
            if (!consumed)
            {
                const string errorCode = "Subscription.FreeVisitNoLongerAvailable";
                _metricsService.RecordBookingCreated(succeeded: false, errorCode);
                return Error.Conflict(errorCode, "Your subscription's free visit is no longer available. Please retry.");
            }
        }

        var booking = new Booking(
            Guid.NewGuid(),
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
                summary.Price.TaxAmount, summary.Price.PlatformFee, summary.FinalPayable),
            summary.Coupon?.Code,
            summary.Coupon?.DiscountAmount,
            summary.SubscriptionBenefit?.SubscriptionId,
            summary.SubscriptionBenefit?.FreeVisitApplied ?? false,
            summary.SubscriptionBenefit?.DiscountAmount);

        // Add-on line items come from the price breakdown, not summary.AddOns:
        // the breakdown already carries each selection's quantity and
        // resolved unit price, exactly what the snapshot needs, whereas
        // summary.AddOns is a plain catalog projection for display.
        var item = booking.AddItem(
            Guid.NewGuid(), summary.Service.Id, summary.Service.Name, summary.Service.Slug,
            summary.Price.BasePrice, summary.Price.Quantity);

        foreach (var addOnLine in summary.Price.AddOnLineItems)
        {
            booking.AddAddOnToItem(item.Id, Guid.NewGuid(), addOnLine.AddOnId, addOnLine.Name, addOnLine.UnitPrice, addOnLine.Quantity);
        }

        booking.TransitionTo(BookingStatus.PaymentPending, NoPaymentGatewayReason);

        await _bookingRepository.AddAsync(booking);

        if (summary.Coupon is not null)
        {
            await _couponService.CreateRedemptionRecordAsync(summary.Coupon.CouponId, customerId, booking.Id, summary.Coupon.DiscountAmount);
        }

        _metricsService.RecordBookingCreated(succeeded: true);
        return Result.Success(ToDetailResponse(booking, providerAssignmentStatus: null));
    }

    public async Task<Result<IReadOnlyList<BookingListItemResponse>>> ListAsync(Guid customerId, BookingStatusBucket? bucket)
    {
        var statuses = bucket is null
            ? Enum.GetValues<BookingStatus>()
            : BookingStatusMapper.StatusesInBucket(bucket.Value);

        var bookings = await _bookingRepository.ListByCustomerAsync(customerId, statuses);

        IReadOnlyList<BookingListItemResponse> response = bookings.Select(ToListItem).ToList();
        return Result.Success(response);
    }

    public async Task<Result<BookingDetailResponse>> GetDetailAsync(Guid customerId, Guid bookingId)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null || booking.CustomerId != customerId)
        {
            return Error.NotFound("Booking.NotFound", "The specified booking does not exist.");
        }

        var activeAssignment = await _assignmentRepository.GetActiveByBookingAsync(bookingId);
        return Result.Success(ToDetailResponse(booking, activeAssignment?.Status));
    }

    private static BookingListItemResponse ToListItem(Booking booking) => new(
        booking.Id,
        booking.Items.Count > 0 ? booking.Items[0].NameSnapshot : string.Empty,
        booking.SlotDate,
        booking.TotalPayableSnapshot,
        booking.Status,
        BookingStatusMapper.LabelFor(booking.Status),
        booking.CreatedAtUtc);

    private static BookingDetailResponse ToDetailResponse(Booking booking, BookingProviderAssignmentStatus? providerAssignmentStatus)
    {
        var item = booking.Items.Count > 0 ? booking.Items[0] : null;

        var addOns = item?.AddOns
            .Select(a => new Application.Catalog.ServiceAddOnSummaryResponse(a.ServiceAddOnId, a.NameSnapshot, null, a.UnitPriceSnapshot))
            .ToList()
            ?? [];

        return new BookingDetailResponse(
            booking.Id,
            new BookingServiceSummary(item?.ServiceId ?? Guid.Empty, item?.NameSnapshot ?? string.Empty, item?.SlugSnapshot ?? string.Empty),
            addOns,
            new BookingAddressSummary(
                booking.SourceAddressId ?? Guid.Empty, booking.AddressLabelSnapshot, booking.AddressLine1Snapshot,
                booking.AddressLine2Snapshot, booking.AddressLandmarkSnapshot, booking.AddressPincodeSnapshot,
                booking.AddressCitySnapshot, booking.AddressStateSnapshot, booking.AddressLatitudeSnapshot,
                booking.AddressLongitudeSnapshot, booking.AddressContactNameSnapshot, booking.AddressContactMobileSnapshot),
            new BookingSlotSummary(booking.SlotWindowId, booking.SlotWindowNameSnapshot, booking.SlotDate, booking.SlotStartTimeSnapshot, booking.SlotEndTimeSnapshot),
            new Application.Pricing.PriceBreakdownResponse(
                booking.BasePriceSnapshot, booking.QuantitySnapshot, booking.BaseTotalSnapshot,
                item?.AddOns.Select(a => new Application.Pricing.AddOnLineItem(a.ServiceAddOnId, a.NameSnapshot, a.UnitPriceSnapshot, a.Quantity, a.LineTotalSnapshot)).ToList() ?? [],
                booking.AddOnTotalSnapshot, booking.VisitChargeSnapshot, booking.SubtotalSnapshot,
                booking.TaxPercentageSnapshot, booking.TaxAmountSnapshot, booking.PlatformFeeSnapshot, booking.TotalPayableSnapshot),
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
            providerAssignmentStatus);
    }
}
