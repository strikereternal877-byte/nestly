using Nestly.Domain;

namespace Nestly.Application.Reschedules;

public interface IRescheduleRepository
{
    Task AddAsync(BookingReschedule reschedule);

    Task<IReadOnlyList<BookingReschedule>> ListByBookingAsync(Guid bookingId);

    /// <summary>How many times this booking has already been rescheduled (task 82b count limit).</summary>
    Task<int> CountByBookingAsync(Guid bookingId);

    /// <summary>What the customer has actually paid in late-reschedule fees on this booking (<see cref="BookingReschedule.FeeCollectedAmount"/> summed).</summary>
    Task<decimal> SumCollectedFeesAsync(Guid bookingId);
}
