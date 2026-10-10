namespace Nestly.Domain;

/// <summary>
/// Why a <see cref="RecurringBookingPlan"/> is <see cref="RecurringBookingPlanStatus.Paused"/> -
/// so the customer is told the truth about it ("you paused this" vs "we paused this because the
/// last visits went unpaid") and so the right guidance is shown for getting it going again.
/// Stored as a string (max length 20); append-only like the other enums stored that way.
/// </summary>
public enum RecurringBookingPauseReason
{
    /// <summary>The customer paused it themselves.</summary>
    Customer,

    /// <summary>Paused by the system after consecutive visits expired unpaid - see <see cref="RecurringBookingPlan.PauseForUnpaidVisits"/>.</summary>
    UnpaidVisits,

    /// <summary>Paused by the system after an open-ended plan's auto-charge ran out of retries - see <see cref="RecurringBookingPlan.PauseForPaymentFailure"/>.</summary>
    PaymentFailure,

    /// <summary>Paused by an admin (support) - see <see cref="RecurringBookingPlan.PauseByAdmin"/>. The customer cannot resume it themselves; support does.</summary>
    Admin
}
