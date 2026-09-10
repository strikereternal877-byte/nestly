using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Nestly.Application.ProviderIdentity;
using Nestly.Application.ProviderProfile;
using Nestly.Application.Storage;
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
    private readonly IFileStorageService _fileStorageService;
    private readonly IValidator<UpdateProviderProfileRequest> _updateProfileValidator;
    private readonly IValidator<UpdateProviderPhotoRequest> _updatePhotoValidator;
    private readonly IValidator<SubmitProviderKycDocumentRequest> _kycDocumentValidator;
    private readonly IValidator<UpdateProviderServiceAreasRequest> _serviceAreasValidator;
    private readonly IValidator<UpdateProviderSkillsRequest> _skillsValidator;

    public ProfileController(
        IProviderProfileService profileService,
        IProviderKycService kycService,
        IFileStorageService fileStorageService,
        IValidator<UpdateProviderProfileRequest> updateProfileValidator,
        IValidator<UpdateProviderPhotoRequest> updatePhotoValidator,
        IValidator<SubmitProviderKycDocumentRequest> kycDocumentValidator,
        IValidator<UpdateProviderServiceAreasRequest> serviceAreasValidator,
        IValidator<UpdateProviderSkillsRequest> skillsValidator)
    {
        _profileService = profileService;
        _kycService = kycService;
        _fileStorageService = fileStorageService;
        _updateProfileValidator = updateProfileValidator;
        _updatePhotoValidator = updatePhotoValidator;
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

    /// <summary>
    /// Set or clear the profile photo (task 293). <c>PhotoUrl</c> is a
    /// reference to an already-hosted image (storage key/URL), not a binary
    /// upload - the same convention <see cref="SubmitKycDocument"/> uses.
    /// A new photo always re-enters admin moderation; customers see it only
    /// once it is approved.
    /// </summary>
    [HttpPut("photo")]
    [ProducesResponseType(typeof(ProviderProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdatePhoto([FromBody] UpdateProviderPhotoRequest request)
    {
        var validation = await _updatePhotoValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _profileService.UpdatePhotoAsync(CurrentProviderId(), request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>
    /// Uploads a profile photo file and returns its URL for
    /// <see cref="UpdatePhoto"/> — a separate call rather than accepting the
    /// file directly on <see cref="UpdatePhoto"/>, so that endpoint's existing
    /// JSON contract and validator are untouched. Mirrors
    /// provider-api's <c>JobsController.UploadCompletionPhoto</c>: content-type
    /// checked against an image allowlist and size capped before anything is
    /// read into memory or written to storage.
    /// </summary>
    [HttpPost("photo/upload")]
    [ProducesResponseType(typeof(ProviderFileUploadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(MaxPhotoUploadBytes)]
    public async Task<IActionResult> UploadPhoto(IFormFile file)
    {
        if (file is null || file.Length == 0)
        {
            return Problem("A photo file is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (file.Length > MaxPhotoUploadBytes)
        {
            return Problem($"Photos must be {MaxPhotoUploadBytes / (1024 * 1024)}MB or smaller.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!AllowedPhotoContentTypes.Contains(file.ContentType))
        {
            return Problem("Only JPEG, PNG, or WebP photos are accepted.", statusCode: StatusCodes.Status400BadRequest);
        }

        await using var stream = file.OpenReadStream();
        var storedRef = await _fileStorageService.SaveAsync(stream, file.FileName, file.ContentType);
        var absoluteUrl = FileReferenceUrl.ToAbsolute(storedRef, Request.Scheme, Request.Host.ToString());
        return Ok(new ProviderFileUploadResponse(absoluteUrl));
    }

    private const long MaxPhotoUploadBytes = 8 * 1024 * 1024;

    private static readonly HashSet<string> AllowedPhotoContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
    };

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
        if (!Enum.TryParse<Nestly.Domain.ProviderKycDocumentType>(body.DocType, ignoreCase: true, out var docType))
        {
            ModelState.AddModelError(nameof(body.DocType), $"'{body.DocType}' is not a valid document type.");
            return ValidationProblem(ModelState);
        }

        var request = new SubmitProviderKycDocumentRequest(CurrentProviderId(), docType, body.FileRef, body.DocNumber);
        var validation = await _kycDocumentValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _kycService.SubmitDocumentAsync(request);
        return result.IsSuccess ? StatusCode(StatusCodes.Status201Created, result.Value) : result.ToProblemResult();
    }

    /// <summary>
    /// Uploads a KYC document file and returns its URL for
    /// <see cref="SubmitKycDocument"/>'s <c>FileRef</c> — same
    /// upload-then-submit split as <see cref="UploadPhoto"/>/<see cref="UpdatePhoto"/>.
    /// Accepts PDF in addition to the photo allowlist: identity/address/bank
    /// proofs are commonly scanned or exported as PDF, unlike a profile photo.
    /// </summary>
    [HttpPost("kyc/documents/upload")]
    [ProducesResponseType(typeof(ProviderFileUploadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(MaxKycUploadBytes)]
    public async Task<IActionResult> UploadKycDocument(IFormFile file)
    {
        if (file is null || file.Length == 0)
        {
            return Problem("A document file is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (file.Length > MaxKycUploadBytes)
        {
            return Problem($"Documents must be {MaxKycUploadBytes / (1024 * 1024)}MB or smaller.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!AllowedKycContentTypes.Contains(file.ContentType))
        {
            return Problem("Only JPEG, PNG, WebP images or PDF documents are accepted.", statusCode: StatusCodes.Status400BadRequest);
        }

        await using var stream = file.OpenReadStream();
        var storedRef = await _fileStorageService.SaveAsync(stream, file.FileName, file.ContentType);
        var absoluteUrl = FileReferenceUrl.ToAbsolute(storedRef, Request.Scheme, Request.Host.ToString());
        return Ok(new ProviderFileUploadResponse(absoluteUrl));
    }

    private const long MaxKycUploadBytes = 8 * 1024 * 1024;

    private static readonly HashSet<string> AllowedKycContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "application/pdf",
    };

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

    /// <summary>
    /// Go-live checklist (docs/OPEN-FIXES-FEATURES.csv "Provider Web,
    /// Proposed new page, Onboarding checklist and go-live status"): names
    /// the specific prerequisites the caller is still missing before they can
    /// start receiving work, so provider-web can show a persistent banner and
    /// a checklist naming the exact gap instead of an unexplained empty jobs
    /// list.
    /// </summary>
    [HttpGet("go-live-status")]
    [ProducesResponseType(typeof(ProviderGoLiveStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetGoLiveStatus()
    {
        var result = await _profileService.GetGoLiveStatusAsync(CurrentProviderId());
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>
    /// Permanently deletes the caller's own account (right to erasure).
    /// Job/earnings history is retained under this provider id for
    /// financial/legal reasons, but personal fields are anonymized and every
    /// active session is revoked immediately - login is impossible from this
    /// point on. Mirrors consumer-api's <c>CustomerProfileController.DeleteAccount</c>.
    /// </summary>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAccount()
    {
        var result = await _profileService.DeleteAccountAsync(CurrentProviderId());
        return result.IsSuccess ? NoContent() : result.ToProblemResult();
    }

    private Guid CurrentProviderId() =>
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

/// <summary>
/// Request body for <see cref="ProfileController.SubmitKycDocument"/> — the
/// provider id is deliberately excluded here (unlike
/// <see cref="SubmitProviderKycDocumentRequest"/>) and taken from the JWT
/// instead, so a caller can never submit a document against another
/// provider's id (SRS 28.3 IDOR). <c>DocType</c> is a string (its enum's
/// name, e.g. "IdentityProof") rather than the raw enum type: provider-api
/// has no JsonStringEnumConverter registered, so binding the enum directly
/// here would require the wire format to be its ordinal number instead -
/// inconsistent with <see cref="ProviderKycDocumentResponse"/>'s own DocType,
/// which is already a string for the same reason.
/// </summary>
public record SubmitProviderKycDocumentBody(string DocType, string FileRef, string? DocNumber);
