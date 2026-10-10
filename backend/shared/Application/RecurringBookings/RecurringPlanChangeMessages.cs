using System.Globalization;
using Nestly.Domain;

namespace Nestly.Application.RecurringBookings;

/// <summary>The wording of a plan-change confirmation: a title, a short summary (SMS, push) and the full text (e-mail).</summary>
public sealed record RecurringPlanChangeMessage(string Title, string Summary, string Details);

/// <summary>
/// Builds what a customer is told after changing their own recurring plan. Pure - facts in, words out - so every
/// sentence a customer can receive is covered by a plain unit test instead of being scattered through the service that
/// performs the action.
///
/// <para>
/// What each message is careful to say, because it is what a customer wrongly assumes otherwise: pausing, skipping and
/// changing the time cost nothing themselves; visits that are <i>already booked</i> are not affected by a pause or a
/// time change and are still charged unless cancelled; and cancelling an already-booked visit follows the usual
/// cancellation policy (a fee close to the visit). Where an action did cancel visits, the real count, fees and refund
/// are stated.
/// </para>
/// </summary>
public static class RecurringPlanChangeMessages
{
    public static RecurringPlanChangeMessage Build(RecurringBookingPlan plan, string windowLabel, RecurringPlanChange change) =>
        change.Kind switch
        {
            RecurringPlanChangeKind.Paused => Paused(change),
            RecurringPlanChangeKind.Resumed => Resumed(plan, windowLabel),
            RecurringPlanChangeKind.VisitsSkipped => Skipped(plan, change),
            RecurringPlanChangeKind.TimeChanged => TimeChanged(plan, windowLabel, change),
            RecurringPlanChangeKind.Cancelled => Cancelled(plan, change),
            RecurringPlanChangeKind.PausedBySupport => PausedBySupport(change),
            RecurringPlanChangeKind.ResumedBySupport => ResumedBySupport(plan, windowLabel),
            RecurringPlanChangeKind.CancelledBySupport => CancelledBySupport(plan, change),
            _ => throw new ArgumentOutOfRangeException(nameof(change), change.Kind, "Unknown plan change.")
        };

    private static RecurringPlanChangeMessage Paused(RecurringPlanChange change)
    {
        string alreadyBooked = change.BookedVisitsStillAhead > 0
            ? $"{Visits(change.BookedVisitsStillAhead)} already booked{Next(change.NextBookedVisitDate)} {GoAheadAndCharged(change.BookedVisitsStillAhead)}"
            : string.Empty;

        string summary = change.BookedVisitsStillAhead > 0
            ? $"No new visits will be booked. {Capitalise(alreadyBooked)} Pausing itself is free."
            : "No new visits will be booked until you resume. Pausing is free.";

        string details = "You paused this plan. No new visits will be booked while it is paused, and pausing is free."
            + (change.BookedVisitsStillAhead > 0
                ? $"\n\n{Capitalise(alreadyBooked)} To stop one, cancel it from My bookings - the usual cancellation policy applies, so a visit cancelled shortly before it starts can carry a fee."
                : string.Empty)
            + "\n\nResume any time from Recurring bookings and the plan carries on from your next visit.";

        return new("Plan paused", summary, details);
    }

    private static RecurringPlanChangeMessage PausedBySupport(RecurringPlanChange change)
    {
        string alreadyBooked = change.BookedVisitsStillAhead > 0
            ? $"{Visits(change.BookedVisitsStillAhead)} already booked{Next(change.NextBookedVisitDate)} {GoAheadAndCharged(change.BookedVisitsStillAhead)}"
            : string.Empty;

        string summary = change.BookedVisitsStillAhead > 0
            ? $"Our support team paused this plan, so no new visits will be booked. {Capitalise(alreadyBooked)} Contact support to resume it."
            : "Our support team paused this plan, so no new visits will be booked. Contact support to resume it.";

        string details = "Our support team paused this plan. No new visits will be booked while it is paused."
            + (change.BookedVisitsStillAhead > 0
                ? $"\n\n{Capitalise(alreadyBooked)} To stop one, cancel it from My bookings - the usual cancellation policy applies, so a visit cancelled shortly before it starts can carry a fee."
                : string.Empty)
            + "\n\nYou cannot resume it yourself because support paused it - contact support and we will get it running again.";

        return new("Plan paused by support", summary, details);
    }

