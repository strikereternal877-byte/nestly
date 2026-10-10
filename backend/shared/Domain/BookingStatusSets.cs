namespace Nestly.Domain;

/// <summary>Named groups of <see cref="BookingStatus"/>, for rules that need "which bookings still count" and should not each keep their own list.</summary>
public static class BookingStatusSets
{
    /// <summary>
    /// A booking the customer is paid up on and that has been neither carried out nor called off. An unpaid one is left out:
    /// abandoned checkouts expire on their own, and counting them would lock a customer out of retrying a payment they just
    /// walked away from. What the Booking rules' "active bookings per customer" cap and the Coupon rules' "active coupons per
    /// customer" cap both count.
    /// </summary>
    public static readonly BookingStatus[] Committed =
    [
        BookingStatus.Confirmed, BookingStatus.AwaitingFulfilment, BookingStatus.Assigned, BookingStatus.Rescheduled,
        BookingStatus.ProviderEnRoute, BookingStatus.ProviderArrived, BookingStatus.InProgress
    ];
}
