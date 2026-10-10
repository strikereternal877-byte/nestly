using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Nestly.Application.Reschedules;
using Nestly.BuildingBlocks.Extensions;

namespace Nestly.ConsumerApi.Controllers;

/// <summary>
/// Customer-initiated booking reschedule (SRS 11.15, 24.6, tasks 82a-d, 83).
/// Every action is scoped to the caller's own customer id, same as
/// <see cref="BookingsController"/> and <see cref="CancellationsController"/>.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Authorize]
[Route("api/v{version:apiVersion}/bookings/{bookingId:guid}/reschedule")]
public class ReschedulesController : ControllerBase
{
    private readonly IRescheduleService _rescheduleService;
    private readonly IValidator<RescheduleBookingRequest> _rescheduleValidator;

    public ReschedulesController(IRescheduleService rescheduleService, IValidator<RescheduleBookingRequest> rescheduleValidator)
    {
        _rescheduleService = rescheduleService;
        _rescheduleValidator = rescheduleValidator;
    }

    /// <summary>Whether this booking can be rescheduled right now - status, window, and count-limit checks (SRS 11.15.1) - plus the rules in numbers: when rescheduling stops being free, the last moment it is allowed, and, when a reschedule now would be late, the late fee and - when late-fee collection is switched on (it ships off) - whether the customer's wallet covers it (the fee is then debited from the wallet).</summary>
    [HttpGet("eligibility")]
    [ProducesResponseType(typeof(RescheduleEligibilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEligibility(Guid bookingId)
    {
        var result = await _rescheduleService.GetEligibilityAsync(CurrentCustomerId(), bookingId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Eligible future slots for this booking's service at a locality/date, for the picker (SRS 11.15.3, 24.6).</summary>
    [HttpGet("slots")]
    [ProducesResponseType(typeof(Application.Slots.SlotAvailabilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> GetEligibleSlots(Guid bookingId, [FromQuery] Guid localityId, [FromQuery] DateOnly date)
    {
        if (localityId == Guid.Empty)
        {
            return ValidationProblem("localityId is required.");
        }

        var result = await _rescheduleService.GetEligibleSlotsAsync(CurrentCustomerId(), bookingId, localityId, date);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Confirms the reschedule and updates the booking's slot immediately (SRS 11.15.3, 24.6). When late-fee collection is switched on (it ships off; otherwise the fee is only recorded), a late reschedule's fee is debited from the customer's wallet in the same step and booked as platform revenue; when the wallet cannot cover it the response is 422 <c>Reschedule.LateFeeWalletShort</c> (naming the amount to add) and nothing moves. A professional already on the booking is kept when the new time works for them, otherwise replaced, and is told either way.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(RescheduleOutcomeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Confirm(Guid bookingId, [FromBody] RescheduleBookingRequest request)
    {
        var validation = await _rescheduleValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _rescheduleService.ConfirmRescheduleAsync(CurrentCustomerId(), bookingId, request);
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
