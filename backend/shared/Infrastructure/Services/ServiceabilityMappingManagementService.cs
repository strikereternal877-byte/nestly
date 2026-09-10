using Nestly.Application;
using Nestly.Application.Serviceability;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>Admin CRUD over category/city and service/pincode serviceability mappings (SRS 12.9.2, task 111).</summary>
public class ServiceabilityMappingManagementService : IServiceabilityMappingManagementService
{
    private readonly ICategoryCityMappingRepository _categoryCityMappingRepository;
    private readonly IServicePincodeMappingRepository _servicePincodeMappingRepository;
    private readonly ICategoryRepository _categoryRepository;
    private readonly ICityRepository _cityRepository;
    private readonly IServiceRepository _serviceRepository;
    private readonly IPincodeRepository _pincodeRepository;

    public ServiceabilityMappingManagementService(
        ICategoryCityMappingRepository categoryCityMappingRepository,
        IServicePincodeMappingRepository servicePincodeMappingRepository,
        ICategoryRepository categoryRepository,
        ICityRepository cityRepository,
        IServiceRepository serviceRepository,
        IPincodeRepository pincodeRepository)
    {
        _categoryCityMappingRepository = categoryCityMappingRepository;
        _servicePincodeMappingRepository = servicePincodeMappingRepository;
        _categoryRepository = categoryRepository;
        _cityRepository = cityRepository;
        _serviceRepository = serviceRepository;
        _pincodeRepository = pincodeRepository;
    }

    public async Task<IReadOnlyList<CategoryLookupResponse>> ListCategoriesAsync()
    {
        // Empty query matches every active category - SearchActiveAsync's
        // Contains("") is always true - reused here rather than adding a
        // parallel "list all" repository method for what is otherwise the
        // same query.
        var categories = await _categoryRepository.SearchActiveAsync(string.Empty);
        return categories.Select(c => new CategoryLookupResponse(c.Id, c.Name)).ToList();
    }

    public async Task<IReadOnlyList<ServiceLookupResponse>> ListServicesAsync()
    {
        var services = await _serviceRepository.SearchActiveAsync(string.Empty);
        return services.Select(s => new ServiceLookupResponse(s.Id, s.Name)).ToList();
    }

    public Task<IReadOnlyList<CategoryCityMappingResponse>> ListCategoryCityMappingsAsync(Guid? categoryId, Guid? cityId) =>
        _categoryCityMappingRepository.ListAsync(categoryId, cityId);

    public async Task<Result<CategoryCityMappingResponse>> CreateCategoryCityMappingAsync(CategoryCityMappingCreateRequest request)
    {
        var category = await _categoryRepository.GetByIdAsync(request.CategoryId);
        if (category is null)
        {
            return Error.NotFound("Serviceability.CategoryNotFound", "The specified category does not exist.");
        }

        var city = await _cityRepository.GetByIdAsync(request.CityId);
        if (city is null)
        {
            return Error.NotFound("Serviceability.CityNotFound", "The specified city does not exist.");
        }

        var existing = await _categoryCityMappingRepository.FindAsync(request.CategoryId, request.CityId);
        if (existing is not null)
        {
            // A mapping already exists - re-enable it if it was suspended
            // rather than failing on the unique-index conflict (see the
            // interface doc comment for why this is idempotent).
            if (!existing.IsActive)
            {
                existing.Activate();
                await _categoryCityMappingRepository.UpdateAsync(existing);
            }

            return new CategoryCityMappingResponse(existing.Id, category.Id, category.Name, city.Id, city.Name, existing.IsActive);
        }

        var mapping = new CategoryCityMapping(Guid.NewGuid(), request.CategoryId, request.CityId);
        await _categoryCityMappingRepository.AddAsync(mapping);
        return new CategoryCityMappingResponse(mapping.Id, category.Id, category.Name, city.Id, city.Name, mapping.IsActive);
    }

    public async Task<Result> ActivateCategoryCityMappingAsync(Guid id)
    {
        var mapping = await _categoryCityMappingRepository.GetByIdAsync(id);
        if (mapping is null)
        {
            return Result.Failure(Error.NotFound("Serviceability.MappingNotFound", "The specified mapping does not exist."));
        }

        mapping.Activate();
        await _categoryCityMappingRepository.UpdateAsync(mapping);
        return Result.Success();
    }

    public async Task<Result> DeactivateCategoryCityMappingAsync(Guid id)
    {
        var mapping = await _categoryCityMappingRepository.GetByIdAsync(id);
        if (mapping is null)
        {
            return Result.Failure(Error.NotFound("Serviceability.MappingNotFound", "The specified mapping does not exist."));
        }

        mapping.Deactivate();
        await _categoryCityMappingRepository.UpdateAsync(mapping);
        return Result.Success();
    }

    public Task<IReadOnlyList<ServicePincodeMappingResponse>> ListServicePincodeMappingsAsync(Guid? serviceId, Guid? pincodeId) =>
        _servicePincodeMappingRepository.ListAsync(serviceId, pincodeId);

    public async Task<Result<ServicePincodeMappingResponse>> CreateServicePincodeMappingAsync(ServicePincodeMappingCreateRequest request)
    {
        var service = await _serviceRepository.GetByIdAsync(request.ServiceId);
        if (service is null)
        {
            return Error.NotFound("Serviceability.ServiceNotFound", "The specified service does not exist.");
        }

        var pincode = await _pincodeRepository.GetByIdAsync(request.PincodeId);
        if (pincode is null)
        {
            return Error.NotFound("Serviceability.PincodeNotFound", "The specified pincode does not exist.");
        }

        var existing = await _servicePincodeMappingRepository.FindAsync(request.ServiceId, request.PincodeId);
        if (existing is not null)
        {
            if (!existing.IsActive)
            {
                existing.Activate();
                await _servicePincodeMappingRepository.UpdateAsync(existing);
            }

            return new ServicePincodeMappingResponse(existing.Id, service.Id, service.Name, pincode.Id, pincode.Code, existing.IsActive);
        }

        var mapping = new ServicePincodeMapping(Guid.NewGuid(), request.ServiceId, request.PincodeId);
        await _servicePincodeMappingRepository.AddAsync(mapping);
        return new ServicePincodeMappingResponse(mapping.Id, service.Id, service.Name, pincode.Id, pincode.Code, mapping.IsActive);
    }

    public async Task<Result> ActivateServicePincodeMappingAsync(Guid id)
    {
        var mapping = await _servicePincodeMappingRepository.GetByIdAsync(id);
        if (mapping is null)
        {
            return Result.Failure(Error.NotFound("Serviceability.MappingNotFound", "The specified mapping does not exist."));
        }

        mapping.Activate();
        await _servicePincodeMappingRepository.UpdateAsync(mapping);
        return Result.Success();
    }

    public async Task<Result> DeactivateServicePincodeMappingAsync(Guid id)
    {
        var mapping = await _servicePincodeMappingRepository.GetByIdAsync(id);
        if (mapping is null)
        {
            return Result.Failure(Error.NotFound("Serviceability.MappingNotFound", "The specified mapping does not exist."));
        }

        mapping.Deactivate();
        await _servicePincodeMappingRepository.UpdateAsync(mapping);
        return Result.Success();
    }

    public Task<IReadOnlyList<UnmappedActiveServiceResponse>> ListUnmappedActiveServicesAsync() =>
        _servicePincodeMappingRepository.ListUnmappedActiveServicesAsync();
}
