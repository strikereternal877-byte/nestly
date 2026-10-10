using Nestly.Application.Bookings;
using Nestly.Application.Coupons;
using Nestly.Application.Settings;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Coupon validation and redemption (SRS 11.10, 14.2, tasks 72a-d, 73). Pure
/// per-coupon rules (validity window, discount + cap calculation) live on
/// <see cref="Coupon"/> itself; everything here is the I/O-dependent half -
/// reading usage counts and booking history from other aggregates - that a
/// domain entity has no business doing.
///
/// <para>
/// Once an admin has saved the Coupon group in Settings (<see cref="IPlatformRules"/>), two of its rules apply here: switching
/// coupons off refuses every code with <c>Coupon.Disabled</c>, and a cap on different coupons held across live bookings
/// refuses a further one with <c>Coupon.ActiveLimitReached</c>. (The cap is checked where the code is validated - which booking
/// creation re-runs - so two bookings placed at the same instant can exceed it by one.) The maximum percentage guards coupon
/// creation, in <c>CouponManagementService</c>; stacking is not applicable, since a booking takes one coupon.
/// </para>
/// </summary>
public class CouponService : ICouponService
{
    private readonly ICouponRepository _couponRepository;
    private readonly ICouponRedemptionRepository _redemptionRepository;
    private readonly IBookingRepository _bookingRepository;
    private readonly TimeProvider _timeProvider;
    private readonly IPlatformRules _platformRules;

    public CouponService(
        ICouponRepository couponRepository,
        ICouponRedemptionRepository redemptionRepository,
        IBookingRepository bookingRepository,
        TimeProvider timeProvider,
        IPlatformRules? platformRules = null)
    {
        _couponRepository = couponRepository;
        _redemptionRepository = redemptionRepository;
        _bookingRepository = bookingRepository;
        _timeProvider = timeProvider;
        _platformRules = platformRules ?? NoPlatformRules.Instance;
    }

    public async Task<Result<CouponSummaryResponse>> ValidateAsync(Guid customerId, string code, Guid categoryId, decimal orderAmount)
    {
        var couponRules = await _platformRules.GetCouponAsync();
        if (couponRules is { CouponsEnabled: false })
        {
            return Error.Business("Coupon.Disabled", "Coupons aren't available right now.");
        }

        var coupon = await _couponRepository.GetByCodeAsync(code);
        if (coupon is null)
        {
            return Error.NotFound("Coupon.NotFound", "This coupon code does not exist.");
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        if (!coupon.IsWithinValidityWindow(nowUtc))
        {
            return Error.Business("Coupon.NotActive", "This coupon is not currently active or has expired.");
        }

        if (coupon.ApplicableCategoryId is not null && coupon.ApplicableCategoryId != categoryId)
        {
            return Error.Business("Coupon.CategoryNotApplicable", "This coupon does not apply to the selected service's category.");
        }

        if (coupon.RestrictedToCustomerId is not null && coupon.RestrictedToCustomerId != customerId)
        {
            return Error.Business("Coupon.NotApplicableToCustomer", "This coupon is not valid for your account.");
        }

        if (coupon.CustomerSegment != CouponCustomerSegment.All)
        {
            bool isFirstBooking = await IsFirstBookingCustomerAsync(customerId);

            if (coupon.CustomerSegment == CouponCustomerSegment.FirstBookingOnly && !isFirstBooking)
            {
                return Error.Business("Coupon.FirstBookingOnly", "This coupon is only valid on a customer's first booking.");
            }

            if (coupon.CustomerSegment == CouponCustomerSegment.RepeatBookingOnly && isFirstBooking)
            {
                return Error.Business("Coupon.RepeatBookingOnly", "This coupon is only valid for repeat customers.");
            }
        }

        if (coupon.UsageLimitTotal is not null && coupon.RedemptionCount >= coupon.UsageLimitTotal)
        {
            return Error.Conflict("Coupon.UsageLimitReached", "This coupon has reached its overall usage limit.");
        }

        // Preview only - fast, friendly feedback in the checkout UI before a
        // booking is attempted. Not the enforcement boundary: this read has
        // no transaction or lock tying it to the reservation that happens
        // later in ReserveAsync, so it cannot by itself stop two concurrent
        // bookings from both seeing "not yet used" (NESTLY-009). The real
        // guard against that is the atomic per-customer check inside
        // ICouponRepository.TryReserveRedemptionAsync, run again immediately
        // before the booking is created.
        if (coupon.UsageLimitPerCustomer is not null)
        {
            int usedByCustomer = await _redemptionRepository.CountByCouponAndCustomerAsync(coupon.Id, customerId);
            if (usedByCustomer >= coupon.UsageLimitPerCustomer)
            {
                return Error.Business("Coupon.AlreadyUsedByCustomer", "You have already used this coupon the maximum number of times.");
            }
        }

        if (couponRules?.MaxActiveCouponsPerCustomer is { } maxActive
            && await _redemptionRepository.CountDistinctOnLiveBookingsAsync(customerId, coupon.Id) >= maxActive)
        {
            return Error.Business(
                "Coupon.ActiveLimitReached",
                $"You can use up to {maxActive} different coupon{(maxActive == 1 ? "" : "s")} across your upcoming bookings. " +
                "Once one of those is done or cancelled you can apply another.");
        }

        if (!coupon.TryCalculateDiscount(orderAmount, out decimal discountAmount))
        {
            return Error.Validation("Coupon.MinOrderAmountNotMet", $"This coupon requires a minimum order amount of {coupon.MinOrderAmount}.");
        }

        return Result.Success(new CouponSummaryResponse(coupon.Id, coupon.Code, coupon.Description, discountAmount));
    }

    public async Task<Result> ReserveAsync(Guid couponId, Guid customerId)
    {
        bool reserved = await _couponRepository.TryReserveRedemptionAsync(couponId, customerId);
        return reserved
            ? Result.Success()
            : Result.Failure(Error.Conflict("Coupon.UsageLimitReached", "This coupon can no longer be redeemed - its usage limit has been reached."));
    }

    public Task CreateRedemptionRecordAsync(Guid couponId, Guid customerId, Guid bookingId, decimal discountAmount) =>
        _redemptionRepository.AddAsync(new CouponRedemption(Guid.NewGuid(), couponId, customerId, bookingId, discountAmount));

    public async Task ReleaseAsync(Guid bookingId)
    {
        var redemption = await _redemptionRepository.GetByBookingIdAsync(bookingId);
        if (redemption is null)
        {
            return;
        }

        await _couponRepository.ReleaseRedemptionAsync(redemption.CouponId, redemption.CustomerId);
        await _redemptionRepository.DeleteByBookingIdAsync(bookingId);
    }

    /// <summary>
    /// "First booking" (SRS 11.10.2) is defined as: no prior booking ever
    /// progressed past Initiated (an abandoned, never-paid-for cart doesn't
    /// count as an order). Reuses ListByCustomerAsync rather than adding a
    /// dedicated count query - booking volume per customer is small enough
    /// that loading the (small) list is not a meaningful cost in this phase.
    /// </summary>
    private async Task<bool> IsFirstBookingCustomerAsync(Guid customerId)
    {
        var pastStatuses = Enum.GetValues<BookingStatus>().Where(s => s != BookingStatus.Initiated).ToList();
        var bookings = await _bookingRepository.ListByCustomerAsync(customerId, pastStatuses);
        return bookings.Count == 0;
    }
}
