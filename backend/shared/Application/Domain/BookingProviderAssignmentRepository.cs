using Nestly.Domain;

namespace Nestly.Application;

/// <summary>Persistence for <see cref="BookingProviderAssignment"/> (task 147).</summary>
public interface IBookingProviderAssignmentRepository
{
    Task AddAsync(BookingProviderAssignment entity);
    Task UpdateAsync(BookingProviderAssignment entity);
    Task<BookingProviderAssignment?> GetByIdAsync(Guid id);

    /// <summary>The currently outstanding assignment for a booking (status Assigned or Accepted), or null if none - PROVIDER.md OPEN DECISIONS #5, only one row is ever "live" at a time.</summary>
    Task<BookingProviderAssignment?> GetActiveByBookingAsync(Guid bookingId);

    /// <summary>Full assignment history for a booking, newest first (task 159 - shows prior rejections leading to the current state).</summary>
    Task<IReadOnlyList<BookingProviderAssignment>> ListByBookingAsync(Guid bookingId);

    /// <summary>Every assignment ever made to a provider, across every booking, newest first (task 149a - the provider's own "my jobs" list, unlike <c>IBookingRepository.ListByAssignedProviderAsync</c> this includes rejected/superseded rows too).</summary>
    Task<IReadOnlyList<BookingProviderAssignment>> ListByProviderAsync(Guid providerId);
}
