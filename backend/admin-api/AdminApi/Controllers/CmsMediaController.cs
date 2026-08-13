using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Nestly.Application.Cms;
using Nestly.Application.Storage;
using Nestly.BuildingBlocks.Extensions;
using Nestly.Domain;
using Nestly.Infrastructure;

namespace Nestly.AdminApi.Controllers;

/// <summary>
/// Admin CMS media library management (SRS 12.16.2 "media upload support",
/// task 124e): CRUD over the asset library <see cref="Banner"/> draws its
/// image from, plus <see cref="Upload"/> (task 314) - a genuine file upload
/// via <c>IFileStorageService</c>, the same abstraction provider-web's
/// job-completion photos use. <c>Create</c>/<c>Update</c> still accept a
/// hand-typed URL directly (an already-hosted external image, or a CDN
/// asset uploaded outside this app) - the two are not mutually exclusive,
/// every <see cref="Nestly.Domain.CmsMedia"/> row is just a URL either way.
/// Read-only actions require "cms.read"; every mutating action requires
/// "cms.write" (task 96b/96c), matching <c>CouponsController</c>'s
/// per-action policy split.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/cms/media")]
[Authorize(AuthenticationSchemes = DependencyInjection.AdminJwtBearerScheme)]
public class CmsMediaController : ControllerBase
{
    private const string ReadPolicy = AdminModules.Cms + ".read";
    private const string WritePolicy = AdminModules.Cms + ".write";

    private readonly ICmsMediaService _mediaService;
    private readonly IValidator<CmsMediaCreateRequest> _createValidator;
    private readonly IValidator<CmsMediaUpdateRequest> _updateValidator;

    public CmsMediaController(
        ICmsMediaService mediaService,
        IValidator<CmsMediaCreateRequest> createValidator,
        IValidator<CmsMediaUpdateRequest> updateValidator)
    {
        _mediaService = mediaService;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
    }

    /// <summary>Every media asset, newest first (task 124e).</summary>
    [HttpGet]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<CmsMediaResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List() => Ok(await _mediaService.ListAsync());

    /// <summary>Media asset detail.</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(CmsMediaResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await _mediaService.GetByIdAsync(id);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Registers a new media asset by URL (task 124e).</summary>
    [HttpPost]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(CmsMediaResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CmsMediaCreateRequest request)
    {
        var validation = await _createValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _mediaService.CreateAsync(request);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetById), new { id = result.Value.Id }, result.Value)
            : result.ToProblemResult();
    }

    /// <summary>
    /// Task 314: registers a new media asset from an uploaded file instead of
    /// a hand-typed URL. Validated here rather than via a FluentValidation
    /// record validator since the payload is multipart, not JSON -
    /// content-type is checked against an image allowlist and size is capped
    /// before anything is read into memory or written to disk, mirroring
    /// provider-api's <c>JobsController.UploadCompletionPhoto</c> exactly.
    /// </summary>
    [HttpPost("upload")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(CmsMediaResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> Upload(IFormFile file, [FromForm] string? altText)
    {
        if (file is null || file.Length == 0)
        {
            return Problem("A file is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (file.Length > MaxUploadBytes)
        {
            return Problem($"Files must be {MaxUploadBytes / (1024 * 1024)}MB or smaller.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!AllowedUploadContentTypes.Contains(file.ContentType))
        {
            return Problem("Only JPEG, PNG, or WebP images are accepted.", statusCode: StatusCodes.Status400BadRequest);
        }

        await using var stream = file.OpenReadStream();
        var storedRef = await _mediaService.SaveFileAsync(stream, file.FileName, file.ContentType);

        // IFileStorageService's result may already be absolute (Supabase) or
        // relative to this API's own origin (local disk) - resolved here
        // (not in the service, which has no HTTP context) so the stored
        // CmsMedia.Url works when read back from any origin: customer-web,
        // admin-web, anywhere else this library is rendered from.
        var absoluteUrl = FileReferenceUrl.ToAbsolute(storedRef, Request.Scheme, Request.Host.ToString());
        var createResult = await _mediaService.CreateAsync(new CmsMediaCreateRequest(absoluteUrl, altText));
        return createResult.IsSuccess
            ? CreatedAtAction(nameof(GetById), new { id = createResult.Value.Id }, createResult.Value)
            : createResult.ToProblemResult();
    }

    private const long MaxUploadBytes = 8 * 1024 * 1024;

    private static readonly HashSet<string> AllowedUploadContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
    };

    /// <summary>Edits a media asset's URL/alt text.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(CmsMediaResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] CmsMediaUpdateRequest request)
    {
        var validation = await _updateValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _mediaService.UpdateAsync(id, request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Deletes a media asset. Fails with a conflict if a banner still references it.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await _mediaService.DeleteAsync(id);
        return result.IsSuccess ? NoContent() : result.ToProblemResult();
    }

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
