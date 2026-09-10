using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.Serviceability;

/// <summary>
/// Admin CRUD over the category/city and service/pincode serviceability
/// mappings (SRS 12.9.2). Deactivating a mapping is how admins apply a
/// temporary suspension or blackout without deleting the record - see
/// <see cref="Nestly.Domain.CategoryCityMapping"/> and
/// <see cref="Nestly.Domain.ServicePincodeMapping"/>.
/// </summary>
public interface IServiceabilityMappingManagementService
{
    /// <summary>Active categories, for the mapping screen's category picker.</summary>
    Task<IReadOnlyList<CategoryLookupResponse>> ListCategoriesAsync();

    /// <summary>Active services, for the mapping screen's service picker.</summary>
    Task<IReadOnlyList<ServiceLookupResponse>> ListServicesAsync();

    Task<IReadOnlyList<CategoryCityMappingResponse>> ListCategoryCityMappingsAsync(Guid? categoryId, Guid? cityId);

    /// <summary>
    /// Creates the mapping, or - if one already exists for this
    /// (category, city) pair but was previously suspended - reactivates it
    /// instead of failing on the unique-index conflict. A suspension is
    /// meant to be reversible without losing the record, so re-enabling
    /// serviceability should feel the same whether the mapping is brand new
    /// or was deactivated earlier.
    /// </summary>
    Task<Result<CategoryCityMappingResponse>> CreateCategoryCityMappingAsync(CategoryCityMappingCreateRequest request);

    Task<Result> ActivateCategoryCityMappingAsync(Guid id);
    Task<Result> DeactivateCategoryCityMappingAsync(Guid id);

    Task<IReadOnlyList<ServicePincodeMappingResponse>> ListServicePincodeMappingsAsync(Guid? serviceId, Guid? pincodeId);

    /// <summary>Same reactivate-if-suspended behaviour as <see cref="CreateCategoryCityMappingAsync"/>.</summary>
    Task<Result<ServicePincodeMappingResponse>> CreateServicePincodeMappingAsync(ServicePincodeMappingCreateRequest request);

    Task<Result> ActivateServicePincodeMappingAsync(Guid id);
    Task<Result> DeactivateServicePincodeMappingAsync(Guid id);

    /// <summary>
    /// docs/OPEN-FIXES-FEATURES.csv "Service pincode mapping coverage":
    /// active services with no active pincode mapping anywhere - launched,
    /// but unbookable in every city, with nothing today that would have told
    /// an admin. Non-destructive by design (see the CSV row's own suggested
    /// fix): a warning list for the serviceability screen to surface, not a
    /// guard that blocks activating a service or launching it without a
    /// mapping - a service is legitimately created before its first pincode
    /// is mapped, so hard-blocking that ordering would break the normal
    /// catalog-then-serviceability setup flow.
    /// </summary>
    Task<IReadOnlyList<UnmappedActiveServiceResponse>> ListUnmappedActiveServicesAsync();
}
