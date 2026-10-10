using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.Coupons;

/// <summary>
/// Coupon validation and redemption (SRS 11.10, 14.2, tasks 72a-d, 73).
/// Split into a read-only preview (<see cref="ValidateAsync"/>, safe to call
/// as many times as the customer edits their cart/checkout) and a two-step
/// commit used only once, at booking creation:
/// <see cref="ReserveAsync"/> atomically consumes one unit of both the
/// coupon's global usage cap and the calling customer's per-customer cap
/// before the booking exists (so neither cap can ever be exceeded, and a
/// failed reservation never creates a booking), and
/// <see cref="CreateRedemptionRecordAsync"/> links that reservation to the
/// booking once it has actually been persisted (a <see cref="Domain.CouponRedemption"/>
/// row has a foreign key to the booking, so it cannot be inserted first).
/// </summary>
public interface ICouponService
{
    /// <summary>
    /// Validates <paramref name="code"/> against every applicability rule
    /// (validity window, min order amount + cap, category, first/repeat
    /// booking segment, both usage caps) and computes the discount for
    /// <paramref name="orderAmount"/> without reserving anything. Used by
    /// the booking summary/preview and the coupon apply/remove endpoints.
    /// </summary>
    Task<Result<CouponSummaryResponse>> ValidateAsync(Guid customerId, string code, Guid categoryId, decimal orderAmount);

    /// <summary>Atomically reserves one redemption against both the coupon's global cap and <paramref name="customerId"/>'s per-customer cap. Call only immediately before creating the booking that will use it.</summary>
    Task<Result> ReserveAsync(Guid couponId, Guid customerId);

    Task CreateRedemptionRecordAsync(Guid couponId, Guid customerId, Guid bookingId, decimal discountAmount);

    /// <summary>
    /// Undoes <see cref="ReserveAsync"/> + <see cref="CreateRedemptionRecordAsync"/>
    /// for a booking that never actually completed (expired unpaid, or was
    /// cancelled before payment ever settled) - releases both the overall
    /// and per-customer usage counters and removes the redemption record, so
    /// the coupon usage this booking would have consumed is genuinely given
    /// back rather than permanently burned on an order that never happened.
    /// No-op if this booking never used a coupon.
    /// </summary>
    Task ReleaseAsync(Guid bookingId);
}
