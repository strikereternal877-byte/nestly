using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.RecurringBookings;

/// <summary>
/// The admin-side surface over <c>recurring_booking_plan</c> (task 299): a
/// filterable list of every plan on the platform, the status/cadence/volume
/// report behind it, and (Order/Booking Management UX pass) plan-level
/// cancellation.
///
/// Search/report stay separate from <see cref="IRecurringBookingPlanService"/>
/// rather than an admin overload on it: that service is entirely
/// customer-scoped (every method takes the caller's customer id and refuses
/// anything it does not own), and widening it to sometimes skip that check
/// would put the ownership guard behind a boolean.
///
/// <see cref="CancelAsync"/> is the one write this service owns: an admin
/// stopping the whole standing instruction in one action, distinct from
/// cancelling the individual bookings a plan has already produced (which
/// still goes through <c>IBookingManagementService</c>, unaffected by this -
/// a plan cancellation does not touch bookings already generated, only
/// whether more get generated).
/// </summary>
public interface IRecurringBookingPlanAdminService
{
    /// <summary>Horizon applied when the caller supplies no date range - four weeks, long enough to contain at least one occurrence of every supported cadence except monthly's worst case.</summary>
    const int DefaultHorizonDays = 28;

    Task<Result<AdminRecurringPlanSearchResponse>> SearchAsync(AdminRecurringPlanSearchRequest request);

    /// <summary>Validation failure when <c>ToDate</c> precedes <c>FromDate</c>, matching <c>IReportingQueryService</c>'s "Reports.InvalidDateRange".</summary>
    Task<Result<AdminRecurringPlanReportResponse>> GetReportAsync(AdminRecurringPlanReportRequest request);

    /// <summary>
    /// Cancels a plan regardless of which customer owns it (unlike
    /// <see cref="IRecurringBookingPlanService.CancelAsync"/>, which is
    /// scoped to the caller's own plans) - terminal, via the same
    /// <see cref="Domain.RecurringBookingPlan.Cancel"/> domain method, so the
    /// same Active-or-Paused-only rule applies. <paramref name="adminUserId"/>
    /// is the acting admin, for the audit trail.
    /// </summary>
    Task<Result<AdminRecurringPlanSummaryResponse>> CancelAsync(Guid planId, Guid adminUserId, AdminCancelRecurringPlanRequest request);

    /// <summary>NotFound ("RecurringBookingPlan.NotFound") when the id is not a plan.</summary>
    Task<Result<AdminRecurringPlanDetailResponse>> GetAsync(Guid planId);

    /// <summary>
    /// Pauses an active plan on the customer's behalf (the scheduler stops booking new visits; visits already booked
    /// are untouched). Recorded as paused by an admin, which the customer cannot undo themselves; the customer is
    /// told, and the action is audited with the reason.
    /// </summary>
    Task<Result<AdminRecurringPlanSummaryResponse>> PauseAsync(Guid planId, Guid adminUserId, AdminPauseRecurringPlanRequest request);

    /// <summary>
    /// Resumes a paused plan, whoever or whatever paused it (a plan the system paused for unpaid visits is resumed
    /// the same way). The customer is told, and the action is audited with the reason.
    /// </summary>
    Task<Result<AdminRecurringPlanSummaryResponse>> ResumeAsync(Guid planId, Guid adminUserId, AdminResumeRecurringPlanRequest request);
}