    private static RecurringPlanChangeMessage ResumedBySupport(RecurringBookingPlan plan, string windowLabel)
    {
        string next = $"Next visit {Day(plan.NextOccurrenceDate)}{Window(windowLabel)}.";
        string payment = PaymentSentence(plan);

        string summary = $"Our support team resumed your plan. {next} {payment}";
        string details = $"Our support team resumed your plan, so it is running again. {next} {payment}"
            + (plan.PrepaidUpfront
                ? string.Empty
                : "\n\nA visit that isn't paid in time is released and no professional is sent for it.");

        return new("Plan resumed by support", summary, details);
    }

    private static RecurringPlanChangeMessage CancelledBySupport(RecurringBookingPlan plan, RecurringPlanChange change)
    {
        string stillAhead = change.BookedVisitsStillAhead > 0
            ? $"{Visits(change.BookedVisitsStillAhead)} already booked{Next(change.NextBookedVisitDate)} {GoAheadAndCharged(change.BookedVisitsStillAhead, " from My bookings")}"
            : "There are no visits already booked.";

        string prepaid = plan.PrepaidUpfront
            ? " If you had paid for visits in advance, contact support about the ones that will not happen."
            : string.Empty;

        return new(
            "Plan cancelled by support",
            $"Our support team cancelled this plan, so no further visits will be booked. {stillAhead}{prepaid}",
            $"Our support team cancelled this plan. No further visits will be booked.\n\n{stillAhead} The usual cancellation policy applies to a visit you cancel, so one close to its start can carry a fee.{prepaid}");
    }

    private static RecurringPlanChangeMessage Resumed(RecurringBookingPlan plan, string windowLabel)
    {
        string next = $"Next visit {Day(plan.NextOccurrenceDate)}{Window(windowLabel)}.";
        string payment = PaymentSentence(plan);

        string summary = $"{next} {payment}";
        string details = $"Your plan is running again. {next} {payment}"
            + (plan.PrepaidUpfront
                ? string.Empty
                : "\n\nA visit that isn't paid in time is released and no professional is sent for it.");

        return new("Plan resumed", summary, details);
    }

    private static RecurringPlanChangeMessage Skipped(RecurringBookingPlan plan, RecurringPlanChange change)
    {
        string resumeOn = change.ResumeOn is { } r ? Day(r) : "the date you chose";
        string next = $"Your next visit is {Day(plan.NextOccurrenceDate)}.";

        string cancelled = change.VisitsCancelled > 0
            ? $"{Visits(change.VisitsCancelled)} already booked before then {(change.VisitsCancelled == 1 ? "was" : "were")} cancelled: "
                + Settlement(change.CancellationFees, change.Refunded, string.Empty)
            : string.Empty;

        string kept = change.BookedVisitsStillAhead > 0
            ? $"{Visits(change.BookedVisitsStillAhead)} already booked before then {GoAheadAndCharged(change.BookedVisitsStillAhead, " from My bookings")}"
            : string.Empty;

        string summary = $"No visits before {resumeOn}. {next} {cancelled} {kept}".Replace("  ", " ").Trim();
        string details = $"No visits will be booked before {resumeOn}. {next} Skipping is free."
            + (cancelled.Length > 0 ? $"\n\n{cancelled}" : string.Empty)
            + (kept.Length > 0
                ? $"\n\n{kept} The usual cancellation policy applies to a visit you cancel, so one close to its start can carry a fee."
                : string.Empty);

        return new("Visits skipped", summary, details);
    }

