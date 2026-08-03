using System.IdentityModel.Tokens.Jwt;
using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Nestly.Application.ProviderIdentity;
using Nestly.Application.ProviderProfile;
using Nestly.BuildingBlocks.Extensions;
using Nestly.Infrastructure;

namespace Nestly.ProviderApi.Controllers;

/// <summary>
/// Provider profile, KYC, service areas and skills (task 149a, PROVIDER.md API
/// surface "Profile/Onboarding"). Every action is scoped to the caller's own
/// provider id taken from the JWT — there is no route or body parameter that
/// could name a different provider (SRS 28.3 IDOR), mirroring
/// consumer-api's <c>CustomerProfileController</c>/<c>CustomerAddressController</c>.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Authorize(AuthenticationSchemes = DependencyInjection.ProviderJwtBearerScheme)]
[Route("api/v{version:apiVersion}/profile")]
public class ProfileController : ControllerBase
{
    private readonly IProviderProfileService _profileService;
    private readonly IProviderKycService _kycService;
    private readonly IValidator<UpdateProviderProfileRequest> _updateProfileValidator;
    private readonly IValidator<SubmitProviderKycDocumentRequest> _kycDocumentValidator;
    private readonly IValidator<UpdateProviderServiceAreasRequest> _serviceAreasValidator;
    private readonly IValidator<UpdateProviderSkillsRequest> _skillsValidator;

    public ProfileController(
        IProviderProfileService profileService,
        IProviderKycService kycService,
        IValidator<UpdateProviderProfileRequest> updateProfileValidator,
        IValidator<SubmitProviderKycDocumentRequest> kycDocumentValidator,
        IValidator<UpdateProviderServiceAreasRequest> serviceAreasValidator,
        IValidator<UpdateProviderSkillsRequest> skillsValidator)
    {
        _profileService = profileService;
        _kycService = kycService;
        _updateProfileValidator = updateProfileValidator;
        _kycDocumentValidator = kycDocumentValidator;
        _serviceAreasValidator = serviceAreasValidator;
        _skillsValidator = skillsValidator;
    }

    /// <summary>View profile.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ProviderProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get()
    {
        var result = await _profileService.GetAsync(CurrentProviderId());
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Edit legal name, display name and email.</summary>
    [HttpPut]
    [ProducesResponseType(typeof(ProviderProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update([FromBody] UpdateProviderProfileRequest request)
    {
        var validation = await _updateProfileValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _profileService.UpdateAsync(CurrentProviderId(), request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Overall KYC picture: onboarding status plus every submitted document.</summary>
    [HttpGet("kyc")]
    [ProducesResponseType(typeof(ProviderKycStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetKycStatus()
    {
        var result = await _kycService.GetStatusAsync(CurrentProviderId());
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>
    /// Submit a KYC document. <c>FileRef</c> is a reference to an
    /// already-uploaded file (storage key/URL) — this endpoint does not
    /// itself accept a binary upload, matching <see cref="IProviderKycService"/>.
    /// </summary>
    [HttpPost("kyc/documents")]
    [ProducesResponseType(typeof(ProviderKycDocumentResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SubmitKycDocument([FromBody] SubmitProviderKycDocumentBody body)
    {
        var request = new SubmitProviderKycDocumentRequest(CurrentProviderId(), body.DocType, body.FileRef, body.DocNumber);
        var validation = await _kycDocumentValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _kycService.SubmitDocumentAsync(request);
        return result.IsSuccess ? StatusCode(StatusCodes.Status201Created, result.Value) : result.ToProblemResult();
    }

    /// <summary>List the provider's declared geography coverage.</summary>
    [HttpGet("service-areas")]
    [ProducesResponseType(typeof(IReadOnlyList<ProviderServiceAreaResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetServiceAreas()
    {
        return Ok(await _profileService.GetServiceAreasAsync(CurrentProviderId()));
    }

    /// <summary>Replace the provider's whole geography coverage set.</summary>
    [HttpPut("service-areas")]
    [ProducesResponseType(typeof(IReadOnlyList<ProviderServiceAreaResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateServiceAreas([FromBody] UpdateProviderServiceAreasRequest request)
    {
        var validation = await _serviceAreasValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _profileService.UpdateServiceAreasAsync(CurrentProviderId(), request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>List the categories/services the provider is qualified for.</summary>
    [HttpGet("skills")]
    [ProducesResponseType(typeof(IReadOnlyList<ProviderSkillResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSkills()
    {
        return Ok(await _profileService.GetSkillsAsync(CurrentProviderId()));
    }

    /// <summary>Replace the provider's whole declared skill set.</summary>
    [HttpPut("skills")]
    [ProducesResponseType(typeof(IReadOnlyList<ProviderSkillResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateSkills([FromBody] UpdateProviderSkillsRequest request)
    {
        var validation = await _skillsValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _profileService.UpdateSkillsAsync(CurrentProviderId(), request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
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

/// <summary>
/// Request body for <see cref="ProfileController.SubmitKycDocument"/> — the
/// provider id is deliberately excluded here (unlike
/// <see cref="SubmitProviderKycDocumentRequest"/>) and taken from the JWT
/// instead, so a caller can never submit a document against another
/// provider's id (SRS 28.3 IDOR).
/// </summary>
public record SubmitProviderKycDocumentBody(Nestly.Domain.ProviderKycDocumentType DocType, string FileRef, string? DocNumber);
