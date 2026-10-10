using Nestly.Domain;

namespace Nestly.Application.RecurringBookings;

/// <summary>The kinds of change a customer can make to their own recurring plan that are confirmed back to them.</summary>
public enum RecurringPlanChangeKind
{
    Paused,
    Resumed,
    VisitsSkipped,
    TimeChanged,
    Cancelled,

    // Done by an admin (support) on the customer's behalf - worded as "our support team", not "you".
    PausedBySupport,
    ResumedBySupport,
    CancelledBySupport
}

/// <summary>
/// The facts of one customer-made change to a plan - everything the confirmation has to say that is not already on the
/// plan itself. Counts and money are what the action actually did (visits cancelled, fees kept, amount refunded), not
/// what a screen predicted.
/// </summary>
/// <param name="Kind">Which action it was.</param>
/// <param name="BookedVisitsStillAhead">Visits already booked that are still going ahead after the change (and so still charged unless cancelled).</param>
/// <param name="NextBookedVisitDate">The earliest of those.</param>
/// <param name="ResumeOn">For skipped visits: the date the customer asked visits to resume on.</param>
/// <param name="VisitsCancelled">Visits the action cancelled for the customer.</param>
/// <param name="CancellationFees">Cancellation fees kept across those visits.</param>
/// <param name="Refunded">Amount refunded across those visits.</param>
public sealed record RecurringPlanChange(
    RecurringPlanChangeKind Kind,
    int BookedVisitsStillAhead = 0,
    DateOnly? NextBookedVisitDate = null,
    DateOnly? ResumeOn = null,
    int VisitsCancelled = 0,
    decimal CancellationFees = 0m,
    decimal Refunded = 0m);

/// <summary>
/// Confirms to the customer, by SMS / e-mail / push, a change they just made to their own recurring plan. The on-screen
/// result is gone the moment they navigate away and is invisible to anyone else on the account; this is the record.
///
/// <para>
/// Best effort by design: the change has already happened and is what matters, so a notification that cannot be sent
/// is logged and never fails or undoes it.
/// </para>
/// </summary>
public interface IRecurringPlanNotifier
{
    Task NotifyChangedAsync(RecurringBookingPlan plan, RecurringPlanChange change, CancellationToken cancellationToken = default);
}
