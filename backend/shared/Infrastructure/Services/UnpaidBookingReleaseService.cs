using Nestly.Application.Bookings;
using Nestly.Application.Coupons;
using Nestly.Application.Slots;
using Nestly.Application.Wallet;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>See <see cref="IUnpaidBookingReleaseService"/>.</summary>
public class UnpaidBookingReleaseService : IUnpaidBookingReleaseService
{
    private readonly IBookingRepository _bookingRepository;
    private readonly ISlotAvailabilityService _slotAvailabilityService;
    private readonly IWalletService _walletService;
    private readonly ICouponService _couponService;

    public UnpaidBookingReleaseService(
        IBookingRepository bookingRepository,
        ISlotAvailabilityService slotAvailabilityService,
        IWalletService walletService,
        ICouponService couponService)
    {
        _bookingRepository = bookingRepository;
        _slotAvailabilityService = slotAvailabilityService;
        _walletService = walletService;
        _couponService = couponService;
    }

    public async Task ExpireAsync(Booking booking, string reason)
    {
        booking.TransitionTo(BookingStatus.Expired, reason);
        await _bookingRepository.UpdateAsync(booking);

        // Hand the slot's seat back to the pool, same as
        // CancellationService.ExecuteCancellationAsync - the reservation was
        // taken when the booking was created (BookingService.CreateAsync) and
        // nothing else ever releases it for an abandoned PaymentPending booking.
        await _slotAvailabilityService.ReleaseSlotAsync(booking.SlotWindowId, booking.SlotDate);

        // Same reasoning for the other two things BookingService.CreateAsync
        // reserves atomically alongside the slot: wallet balance debited at
        // checkout (task 310) and a coupon redemption (task 72a-d). Neither is
        // refunded/released for an abandoned PaymentPending booking unless done
        // here - the customer would permanently lose real wallet balance, and a
        // single-use coupon would be burned, for an order that never happened.
        if (booking.WalletCreditAppliedSnapshot is { } walletAmount && walletAmount > 0)
        {
            await _walletService.CreditAsync(
                booking.CustomerId, walletAmount, WalletSourceType.BookingWalletCreditReversal, booking.Id,
                "Wallet credit reversed - booking expired unpaid");
        }

        await _couponService.ReleaseAsync(booking.Id);
    }
}
