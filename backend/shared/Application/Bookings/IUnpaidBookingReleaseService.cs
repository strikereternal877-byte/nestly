using Nestly.Domain;

namespace Nestly.Application.Bookings;

/// <summary>
/// Gives back everything a booking reserved at creation when it will never be
/// paid for. The single implementation of what an abandoned
/// <see cref="BookingStatus.PaymentPending"/> booking must undo, shared by the
/// expiry sweep (an unpaid checkout timing out) and the prepaid-plan flow (a
/// visit that cannot be staffed is dropped from the purchase, or the whole
/// unpaid purchase is abandoned).
/// </summary>
public interface IUnpaidBookingReleaseService
{
    /// <summary>
    /// Moves a <see cref="BookingStatus.PaymentPending"/> booking to
    /// <see cref="BookingStatus.Expired"/> and releases its slot seat, any wallet
    /// balance it consumed, and its coupon redemption. Persists the booking.
    /// </summary>
    Task ExpireAsync(Booking booking, string reason);
}
