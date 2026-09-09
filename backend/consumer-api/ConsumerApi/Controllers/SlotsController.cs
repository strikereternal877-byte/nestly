using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nestly.Application.Slots;
using Nestly.BuildingBlocks.Extensions;

namespace Nestly.ConsumerApi.Controllers;

/// <summary>Slot availability (task 46, SRS 24.4). No auth - anyone can check availability before login.</summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/slots")]
public class SlotsController : ControllerBase
{
    private readonly ISlotAvailabilityService _slotAvailabilityService;

    public SlotsController(ISlotAvailabilityService slotAvailabilityService)
    {
        _slotAvailabilityService = slotAvailabilityService;
    }

    /// <summary>Available slots for a service, at an address (locality), on a date.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(SlotAvailabilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAvailableSlots([FromQuery] Guid serviceId, [FromQuery] Guid localityId, [FromQuery] DateOnly date)
    {
        var result = await _slotAvailabilityService.GetAvailableSlotsAsync(serviceId, localityId, date);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Availability for every date in a range - one call for a whole date strip.</summary>
    [HttpGet("range")]
    [ProducesResponseType(typeof(SlotRangeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAvailableSlotsRange(
        [FromQuery] Guid serviceId,
        [FromQuery] Guid localityId,
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to)
    {
        var result = await _slotAvailabilityService.GetAvailableSlotsRangeAsync(serviceId, localityId, from, to);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Re-checks a previously offered slot right before booking confirmation.</summary>
    [HttpGet("revalidate")]
    [ProducesResponseType(typeof(SlotRevalidationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revalidate(
        [FromQuery] Guid serviceId,
        [FromQuery] Guid localityId,
        [FromQuery] Guid slotWindowId,
        [FromQuery] DateOnly date)
    {
        var result = await _slotAvailabilityService.RevalidateSlotAsync(serviceId, localityId, slotWindowId, date);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }
}
