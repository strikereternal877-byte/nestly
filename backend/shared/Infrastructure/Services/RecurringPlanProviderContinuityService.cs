using Nestly.Application.Bookings;
using Nestly.Application.RecurringBookings;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>See <see cref="IRecurringPlanProviderContinuityService"/>.</summary>
public class RecurringPlanProviderContinuityService : IRecurringPlanProviderContinuityService
{
    /// <summary>
    /// Statuses whose assigned provider is no precedent for the next visit. A
    /// cancelled visit's provider may never have set foot in the customer's
    /// home, so treating them as "the regular cleaner" would invent a
    /// relationship that never existed; an Expired occurrence was never even
    /// paid for. Everything else - including a booking still awaiting its slot
    /// - counts, because the customer has been told who is coming and expects
    /// the same person next time.
    /// </summary>
    internal static readonly HashSet<BookingStatus> NonPrecedentStatuses =
    [
        BookingStatus.CancelledByCustomer,
        BookingStatus.CancelledByAdmin,
        BookingStatus.Expired,
        BookingStatus.PaymentFailed
    ];

    private readonly IBookingRepository _bookingRepository;

    public RecurringPlanProviderContinuityService(IBookingRepository bookingRepository) =>
        _bookingRepository = bookingRepository;

    public async Task<Guid?> FindStandingProviderAsync(Guid recurringBookingPlanId, Guid? excludingBookingId = null)
    {
        // Already ordered newest slot date first by the repository, which is
        // the order that matters here: "who served me last time", not "who
        // served me first".
        var bookings = await _bookingRepository.ListByRecurringPlanAsync(recurringBookingPlanId);

        return bookings
            .Where(b => b.Id != excludingBookingId)
            .Where(b => !NonPrecedentStatuses.Contains(b.Status))
            .Select(b => b.AssignedProviderId)
            .FirstOrDefault(id => id is not null);
    }
}
