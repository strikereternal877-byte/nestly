namespace Nestly.Application.Serviceability;

/// <summary>Whether a category is active in a city (SRS 12.9.2), for the admin mapping screen.</summary>
public sealed record CategoryCityMappingResponse(
    Guid Id,
    Guid CategoryId,
    string CategoryName,
    Guid CityId,
    string CityName,
    bool IsActive);

/// <summary>Admin request to map a category as active in a city (SRS 12.9.2).</summary>
public sealed record CategoryCityMappingCreateRequest(Guid CategoryId, Guid CityId);

/// <summary>Whether a service is active in a pincode (SRS 12.9.2), for the admin mapping screen.</summary>
public sealed record ServicePincodeMappingResponse(
    Guid Id,
    Guid ServiceId,
    string ServiceName,
    Guid PincodeId,
    string PincodeCode,
    bool IsActive);

/// <summary>Admin request to map a service as active in a pincode (SRS 12.9.2).</summary>
public sealed record ServicePincodeMappingCreateRequest(Guid ServiceId, Guid PincodeId);

/// <summary>Lightweight category lookup for the mapping admin screen's category picker.</summary>
public sealed record CategoryLookupResponse(Guid Id, string Name);

/// <summary>Lightweight service lookup for the mapping admin screen's service picker.</summary>
public sealed record ServiceLookupResponse(Guid Id, string Name);

/// <summary>
/// docs/OPEN-FIXES-FEATURES.csv "Serviceability mappings ... Service pincode
/// mapping coverage": an active service with zero active
/// <see cref="Nestly.Domain.ServicePincodeMapping"/> rows - launched, but
/// silently unbookable everywhere, since <c>SlotAvailabilityService</c> and
/// <c>ServiceabilityValidationService</c> both fail closed on exactly this
/// (no active mapping means not serviceable, by design - see
/// BookabilityProbe's identical reasoning for the platform-wide version of
/// this same gap). Surfaced to admins as a warning to investigate and map,
/// not blocked - see <see cref="IServiceabilityMappingManagementService.ListUnmappedActiveServicesAsync"/>.
/// </summary>
public sealed record UnmappedActiveServiceResponse(
    Guid ServiceId,
    string ServiceName,
    string ServiceSlug,
    Guid CategoryId,
    string CategoryName);
