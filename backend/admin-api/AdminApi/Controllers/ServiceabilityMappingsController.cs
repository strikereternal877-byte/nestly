using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Nestly.Application.Serviceability;
using Nestly.BuildingBlocks.Extensions;
using Nestly.Domain;
using Nestly.Infrastructure;

namespace Nestly.AdminApi.Controllers;

/// <summary>
/// Admin category/city and service/pincode serviceability mapping (SRS
/// 12.9.2, task 111): which categories are active in which city, which
/// services are active in which pincode, and blackout/suspension via
/// deactivation. Gated behind the "serviceability" module, same as
/// <see cref="GeographyController"/>.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/serviceability-mappings")]
[Authorize(AuthenticationSchemes = DependencyInjection.AdminJwtBearerScheme)]
public class ServiceabilityMappingsController : ControllerBase
{
    private const string ReadPolicy = AdminModules.Serviceability + ".read";
    private const string WritePolicy = AdminModules.Serviceability + ".write";

    private readonly IServiceabilityMappingManagementService _mappingManagementService;
    private readonly IValidator<CategoryCityMappingCreateRequest> _categoryCityCreateValidator;
    private readonly IValidator<ServicePincodeMappingCreateRequest> _servicePincodeCreateValidator;

    public ServiceabilityMappingsController(
        IServiceabilityMappingManagementService mappingManagementService,
        IValidator<CategoryCityMappingCreateRequest> categoryCityCreateValidator,
        IValidator<ServicePincodeMappingCreateRequest> servicePincodeCreateValidator)
    {
        _mappingManagementService = mappingManagementService;
        _categoryCityCreateValidator = categoryCityCreateValidator;
        _servicePincodeCreateValidator = servicePincodeCreateValidator;
    }

    // ---- Lookups (for the mapping screen's pickers; cities/pincodes come from GeographyController) ----

    [HttpGet("categories")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<CategoryLookupResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListCategories() => Ok(await _mappingManagementService.ListCategoriesAsync());

    [HttpGet("services")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<ServiceLookupResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListServices() => Ok(await _mappingManagementService.ListServicesAsync());

    // ---- Category <-> City ----

    [HttpGet("category-city")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<CategoryCityMappingResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListCategoryCityMappings([FromQuery] Guid? categoryId, [FromQuery] Guid? cityId) =>
        Ok(await _mappingManagementService.ListCategoryCityMappingsAsync(categoryId, cityId));

    [HttpPost("category-city")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(CategoryCityMappingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateCategoryCityMapping([FromBody] CategoryCityMappingCreateRequest request)
    {
        var validation = await _categoryCityCreateValidator.ValidateAsync(request);
        if (!validation.IsValid) return ValidationProblem(ToModelState(validation));

        var result = await _mappingManagementService.CreateCategoryCityMappingAsync(request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    [HttpPost("category-city/{id:guid}/activate")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ActivateCategoryCityMapping(Guid id)
    {
        var result = await _mappingManagementService.ActivateCategoryCityMappingAsync(id);
        return result.IsSuccess ? NoContent() : result.ToProblemResult();
    }

    /// <summary>Suspends a category's serviceability in a city (SRS 12.9.2 "Service blackout in selected areas").</summary>
    [HttpPost("category-city/{id:guid}/deactivate")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeactivateCategoryCityMapping(Guid id)
    {
        var result = await _mappingManagementService.DeactivateCategoryCityMappingAsync(id);
        return result.IsSuccess ? NoContent() : result.ToProblemResult();
    }

    // ---- Service <-> Pincode ----

    [HttpGet("service-pincode")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<ServicePincodeMappingResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListServicePincodeMappings([FromQuery] Guid? serviceId, [FromQuery] Guid? pincodeId) =>
        Ok(await _mappingManagementService.ListServicePincodeMappingsAsync(serviceId, pincodeId));

    [HttpPost("service-pincode")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(ServicePincodeMappingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateServicePincodeMapping([FromBody] ServicePincodeMappingCreateRequest request)
    {
        var validation = await _servicePincodeCreateValidator.ValidateAsync(request);
        if (!validation.IsValid) return ValidationProblem(ToModelState(validation));

        var result = await _mappingManagementService.CreateServicePincodeMappingAsync(request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    [HttpPost("service-pincode/{id:guid}/activate")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ActivateServicePincodeMapping(Guid id)
    {
        var result = await _mappingManagementService.ActivateServicePincodeMappingAsync(id);
        return result.IsSuccess ? NoContent() : result.ToProblemResult();
    }

    /// <summary>Suspends a service's serviceability in a pincode (SRS 12.9.2 "Temporary service suspension").</summary>
    [HttpPost("service-pincode/{id:guid}/deactivate")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeactivateServicePincodeMapping(Guid id)
    {
        var result = await _mappingManagementService.DeactivateServicePincodeMappingAsync(id);
        return result.IsSuccess ? NoContent() : result.ToProblemResult();
    }

    /// <summary>
    /// docs/OPEN-FIXES-FEATURES.csv "Service pincode mapping coverage": a
    /// warning list, not a blocker - every active service that has no active
    /// pincode mapping anywhere, so an admin can catch a launched-but-
    /// unbookable service (like the AC installation flagship service the CSV
    /// row describes) before a customer does.
    /// </summary>
    [HttpGet("unmapped-active-services")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<UnmappedActiveServiceResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListUnmappedActiveServices() =>
        Ok(await _mappingManagementService.ListUnmappedActiveServicesAsync());

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
