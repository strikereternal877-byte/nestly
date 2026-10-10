using Nestly.Application.Pricing;
using Nestly.Domain;

namespace Nestly.Application.RecurringBookings;

/// <summary>
/// Same identity/catalog/slot fields <see cref="Bookings.BookingSummaryRequest"/>
/// takes, minus <c>SlotDate</c> (replaced by <see cref="StartDate"/> plus the
/// recurrence fields, since a plan's occurrence dates are computed, not
/// picked one at a time) and minus <c>CouponCode</c> (a coupon is a one-time
/// promotional redemption; auto-reapplying the same code to every future
/// occurrence would turn a single-use promotion into a standing discount it
/// was never designed to be - task 184's scope note).
///
/// <see cref="ApplyWalletCredit"/> (task 370) is chosen once here, at plan
/// creation, and reused for every occurrence, since there is no
/// per-occurrence UI moment to ask again the way an ad-hoc booking's own
/// wallet checkbox does. Defaults to false, matching that checkbox's
/// existing off-by-default precedent (booking/summary deliberately avoids a
/// silent auto-apply).
/// </summary>
public record CreateRecurringBookingPlanRequest(
    Guid ServiceId,
    Guid CityId,
    Guid AddressId,
    Guid LocalityId,
    Guid SlotWindowId,
    int Quantity,
    RecurringBookingRecurrenceFrequency Frequency,
    DayOfWeek? RecurrenceDayOfWeek,
    int? RecurrenceDayOfMonth,
    DateOnly StartDate,
    DateOnly? EndDate,
    int? OccurrenceCount,
    IReadOnlyList<AddOnSelection> AddOns,
    bool ApplyWalletCredit = false,
    // Recurring-booking payment-timing fix: explicit opt-in only, never
    // inferred - see RecurringBookingPlan.AutoChargeEnabled's doc comment.
    bool AutoChargeEnabled = false,
    // Prepaid: every visit of the first cycle is created up front and paid in
    // one checkout. LeadBookingId is the booking the customer has just placed
    // (and will pay from) - it is visit 1 of the purchase and is not itself a
    // plan occurrence; the plan's occurrences are the repeats after it.
    bool PrepaidUpfront = false,
    Guid? LeadBookingId = null);

public record RecurringBookingPlanResponse(
    Guid Id,
    Guid ServiceId,
    string ServiceName,
    Guid AddressId,
    Guid SlotWindowId,
    int Quantity,
    bool ApplyWalletCredit,
    bool AutoChargeEnabled,
    RecurringBookingRecurrenceFrequency Frequency,
    DayOfWeek? RecurrenceDayOfWeek,
    int? RecurrenceDayOfMonth,
    DateOnly StartDate,
    DateOnly? EndDate,
    int? OccurrenceCount,
    int CompletedOccurrenceCount,
    DateOnly NextOccurrenceDate,
    RecurringBookingPlanStatus Status,
    DateTime CreatedAtUtc,
    bool PrepaidUpfront = false,
    // Non-null while a prepaid cycle still has to be paid; the payment page for
    // the cycle is /booking/payment/{PendingPrepaymentBookingId}.
    Guid? PendingPrepaymentBookingId = null,
    DateOnly? PrepaidThroughDate = null,
    // Dates the prepaid cycle could not be booked for (slot full, no
    // professional) and that the customer is therefore not being charged for.
    IReadOnlyList<DateOnly>? SkippedDates = null,
    // Why the plan is Paused (customer, unpaid visits, payment failure); null when it is not paused.
    RecurringBookingPauseReason? PauseReason = null,
    // The last "skip visits until" date asked for, and how many such requests the plan has used.
    DateOnly? SkipUntilDate = null,
    int SkipRangesUsed = 0,
    // What the "change time" screen needs to offer the plan's available windows for its service and locality.
    Guid? CityId = null,
    Guid? LocalityId = null,
    // Live detail for the plan card, filled in by the list/detail reads (null/empty on the responses to an action):
    // the plan's time window, what a visit costs, the visits already booked, and a visit waiting on payment.
    string? SlotWindowName = null,
    TimeSpan? SlotStartTime = null,
    TimeSpan? SlotEndTime = null,
    // Taken from the plan's newest visit (TotalPayable plus any wallet credit used); null until one exists.
    decimal? VisitAmount = null,
    // Dates of visits already booked and still ahead, soonest first (those waiting on payment are not here).
    IReadOnlyList<DateOnly>? UpcomingBookedVisitDates = null,
    PlanVisitAwaitingPaymentResponse? VisitAwaitingPayment = null,
    // The platform's cancellation policy as it applies to each already-booked visit: free when cancelled at least this
    // many hours before it starts, otherwise this percentage of what was paid is kept. Lets the pause / skip / cancel
    // dialogs state the real charge instead of "a fee may apply".
    decimal? CancellationFreeWindowHours = null,
    decimal? LateCancellationFeePercentage = null);

/// <summary>
/// A pay-as-you-go visit that was created but is not paid yet - what the plan card shows so a customer whose wallet
/// ran short can see it and pay without hunting for the booking. Paid at <c>/booking/payment/{BookingId}</c>.
/// </summary>
public record PlanVisitAwaitingPaymentResponse(Guid BookingId, DateOnly SlotDate, decimal AmountDue);

/// <summary>A projected future date (not yet a real booking) or a past outcome the scheduler already recorded - see <see cref="RecurringBookingOccurrence"/>'s doc comment on why the two are computed differently.</summary>
/// <summary>Recurring-booking payment-timing fix: request body for toggling <see cref="RecurringBookingPlanResponse.AutoChargeEnabled"/> post-creation.</summary>
public record SetAutoChargeRequest(bool Enabled);

/// <summary>Occurrence-count integrity fix: request body for <see cref="IRecurringBookingPlanService.SetOccurrenceBoundsAsync"/> - see <see cref="Domain.RecurringBookingPlan.SetOccurrenceBounds"/>'s doc comment for the validation rules.</summary>
public record SetOccurrenceBoundsRequest(DateOnly? EndDate, int? OccurrenceCount);

/// <summary>
/// "I'm away until <paramref name="ResumeOn"/>": no visit is generated before that date, and the plan carries on
/// from it. <paramref name="CancelBookedVisits"/> also cancels the visits already booked before that date (the few
/// the scheduler has already created ahead of time), through the ordinary cancellation policy.
/// </summary>
public record SkipVisitsRequest(DateOnly ResumeOn, bool CancelBookedVisits = false);

/// <summary>Move every visit generated from now on to a different time-of-day window. Visits already booked keep their slot.</summary>
public record ChangePlanSlotRequest(Guid SlotWindowId);

public record UpcomingOccurrenceResponse(DateOnly ScheduledDate, bool IsProjected);

public record OccurrenceHistoryResponse(
    DateOnly ScheduledDate,
    RecurringBookingOccurrenceOutcome Outcome,
    Guid? BookingId,
    string? SkipReason,
    DateTime ProcessedAtUtc);