    private static RecurringPlanChangeMessage TimeChanged(RecurringBookingPlan plan, string windowLabel, RecurringPlanChange change)
    {
        string applies = $"From {Day(plan.NextOccurrenceDate)} your visits are at {windowLabel}.";
        string kept = change.BookedVisitsStillAhead > 0
            ? $"{Visits(change.BookedVisitsStillAhead)} already booked keep{(change.BookedVisitsStillAhead == 1 ? "s" : string.Empty)} the old time."
            : "Visits already booked keep their time.";

        string summary = $"{applies} {kept} Changing the time is free.";
        string details = $"{applies} Changing the time is free."
            + $"\n\n{kept} To move one, reschedule it from My bookings - each visit follows the service's reschedule policy, and a late reschedule can carry a fee.";

        return new("Visit time changed", summary, details);
    }

    private static RecurringPlanChangeMessage Cancelled(RecurringBookingPlan plan, RecurringPlanChange change)
    {
        if (plan.PrepaidUpfront)
        {
            string outcome = change.VisitsCancelled == 0
                ? "There were no remaining visits to cancel."
                : $"{Visits(change.VisitsCancelled)} that had not happened {(change.VisitsCancelled == 1 ? "was" : "were")} cancelled: "
                    + Settlement(change.CancellationFees, change.Refunded, " to your original payment method");

            return new("Plan cancelled", outcome, $"You cancelled this plan. {outcome}\n\nThe booking you placed together with the plan is its own visit and was not cancelled.");
        }

        string stillAhead = change.BookedVisitsStillAhead > 0
            ? $"{Visits(change.BookedVisitsStillAhead)} already booked{Next(change.NextBookedVisitDate)} {GoAheadAndCharged(change.BookedVisitsStillAhead, " from My bookings")}"
            : "There are no visits already booked.";

        return new(
            "Plan cancelled",
            $"No further visits will be booked. {stillAhead}",
            $"You cancelled this plan. No further visits will be booked.\n\n{stillAhead} The usual cancellation policy applies to a visit you cancel, so one close to its start can carry a fee.");
    }

    /// <summary>
    /// "No fee was charged and ₹x is being refunded to ..." - fees and refund stated as they were, and silent about a
    /// refund when there was nothing paid to refund (an unpaid visit cancels for free and refunds nothing).
    /// </summary>
    private static string Settlement(decimal fees, decimal refunded, string refundDestination)
    {
        string feePart = fees > 0 ? $"{Money(fees)} was kept as cancellation fees" : "no fee was charged";
        return refunded > 0
            ? $"{feePart} and {Money(refunded)} is being refunded{refundDestination}."
            : $"{feePart}.";
    }

    private static string PaymentSentence(RecurringBookingPlan plan) =>
        plan.PrepaidUpfront
            ? "Your visits are paid for in advance."
            : plan.ApplyWalletCredit
                ? "Each visit is paid from your wallet as it is booked."
                : "Each visit is paid as it is booked.";

    /// <summary>"still goes ahead and is still charged unless you cancel it" - agreeing with one visit or several.</summary>
    private static string GoAheadAndCharged(int visits, string cancelWhere = "")
    {
        bool one = visits == 1;
        return $"still {(one ? "goes" : "go")} ahead and {(one ? "is" : "are")} still charged unless you cancel {(one ? "it" : "them")}{cancelWhere}.";
    }

    private static string Visits(int count) => count == 1 ? "1 visit" : $"{count} visits";

    private static string Next(DateOnly? date) => date is { } d ? $" (next: {Day(d)})" : string.Empty;

    private static string Window(string label) => string.IsNullOrWhiteSpace(label) ? string.Empty : $", {label}";

    private static string Day(DateOnly date) => date.ToString("ddd, d MMM yyyy", CultureInfo.InvariantCulture);

    private static string Money(decimal amount) => "₹" + amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
