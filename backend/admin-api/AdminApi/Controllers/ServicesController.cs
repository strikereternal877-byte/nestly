using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Nestly.Application.Catalog;
using Nestly.BuildingBlocks.Extensions;
using Nestly.Domain;
using Nestly.Infrastructure;

namespace Nestly.AdminApi.Controllers;

/// <summary>
/// Admin service/package management (SRS 12.6, task 105): CRUD over the full
/// field set, service option flags (12.6.3), gallery media, activation and
/// featuring. Gated behind the "catalog" permission module, same as
/// <see cref="CategoriesController"/> (SRS 12.5-12.7 share one module).
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/catalog/services")]
[Authorize(AuthenticationSchemes = DependencyInjection.AdminJwtBearerScheme)]
public class ServicesController : ControllerBase
{
    private const string ReadPolicy = AdminModules.Catalog + ".read";
    private const string WritePolicy = AdminModules.Catalog + ".write";

    private readonly IServiceManagementService _serviceManagementService;
    private readonly IValidator<ServiceCreateRequest> _createValidator;
    private readonly IValidator<ServiceUpdateRequest> _updateValidator;
    private readonly IValidator<ServiceMediaCreateRequest> _mediaCreateValidator;

    public ServicesController(
        IServiceManagementService serviceManagementService,
        IValidator<ServiceCreateRequest> createValidator,
        IValidator<ServiceUpdateRequest> updateValidator,
        IValidator<ServiceMediaCreateRequest> mediaCreateValidator)
    {
        _serviceManagementService = serviceManagementService;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _mediaCreateValidator = mediaCreateValidator;
    }

    [HttpGet]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<ServiceAdminResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] Guid? categoryId) =>
        Ok(await _serviceManagementService.ListAsync(categoryId));

    /// <summary>
    /// docs/OPEN-FIXES-FEATURES.csv "Admin Web, Proposed new page, Catalog
    /// health": active services missing a city price, a cover image, an
    /// active serviceability mapping, or that have never been booked.
    /// Warning/audit only, same non-destructive approach as
    /// ServiceabilityMappingsController's unmapped-active-services and
    /// coverage-gap endpoints - see <see cref="IServiceManagementService.ListHealthIssuesAsync"/>.
    /// </summary>
    [HttpGet("health")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<CatalogHealthIssueResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListHealth() => Ok(await _serviceManagementService.ListHealthIssuesAsync());

    [HttpGet("{id:guid}")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(ServiceAdminResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await _serviceManagementService.GetByIdAsync(id);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    [HttpPost]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(ServiceAdminResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] ServiceCreateRequest request)
    {
        var validation = await _createValidator.ValidateAsync(request);
        if (!validation.IsValid) return ValidationProblem(ToModelState(validation));

        var result = await _serviceManagementService.CreateAsync(request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(ServiceAdminResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Update(Guid id, [FromBody] ServiceUpdateRequest request)
    {
        var validation = await _updateValidator.ValidateAsync(request);
        if (!validation.IsValid) return ValidationProblem(ToModelState(validation));

        var result = await _serviceManagementService.UpdateAsync(id, request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    [HttpPost("{id:guid}/activate")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Activate(Guid id)
    {
        var result = await _serviceManagementService.SetActiveAsync(id, true);
        return result.IsSuccess ? NoContent() : result.ToProblemResult();
    }

    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Deactivate(Guid id)
    {
        var result = await _serviceManagementService.SetActiveAsync(id, false);
        return result.IsSuccess ? NoContent() : result.ToProblemResult();
    }

    [HttpPost("{id:guid}/feature")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Feature(Guid id)
    {
        var result = await _serviceManagementService.SetFeaturedAsync(id, true);
        return result.IsSuccess ? NoContent() : result.ToProblemResult();
    }

    [HttpPost("{id:guid}/unfeature")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unfeature(Guid id)
    {
        var result = await _serviceManagementService.SetFeaturedAsync(id, false);
        return result.IsSuccess ? NoContent() : result.ToProblemResult();
    }

    // ---- Gallery media (SRS 12.6.2 "Gallery images") ----

    [HttpGet("{id:guid}/media")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<ServiceMediaResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListMedia(Guid id)
    {
        var result = await _serviceManagementService.ListMediaAsync(id);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    [HttpPost("{id:guid}/media")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(ServiceMediaResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> AddMedia(Guid id, [FromBody] ServiceMediaCreateRequest request)
    {
        var validation = await _mediaCreateValidator.ValidateAsync(request);
        if (!validation.IsValid) return ValidationProblem(ToModelState(validation));

        var result = await _serviceManagementService.AddMediaAsync(id, request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    [HttpDelete("{id:guid}/media/{mediaId:guid}")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveMedia(Guid id, Guid mediaId)
    {
        var result = await _serviceManagementService.RemoveMediaAsync(id, mediaId);
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
