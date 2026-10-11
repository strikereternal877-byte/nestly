using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nestly.Application.Meals;
using Nestly.BuildingBlocks.Extensions;

namespace Nestly.ConsumerApi.Controllers;

/// <summary>
/// The customer's saved food preferences (docs/MEAL-SERVICE.md FOOD
/// PREFERENCES). Read-only here: they are saved by booking a cook, so the
/// review pages pre-fill from the last booking.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Authorize]
[Route("api/v{version:apiVersion}/profile/meal-preferences")]
public class MealPreferencesController : ControllerBase
{
    private readonly IMealPreferenceService _service;

    public MealPreferencesController(IMealPreferenceService service) => _service = service;

    /// <summary>The preferences from the customer's last cook booking; 204 when they have none yet.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(MealPreferencesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Get()
    {
        var saved = await _service.GetAsync(User.GetSubjectId());
        return saved is null ? NoContent() : Ok(saved);
    }
}
