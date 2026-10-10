using Nestly.Domain;

namespace Nestly.Application.RecurringBookings;

/// <summary>
/// Filters for the admin recurring-plan list (task 299). Same shape as
/// <see cref="Coupons.CouponAdminSearchRequest"/>/<c>AdminBookingSearchRequest</c>:
/// every filter optional, paging with the project's standard 1-based
/// page/pageSize pair.
/// </summary>
public sealed record AdminRecurringPlanSearchRequest(
    RecurringBookingPlanStatus? Status,
    RecurringBookingRecurrenceFrequency? Frequency,
    Guid? CustomerId,
    Guid? ServiceId,
    int Page = 1,
    int PageSize = 20,
    // Why a paused plan is paused ("which plans did the system pause for unpaid visits?").
    RecurringBookingPauseReason? PauseReason = null,
    // true: only plans paid for in advance; false: only plans paid visit by visit.
    bool? PrepaidUpfront = null);

/// <summary>
/// One row of the admin recurring-plan list. Carries the customer's and
/// service's current names (joined, not snapshotted) because a plan holds
/// live references to both - see <see cref="RecurringBookingPlan"/>'s class
/// doc comment on why nothing about a plan is a snapshot.
/// </summary>
public sealed record AdminRecurringPlanSummaryResponse(
    Guid Id,
    Guid CustomerId,
    string CustomerName,
    Guid ServiceId,
    string ServiceName,
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
    // How the plan is paid for and what state that is in - what an admin needs to answer "why is this plan stuck?".
    bool PrepaidUpfront = false,
    bool AutoChargeEnabled = false,
    bool ApplyWalletCredit = false,
    // A prepaid cycle has been started and is waiting for the customer to pay.
    bool IsAwaitingPrepayment = false,
    DateOnly? PrepaidThroughDate = null,
    // Why the plan is Paused; null when it is not.
    RecurringBookingPauseReason? PauseReason = null,
    // The last "skip visits until" date the customer asked for.
    DateOnly? SkipUntilDate = null);

public sealed record AdminRecurringPlanSearchResponse(
    IReadOnlyList<AdminRecurringPlanSummaryResponse> Items,
    int TotalCount,
    int Page,
    int PageSize);

/// <summary>
/// Admin-initiated plan cancellation (Order/Booking Management UX pass): stops
/// the whole standing instruction in one action - no further occurrences are
/// ever generated - as opposed to cancelling the individual bookings it has
/// already produced one at a time via <c>IBookingManagementService</c>. A
/// reason is required for the audit trail, same convention as
/// <c>AdminCancelBookingRequest</c>.
/// </summary>
public sealed record AdminCancelRecurringPlanRequest(string Reason);

/// <summary>Why an admin is pausing a plan - recorded to the audit trail (and not sent to the customer, who is told only that support paused it).</summary>
public sealed record AdminPauseRecurringPlanRequest(string Reason);

/// <summary>Why an admin is resuming a plan - recorded to the audit trail.</summary>
public sealed record AdminResumeRecurringPlanRequest(string Reason);

/// <summary>One visit a plan has generated, for the admin plan detail.</summary>
public sealed record AdminRecurringPlanVisitResponse(
    Guid BookingId,
    string BookingReference,
    DateOnly SlotDate,
    BookingStatus Status,
    string StatusLabel,
    decimal TotalPayable);

/// <summary>
/// One plan with what the list has no room for: the customer's contact and wallet balance (the wallet pays each
/// visit when <see cref="AdminRecurringPlanSummaryResponse.ApplyWalletCredit"/> is on) and the visits it has
/// generated - upcoming ones first, then the most recent past ones.
/// </summary>
public sealed record AdminRecurringPlanDetailResponse(
    AdminRecurringPlanSummaryResponse Plan,
    string CustomerMobile,
    decimal WalletBalance,
    IReadOnlyList<AdminRecurringPlanVisitResponse> Visits);

/// <summary>
/// The report's horizon. Both ends optional: omitting them reports the next
/// <see cref="IRecurringBookingPlanAdminService.DefaultHorizonDays"/> days
/// from today, which is the view an ops admin opening the screen wants.
/// </summary>
public sealed record AdminRecurringPlanReportRequest(DateOnly? FromDate, DateOnly? ToDate);

/// <summary>Plan count for one lifecycle status - zero-filled, so a status with no plans still appears as 0 rather than being absent from the list.</summary>
public sealed record RecurringPlanStatusCountRow(RecurringBookingPlanStatus Status, int PlanCount);

/// <summary>Active-plan count for one cadence - the standing weekly/biweekly/monthly load behind the raw plan count.</summary>
public sealed record RecurringPlanFrequencyCountRow(RecurringBookingRecurrenceFrequency Frequency, int PlanCount);

/// <summary>Recurring-origin bookings already scheduled for one date inside the horizon.</summary>
public sealed record RecurringPlanDailyVolumeRow(DateOnly SlotDate, int BookingCount);

/// <summary>
/// The admin recurring-plan report (task 299), mirroring the
/// Coupon/Commission/Nestly Coins report shape: a small set of aggregates
/// over a caller-chosen window, every one of them computed by the database.
///
/// "Upcoming occurrence volume" is deliberately reported as two different
/// numbers rather than one, because a recurring plan has two kinds of future:
/// <list type="bullet">
/// <item><see cref="UpcomingOccurrenceVolume"/> - bookings that already exist
/// (<c>Booking.RecurringBookingPlanId</c> is set, task 296's FK) and fall in
/// the horizon. This is real, committed work an ops admin can staff against.</item>
/// <item><see cref="PlansDueInHorizon"/> - active plans whose
/// <see cref="RecurringBookingPlan.NextOccurrenceDate"/> falls in the horizon
/// but which the scheduler has not reached yet. This is work that is coming
/// but is not yet a booking, and may still be skipped
/// (<see cref="RecurringBookingOccurrenceOutcome"/>).</item>
/// </list>
/// Collapsing the two into a single "expected occurrences" figure would need
/// this service to re-run <see cref="RecurringBookingPlan.PreviewUpcomingOccurrenceDates"/>
/// per plan in memory - which is both a projection presented as a fact and
/// exactly the row-by-row aggregation this report avoids.
/// </summary>
public sealed record AdminRecurringPlanReportResponse(
    int TotalPlans,
    IReadOnlyList<RecurringPlanStatusCountRow> ByStatus,
    IReadOnlyList<RecurringPlanFrequencyCountRow> ActiveByFrequency,
    DateOnly HorizonFromDate,
    DateOnly HorizonToDate,
    int PlansDueInHorizon,
    int UpcomingOccurrenceVolume,
    IReadOnlyList<RecurringPlanDailyVolumeRow> UpcomingVolumeByDate);
