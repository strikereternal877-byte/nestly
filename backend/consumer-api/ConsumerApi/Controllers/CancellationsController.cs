using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Nestly.Application.Cancellations;
using Nestly.BuildingBlocks.Extensions;

namespace Nestly.ConsumerApi.Controllers;

/// <summary>
/// Customer-initiated booking cancellation (SRS 11.14, 24.6, tasks 80a-c,
/// 81). Every action is scoped to the caller's own customer id, same as
/// <see cref="BookingsController"/>.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Authorize]
[Route("api/v{version:apiVersion}/bookings/{bookingId:guid}/cancellation")]
public class CancellationsController : ControllerBase
{
    private readonly ICancellationService _cancellationService;
    private readonly IValidator<CancelBookingRequest> _cancelValidator;

    public CancellationsController(ICancellationService cancellationService, IValidator<CancelBookingRequest> cancelValidator)
    {
        _cancellationService = cancellationService;
        _cancelValidator = cancelValidator;
    }

    /// <summary>Cancellation eligibility + fee/refund policy preview, shown before the customer confirms (SRS 11.14.3). A late-reschedule fee the customer already paid is counted against the cancellation fee (<c>RescheduleFeeCredited</c>), not charged on top.</summary>
    [HttpGet("policy")]
    [ProducesResponseType(typeof(CancellationPolicyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPolicy(Guid bookingId)
    {
        var result = await _cancellationService.GetPolicyAsync(CurrentCustomerId(), bookingId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Confirms the cancellation - transitions the booking, raises a refund if one is owed, tells the assigned professional the booking is off, and returns the outcome (SRS 24.6).</summary>
    [HttpPost]
    [ProducesResponseType(typeof(CancellationOutcomeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Cancel(Guid bookingId, [FromBody] CancelBookingRequest request)
    {
        var validation = await _cancelValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _cancellationService.CancelAsync(CurrentCustomerId(), bookingId, request);
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
