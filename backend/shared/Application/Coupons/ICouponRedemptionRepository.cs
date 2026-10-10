using Nestly.Domain;

namespace Nestly.Application.Coupons;

public interface ICouponRedemptionRepository
{
    Task AddAsync(CouponRedemption redemption);

    /// <summary>Per-customer usage count for a coupon (task 72c per-customer cap).</summary>
    Task<int> CountByCouponAndCustomerAsync(Guid couponId, Guid customerId);

    /// <summary>Every redemption a customer has made, most recent first (SRS 12.4.2 "Coupons used", task 101b).</summary>
    Task<IReadOnlyList<CouponRedemption>> ListByCustomerAsync(Guid customerId);

    /// <summary>The redemption tied to this booking, if any - a booking carries at most one (unique index on BookingId). Null for the common case of a booking that never used a coupon.</summary>
    Task<CouponRedemption?> GetByBookingIdAsync(Guid bookingId);

    /// <summary>
    /// Removes the redemption tied to this booking - the booking never
    /// actually completed (expired unpaid, or was cancelled before payment
    /// ever settled), so this row would otherwise report a discount that was
    /// never really given and permanently occupy the customer's coupons-used
    /// history for an order that never happened. No-op if this booking never
    /// used a coupon.
    /// </summary>
    Task DeleteByBookingIdAsync(Guid bookingId);

    /// <summary>
    /// How many <i>different</i> coupons the customer is holding on bookings that are still live
    /// (<see cref="BookingStatusSets.Committed"/>), not counting <paramref name="exceptCouponId"/> - the one being checked,
    /// which adds nothing new if they already hold it.
    /// </summary>
    Task<int> CountDistinctOnLiveBookingsAsync(Guid customerId, Guid exceptCouponId);
}
