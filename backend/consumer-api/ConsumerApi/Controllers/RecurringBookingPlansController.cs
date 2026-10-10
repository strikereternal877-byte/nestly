using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Nestly.Application.RecurringBookings;
using Nestly.BuildingBlocks.Extensions;

namespace Nestly.ConsumerApi.Controllers;

/// <summary>Recurring booking plans (PRODUCT-ENHANCEMENTS.md section 2, task 186). Every action is scoped to the caller's own customer id — never a route/body parameter, same convention as <see cref="BookingsController"/>.</summary>
[ApiController]
[ApiVersion(1)]
[Authorize]
[Route("api/v{version:apiVersion}/recurring-booking-plans")]
public class RecurringBookingPlansController : ControllerBase
{
    private readonly IRecurringBookingPlanService _planService;
    private readonly IValidator<CreateRecurringBookingPlanRequest> _createValidator;

    public RecurringBookingPlansController(
        IRecurringBookingPlanService planService,
        IValidator<CreateRecurringBookingPlanRequest> createValidator)
    {
        _planService = planService;
        _createValidator = createValidator;
    }

    /// <summary>Creates a recurring plan. Validated end-to-end through the same booking orchestration a one-off booking preview uses (task 58) before anything is persisted.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(RecurringBookingPlanResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] CreateRecurringBookingPlanRequest request)
    {
        var validation = await _createValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _planService.CreateAsync(CurrentCustomerId(), request);
        return result.IsSuccess
            ? CreatedAtAction(nameof(Detail), new { id = result.Value.Id }, result.Value)
            : result.ToProblemResult();
    }

    /// <summary>Lists the caller's recurring plans, most recently created first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RecurringBookingPlanResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List()
    {
        var result = await _planService.ListAsync(CurrentCustomerId());
        return Ok(result.Value);
    }

    /// <summary>Plan detail.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(RecurringBookingPlanResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Detail(Guid id)
    {
        var result = await _planService.GetAsync(CurrentCustomerId(), id);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Pauses an active plan - the scheduler will not attempt or skip-and-notify any occurrence while paused.</summary>
    [HttpPost("{id:guid}/pause")]
    [ProducesResponseType(typeof(RecurringBookingPlanResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Pause(Guid id)
    {
        var result = await _planService.PauseAsync(CurrentCustomerId(), id);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Resumes a paused plan from exactly where it left off.</summary>
    [HttpPost("{id:guid}/resume")]
    [ProducesResponseType(typeof(RecurringBookingPlanResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Resume(Guid id)
    {
        var result = await _planService.ResumeAsync(CurrentCustomerId(), id);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>"I'm away until a date": no visit is generated before it; the plan carries on from there. Optionally cancels the few visits already booked before that date.</summary>
    [HttpPost("{id:guid}/skip-visits")]
    [ProducesResponseType(typeof(RecurringBookingPlanResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SkipVisits(Guid id, [FromBody] SkipVisitsRequest request)
    {
        var result = await _planService.SkipVisitsAsync(CurrentCustomerId(), id, request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Changes the time-of-day window for every visit generated from now on. Visits already booked keep their slot.</summary>
    [HttpPost("{id:guid}/slot")]
    [ProducesResponseType(typeof(RecurringBookingPlanResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ChangeSlot(Guid id, [FromBody] ChangePlanSlotRequest request)
    {
        var result = await _planService.ChangeSlotAsync(CurrentCustomerId(), id, request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Cancels a plan permanently - a cancelled plan can never be resumed.</summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(RecurringBookingPlanResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Cancel(Guid id)
    {
        var result = await _planService.CancelAsync(CurrentCustomerId(), id);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Toggles off-session auto-charge consent on the plan (recurring-booking payment-timing fix). Callable regardless of pause state.</summary>
    [HttpPost("{id:guid}/auto-charge")]
    [ProducesResponseType(typeof(RecurringBookingPlanResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SetAutoCharge(Guid id, [FromBody] SetAutoChargeRequest request)
    {
        var result = await _planService.SetAutoChargeAsync(CurrentCustomerId(), id, request.Enabled);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Edits how many more occurrences this plan will generate - end date and/or occurrence count only (occurrence-count integrity fix). Cannot reduce below what has already been booked.</summary>
    [HttpPost("{id:guid}/bounds")]
    [ProducesResponseType(typeof(RecurringBookingPlanResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SetOccurrenceBounds(Guid id, [FromBody] SetOccurrenceBoundsRequest request)
    {
        var result = await _planService.SetOccurrenceBoundsAsync(CurrentCustomerId(), id, request.EndDate, request.OccurrenceCount);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Upcoming (projected, not yet real) occurrence dates for the manage screen.</summary>
    [HttpGet("{id:guid}/occurrences/upcoming")]
    [ProducesResponseType(typeof(IReadOnlyList<UpcomingOccurrenceResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpcomingOccurrences(Guid id, [FromQuery] int count = 5)
    {
        var result = await _planService.ListUpcomingOccurrencesAsync(CurrentCustomerId(), id, count);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>What the scheduler has actually recorded so far - booked and skipped occurrences alike.</summary>
    [HttpGet("{id:guid}/occurrences/history")]
    [ProducesResponseType(typeof(IReadOnlyList<OccurrenceHistoryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> OccurrenceHistory(Guid id)
    {
        var result = await _planService.ListOccurrenceHistoryAsync(CurrentCustomerId(), id);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    private Guid CurrentCustomerId() =>
        User.GetSubjectId();

    private static ModelStateDictionary ToModelState(ValidationResult validation)
    {
        var modelState = new ModelStateDictionary();
        foreach (var error in validation.Errors)
        {
            modelState.AddModelError(error.PropertyName, error.ErrorMessage);
        }

        return modelState;
    }
}
