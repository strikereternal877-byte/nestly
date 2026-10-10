using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.Bookings;

/// <summary>The result of one real off-session charge attempt (sweep- or admin-forced) - distinct from a <see cref="Result{TValue}"/> failure, which means the attempt never ran at all (booking not found, not a recurring occurrence, already cancelled).</summary>
public enum AutoChargeAttemptOutcome
{
    Succeeded,
    Failed,
    /// <summary>Failed, and this attempt was also the one that crossed <c>RecurringBookingOptions.AutoChargeRetryLimit</c> - the manual-payment fallback notification already went out as part of this same attempt.</summary>
    Exhausted
}

/// <summary>
/// Recurring-booking payment-timing fix: attempts off-session payment for
/// recurring occurrences whose plan has opted in to auto-charge, through the
/// same sandbox gateway seam <c>SubscriptionBillingJob</c> already uses
/// (<c>IPaymentGateway</c>/<c>ISandboxPaymentSimulator</c> via
/// <c>IPaymentService.CreateOrderAsync</c>/<c>SimulateAsync</c>) - not a
/// second, invented payment integration.
/// </summary>
public interface IRecurringOccurrenceAutoChargeJob
{
    /// <summary>Idempotent and safe to re-run (Hangfire's retry convention requires this) - a booking already moved out of PaymentPending (by a successful charge, a manual payment, or BookingExpirySweepJob) is simply not picked up again.</summary>
    Task ProcessDueAttemptsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Payment Management UX pass: an admin-triggered immediate attempt for
    /// one occurrence. Unlike <see cref="ProcessDueAttemptsAsync"/>'s own
    /// sweep, this bypasses the backoff-timing gate, the plan's own
    /// <c>AutoChargeEnabled</c> toggle, and
    /// <see cref="Domain.Booking.AutoChargeCancelledByAdmin"/> - an explicit,
    /// one-off admin action each time, same "admin override, no inherited
    /// eligibility gates" precedent as <c>RescheduleService.AdminRescheduleAsync</c>.
    /// Cancelling future automatic retries does not lock out a deliberate
    /// manual one. Still refuses a booking that is not a recurring
    /// occurrence or is no longer in a chargeable status (already Confirmed,
    /// cancelled, etc.) - those are not gates to override, they mean there is
    /// nothing left to charge. This is the same core attempt logic
    /// <see cref="ProcessDueAttemptsAsync"/> uses per booking (still stamps
    /// <see cref="Domain.Booking.RecordAutoChargeAttempt"/> and can still trip
    /// <see cref="AutoChargeAttemptOutcome.Exhausted"/>), not a second charge
    /// path.
    /// </summary>
    Task<Result<AutoChargeAttemptOutcome>> ForceAttemptAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the same manual-payment fallback notification a naturally
    /// exhausted retry budget would have - reused, not a second message, so
    /// the customer gets one consistent "pay manually" notice regardless of
    /// whether the retry budget ran out or an admin stopped it early via
    /// <see cref="Domain.Booking.CancelAutoChargeRetries"/>. The caller is
    /// responsible for having already cancelled retries on the booking;
    /// this only sends the notice.
    /// </summary>
    Task NotifyRetriesCancelledAsync(Guid bookingId, CancellationToken cancellationToken = default);
}
