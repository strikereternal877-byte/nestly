using Nestly.Domain;

namespace Nestly.Application;

/// <summary>Persistence for <see cref="BookingProviderAssignment"/> (task 147).</summary>
public interface IBookingProviderAssignmentRepository
{
    Task AddAsync(BookingProviderAssignment entity);
    Task UpdateAsync(BookingProviderAssignment entity);
    Task<BookingProviderAssignment?> GetByIdAsync(Guid id);

    /// <summary>The currently outstanding assignment for a booking (status Assigned or Accepted), or null if none - PROVIDER.md OPEN DECISIONS #5, only one row is ever "live" at a time. Deliberately excludes <see cref="BookingProviderAssignmentStatus.Completed"/>: callers that withdraw/reassign/cancel the live assignment (cancellation, reschedule) must not touch a job that already finished.</summary>
    Task<BookingProviderAssignment?> GetActiveByBookingAsync(Guid bookingId);

    /// <summary>Same as <see cref="GetActiveByBookingAsync"/> but also returns a just-<see cref="BookingProviderAssignmentStatus.Completed"/> assignment - for read paths (a customer's/provider's own view of "who is/was on this job") where a finished job's assignment is still the right one to show, as opposed to write paths that must never act on a job that is already done.</summary>
    Task<BookingProviderAssignment?> GetCurrentByBookingAsync(Guid bookingId);

    /// <summary>Full assignment history for a booking, newest first (task 159 - shows prior rejections leading to the current state).</summary>
    Task<IReadOnlyList<BookingProviderAssignment>> ListByBookingAsync(Guid bookingId);

    /// <summary>Every assignment ever made to a provider, across every booking, newest first (task 149a - the provider's own "my jobs" list, unlike <c>IBookingRepository.ListByAssignedProviderAsync</c> this includes rejected/superseded rows too).</summary>
    Task<IReadOnlyList<BookingProviderAssignment>> ListByProviderAsync(Guid providerId);

    /// <summary>
    /// Still-<see cref="BookingProviderAssignmentStatus.Assigned"/> rows whose
    /// <see cref="BookingProviderAssignment.ResponseDeadline"/> has already
    /// passed with no response - the assignment-response-expiry sweep's
    /// candidate set. Excludes anything without a deadline at all (an admin's
    /// manual assignment with no deadline set is never auto-expired).
    /// </summary>
    Task<IReadOnlyList<BookingProviderAssignment>> ListUnansweredPastDeadlineAsync(DateTime nowUtc);

    /// <summary>
    /// Every assignment created on/after <paramref name="sinceUtc"/>, across
    /// every provider, in one round trip - the batched input to the
    /// performance-ranking rollup (docs/OPEN-FIXES-FEATURES.csv "Provider
    /// performance"). Grouped by <see cref="BookingProviderAssignment.ProviderId"/>
    /// in memory by the caller rather than here, so one query serves the
    /// whole ranking list instead of one query per provider.
    /// </summary>
    Task<IReadOnlyList<BookingProviderAssignment>> ListSinceAsync(DateTime sinceUtc);
}
