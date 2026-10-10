using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.RecurringBookings;

/// <summary>Create/pause/resume/cancel a recurring plan and read its state (task 186). Every method is scoped to the caller's own customer id, same convention as <c>IBookingService</c>.</summary>
public interface IRecurringBookingPlanService
{
    /// <summary>
    /// Validates the request through the exact same summary orchestration a
    /// one-off booking preview uses (<see cref="Bookings.IBookingSummaryService"/>,
    /// task 58) against <see cref="CreateRecurringBookingPlanRequest.StartDate"/>
    /// as the trial slot date - so a plan can never be created against a
    /// service/address/slot combination that would immediately fail its own
    /// first occurrence. Persists the plan only if that validation passes.
    /// </summary>
    Task<Result<RecurringBookingPlanResponse>> CreateAsync(Guid customerId, CreateRecurringBookingPlanRequest request);

    Task<Result<IReadOnlyList<RecurringBookingPlanResponse>>> ListAsync(Guid customerId);

    Task<Result<RecurringBookingPlanResponse>> GetAsync(Guid customerId, Guid planId);

    Task<Result<RecurringBookingPlanResponse>> PauseAsync(Guid customerId, Guid planId);

    Task<Result<RecurringBookingPlanResponse>> ResumeAsync(Guid customerId, Guid planId);

    Task<Result<RecurringBookingPlanResponse>> CancelAsync(Guid customerId, Guid planId);

    /// <summary>
    /// Recurring-booking payment-timing fix: toggles the customer's consent
    /// to off-session auto-charge on this plan (see
    /// <see cref="Domain.RecurringBookingPlan.AutoChargeEnabled"/>'s doc
    /// comment). Callable regardless of whether the plan is paused - see
    /// <see cref="Domain.RecurringBookingPlan.SetAutoCharge"/>.
    /// </summary>
    Task<Result<RecurringBookingPlanResponse>> SetAutoChargeAsync(Guid customerId, Guid planId, bool enabled);

    /// <summary>
    /// "I'm away until a date": nothing is generated before it. The plan stays Active; see
    /// <see cref="Domain.RecurringBookingPlan.SkipVisitsUntil"/>. Limited by policy to a few requests per plan and a
    /// maximum look-ahead, so it cannot be chained into a permanent gap.
    /// </summary>
    Task<Result<RecurringBookingPlanResponse>> SkipVisitsAsync(Guid customerId, Guid planId, SkipVisitsRequest request);

    /// <summary>
    /// Changes the time-of-day window for every visit generated from now on, after checking that the new window can
    /// serve the plan's next visit. Visits already booked are untouched - reschedule those individually.
    /// </summary>
    Task<Result<RecurringBookingPlanResponse>> ChangeSlotAsync(Guid customerId, Guid planId, ChangePlanSlotRequest request);

    /// <summary>
    /// Occurrence-count integrity fix: the plan's own "edit" capability,
    /// scoped to only its occurrence budget (see
    /// <see cref="Domain.RecurringBookingPlan.SetOccurrenceBounds"/>'s doc
    /// comment for exactly what that does and does not cover). Before this,
    /// a customer could only Cancel a plan outright or Pause/Resume it -
    /// neither can reduce how many more visits it promises.
    /// </summary>
    Task<Result<RecurringBookingPlanResponse>> SetOccurrenceBoundsAsync(Guid customerId, Guid planId, DateOnly? endDate, int? occurrenceCount);

    /// <summary>Projected future dates plus recent recorded outcomes, for the manage screen (task 187).</summary>
    Task<Result<IReadOnlyList<UpcomingOccurrenceResponse>>> ListUpcomingOccurrencesAsync(Guid customerId, Guid planId, int count = 5);

    Task<Result<IReadOnlyList<OccurrenceHistoryResponse>>> ListOccurrenceHistoryAsync(Guid customerId, Guid planId);
}
