using Nestly.Domain;

namespace Nestly.Application.Cancellations;

/// <summary>
/// Cancellation policy summary shown before the customer confirms (SRS 11.14.3).
///
/// <para>
/// The last three members explain the numbers so the screen never has to re-derive a money or time rule:
/// <paramref name="FreeCancellationEndsAt"/> is the business-local wall-clock moment free cancellation stops
/// (slot start minus the free window), <paramref name="FeeBasisAmount"/> is the amount the late-cancellation
/// percentage applies to (what the customer paid and stands to have refunded), and
/// <paramref name="EarlierRescheduleCharge"/> is a charge carried over from a late reschedule of this booking that
/// pushed the fee above what timing alone would owe (0 when there is none).
/// </para>
///
/// <para>
/// A late-reschedule fee already taken from the customer is counted against the cancellation fee rather than charged on
/// top: <paramref name="CancellationFeeBeforeCredit"/> is the fee the policy sets, <paramref name="RescheduleFeeCredited"/>
/// the part of it already paid as a reschedule fee, and <see cref="CancellationFeeAmount"/> what is still retained from the
/// refund (the first minus the second, never below zero).
/// </para>
/// </summary>
public record CancellationPolicyResponse(
    bool IsEligible,
    string? IneligibilityReason,
    bool WithinFreeCancellationWindow,
    decimal CancellationFeeAmount,
    decimal RefundAmount,
    RefundMethod RefundMethod,
    decimal FreeCancellationWindowHours,
    decimal LateCancellationFeePercentage,
    DateTime? FreeCancellationEndsAt = null,
    decimal FeeBasisAmount = 0m,
    decimal EarlierRescheduleCharge = 0m,
    decimal CancellationFeeBeforeCredit = 0m,
    decimal RescheduleFeeCredited = 0m);

/// <summary>Customer-submitted cancellation request (SRS 11.14.2 - reason is always captured).</summary>
public record CancelBookingRequest(string Reason);

/// <summary>The refund/fee outcome of a completed cancellation (SRS 11.14.3, 24.6).</summary>
/// <remarks>
/// The trailing three members mean what they do on <see cref="CancellationPolicyResponse"/>, fixed at the moment of
/// cancellation, so the result screen can say why the free window did or did not apply and what the fee was
/// charged on.
/// </remarks>
public record CancellationOutcomeResponse(
    Guid BookingId,
    BookingStatus BookingStatus,
    bool WithinFreeCancellationWindow,
    decimal CancellationFeeAmount,
    decimal RefundAmount,
    RefundStatus? RefundStatus,
    RefundMethod? RefundMethod,
    Guid? RefundTransactionId,
    DateTime CancelledAtUtc,
    DateTime? FreeCancellationEndsAt = null,
    decimal FeeBasisAmount = 0m,
    decimal EarlierRescheduleCharge = 0m,
    decimal CancellationFeeBeforeCredit = 0m,
    decimal RescheduleFeeCredited = 0m);
