using System.IdentityModel.Tokens.Jwt;
using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Nestly.Application.ProviderAvailability;
using Nestly.BuildingBlocks.Extensions;
using Nestly.Infrastructure;

namespace Nestly.ProviderApi.Controllers;

/// <summary>
/// Provider availability windows and blackout dates (task 149b, PROVIDER.md API
/// surface "Availability"). Every action is scoped to the caller's own
/// provider id taken from the JWT (SRS 28.3 IDOR), same pattern as
/// <see cref="ProfileController"/>.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Authorize(AuthenticationSchemes = DependencyInjection.ProviderJwtBearerScheme)]
[Route("api/v{version:apiVersion}/availability")]
public class AvailabilityController : ControllerBase
{
    private readonly IProviderAvailabilityService _availabilityService;
    private readonly IValidator<UpdateProviderAvailabilityWindowsRequest> _windowsValidator;
    private readonly IValidator<AddProviderBlackoutDateRequest> _blackoutDateValidator;

    public AvailabilityController(
        IProviderAvailabilityService availabilityService,
        IValidator<UpdateProviderAvailabilityWindowsRequest> windowsValidator,
        IValidator<AddProviderBlackoutDateRequest> blackoutDateValidator)
    {
        _availabilityService = availabilityService;
        _windowsValidator = windowsValidator;
        _blackoutDateValidator = blackoutDateValidator;
    }

    /// <summary>Weekly availability windows plus blackout dates in one view.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ProviderAvailabilityResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get()
    {
        return Ok(await _availabilityService.GetAsync(CurrentProviderId()));
    }

    /// <summary>Replace the provider's whole recurring weekly schedule.</summary>
    [HttpPut("windows")]
    [ProducesResponseType(typeof(IReadOnlyList<ProviderAvailabilityWindowResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateWindows([FromBody] UpdateProviderAvailabilityWindowsRequest request)
    {
        var validation = await _windowsValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _availabilityService.UpdateWindowsAsync(CurrentProviderId(), request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>List blackout dates.</summary>
    [HttpGet("blackout-dates")]
    [ProducesResponseType(typeof(IReadOnlyList<ProviderBlackoutDateResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBlackoutDates()
    {
        return Ok(await _availabilityService.GetBlackoutDatesAsync(CurrentProviderId()));
    }

    /// <summary>Add a blackout date range (leave, illness, personal unavailability).</summary>
    [HttpPost("blackout-dates")]
    [ProducesResponseType(typeof(ProviderBlackoutDateResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AddBlackoutDate([FromBody] AddProviderBlackoutDateRequest request)
    {
        var validation = await _blackoutDateValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _availabilityService.AddBlackoutDateAsync(CurrentProviderId(), request);
        return result.IsSuccess ? StatusCode(StatusCodes.Status201Created, result.Value) : result.ToProblemResult();
    }

    /// <summary>Remove a blackout date.</summary>
    [HttpDelete("blackout-dates/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteBlackoutDate(Guid id)
    {
        var result = await _availabilityService.DeleteBlackoutDateAsync(CurrentProviderId(), id);
        return result.IsSuccess ? NoContent() : result.ToProblemResult();
    }

    private Guid CurrentProviderId() =>
        Guid.Parse(User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value);

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
