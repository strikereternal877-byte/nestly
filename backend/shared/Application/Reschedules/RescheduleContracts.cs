using Nestly.Application.Bookings;
using Nestly.Application.Slots;
using Nestly.Domain;

namespace Nestly.Application.Reschedules;

/// <summary>
/// Reschedule eligibility summary (SRS 11.15.1, tasks 82a-b).
///
/// <para>
/// The trailing members explain the rule so the screen never re-derives a money or time rule (filled in only when the
/// booking is eligible): <paramref name="LateFeeThresholdHours"/> / <paramref name="LateRescheduleFeePercentage"/> are the
/// policy, <paramref name="FreeRescheduleEndsAt"/> is the business-local wall-clock moment a reschedule stops being free
/// (slot start minus the threshold) and <paramref name="LastRescheduleAt"/> the moment after which none is allowed at all,
/// <paramref name="IsLateNow"/> / <paramref name="LateFeeIfRescheduledNow"/> what a reschedule made right now would be
/// recorded as, <paramref name="FeeBasisAmount"/> the paid amount that fee is a percentage of, and
/// <paramref name="CancellationFeeLockedIn"/> the cancellation fee a reschedule made now would lock in as a floor under
/// any later cancellation (<c>Booking.LockedCancellationFeeSnapshot</c>) - moving a booking never erases a fee already owed.
/// </para>
///
/// <para>
/// A late reschedule's fee is taken from the customer's wallet when they confirm, so when <paramref name="IsLateNow"/> the
/// response also says whether the wallet can cover it: <paramref name="WalletBalance"/> is what it holds and
/// <paramref name="LateFeeShortfall"/> what is missing (0 when covered, and when no fee is due). A shortfall means the
/// reschedule is refused until the wallet is topped up, so the screen can say so before the customer picks a slot.
/// </para>
///
/// <para>
/// <paramref name="LateFeeIsCollected"/> says whether a late fee is taken at all: when it is off (the default until it is switched
/// on) the fee is only recorded on the booking, nothing is debited, no wallet is needed, and the screen words it that way.
/// </para>
/// </summary>
public record RescheduleEligibilityResponse(
    bool IsEligible,
    string? IneligibilityReason,
    int ReschedulesUsed,
    int MaxReschedulesPerBooking,
    decimal MinHoursBeforeSlot,
    decimal LateFeeThresholdHours = 0m,
    decimal LateRescheduleFeePercentage = 0m,
    DateTime? FreeRescheduleEndsAt = null,
    DateTime? LastRescheduleAt = null,
    bool IsLateNow = false,
    decimal LateFeeIfRescheduledNow = 0m,
    decimal FeeBasisAmount = 0m,
    decimal CancellationFeeLockedIn = 0m,
    decimal WalletBalance = 0m,
    decimal LateFeeShortfall = 0m,
    bool LateFeeIsCollected = false);

/// <summary>What happened to the professional on the job when it was rescheduled.</summary>
public enum ProfessionalAfterReschedule
{
    /// <summary>Nobody was assigned yet, so there was nobody to keep or release.</summary>
    NoneAssigned = 0,

    /// <summary>The new time is still free for them and they stay on the job.</summary>
    Kept = 1,

    /// <summary>The new time does not work for them, so they were taken off and the booking needs another professional.</summary>
    Released = 2
}

/// <summary>Customer-submitted reschedule request (SRS 24.6). LocalityId re-anchors the slot lookup, mirroring how BookingSummaryRequest does it at booking time.</summary>
public record RescheduleBookingRequest(Guid LocalityId, Guid SlotWindowId, DateOnly SlotDate, string? Reason);

/// <summary>The outcome of a confirmed reschedule (SRS 11.15.2-3, 24.6).</summary>
public record RescheduleOutcomeResponse(
    Guid BookingId,
    BookingStatus BookingStatus,
    BookingSlotSummary PreviousSlot,
    BookingSlotSummary NewSlot,
    bool IsLate,
    decimal FeeAmount,
    int ReschedulesUsed,
    int MaxReschedulesPerBooking,
    DateTime RescheduledAtUtc,
    // The same explanation as on the eligibility response, fixed at the moment of the reschedule, for the slot given up.
    decimal LateFeeThresholdHours = 0m,
    decimal LateRescheduleFeePercentage = 0m,
    DateTime? FreeRescheduleEndedAt = null,
    decimal FeeBasisAmount = 0m,
    // The cancellation fee now locked in as a floor under any later cancellation of this booking (0 if none).
    decimal CancellationFeeLockedIn = 0m,
    // Whether the professional already on the job stays, goes, or there was none yet.
    ProfessionalAfterReschedule Professional = ProfessionalAfterReschedule.NoneAssigned,
    // What was actually taken from the customer's wallet for this reschedule (0 when it was not late, or an admin moved it).
    // FeeAmount above is what the late fee was under the policy; this is what the customer really paid.
    decimal FeeCollected = 0m);
