using Nestly.Domain;

namespace Nestly.Application.RecurringBookings;

/// <summary>
/// The Hangfire-invoked recurring job (task 185). Called on a daily
/// recurring schedule (see <c>BackgroundJobRegistration</c>) - not enqueued
/// per-plan, since a single sweep over every due plan is simpler to reason
/// about and matches this codebase's existing job style (<c>ExportJobService.ProcessAsync</c>
/// re-reads current state from the database rather than trusting anything
/// captured at enqueue time).
/// </summary>
public interface IRecurringBookingSchedulerService
{
    /// <summary>
    /// For every active plan due within the configured lead time: attempts a
    /// real booking through <c>IBookingService.CreateAsync</c>; on success
    /// advances the plan and notifies the customer of the upcoming visit; on
    /// failure (slot no longer available, or any other orchestration
    /// rejection) skips the occurrence, advances the plan without charging
    /// it against <c>OccurrenceCount</c>, and notifies the customer instead
    /// of failing silently. Safe to re-run (Hangfire's global retry policy
    /// expects idempotent jobs) - already-processed dates are skipped via
    /// <see cref="IRecurringBookingOccurrenceRepository.ExistsForDateAsync"/>.
    /// </summary>
    Task ProcessDueOccurrencesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates every booking of a prepaid plan's current cycle right now - all
    /// at once, instead of the daily job's lead-time trickle - so the customer
    /// can pay for the whole cycle in one checkout. Uses the same booking
    /// orchestration as the daily job and records the same occurrence rows, but
    /// sends no per-visit notifications (the customer is on the payment page,
    /// which lists what was and was not booked). A date that cannot be booked
    /// is skipped and reported, never charged for.
    /// </summary>
    /// <param name="plan">A prepaid plan that has not materialised this cycle yet.</param>
    /// <param name="coveredThroughDate">Last date to create a booking for; null means "all remaining occurrences" (a bounded plan).</param>
    Task<PrepaidCycleMaterialization> MaterializePrepaidCycleAsync(
        RecurringBookingPlan plan, DateOnly? coveredThroughDate, CancellationToken cancellationToken);

    /// <summary>
    /// For every open-ended prepaid plan whose paid cycle is about to run out:
    /// creates the next cycle's bookings and asks the customer to pay for them
    /// (see <c>RecurringBookingOptions.PrepaidRenewalLeadDays</c>). Idempotent -
    /// a plan already waiting on a renewal payment is left alone.
    /// </summary>
    Task ProcessPrepaidRenewalsAsync(CancellationToken cancellationToken);
}

/// <summary>What one <see cref="IRecurringBookingSchedulerService.MaterializePrepaidCycleAsync"/> call produced.</summary>
/// <param name="BookedCount">Bookings created, awaiting payment.</param>
/// <param name="SkippedDates">Dates that could not be booked; the customer is not charged for them.</param>
/// <param name="FirstBookingId">The earliest booking created, or null when nothing could be booked.</param>
/// <param name="FirstVisitDate">The slot date of <paramref name="FirstBookingId"/>.</param>
/// <param name="TotalPayable">Sum of what the created bookings still need paid.</param>
public sealed record PrepaidCycleMaterialization(
    int BookedCount, IReadOnlyList<DateOnly> SkippedDates, Guid? FirstBookingId, DateOnly? FirstVisitDate, decimal TotalPayable);
