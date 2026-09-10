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

/// <summary>
/// docs/OPEN-FIXES-FEATURES.csv "Serviceability and provider skills ...
/// Service to pincode mapping": a service/pincode pair where an active
/// provider already covers this pincode with a matching skill (the same
/// skill + area eligibility <c>ProviderMatchingService.FindCandidatesAsync</c>
/// uses to find booking candidates) but the admin serviceability mapping for
/// that pair is missing or suspended, so the pincode still shows the service
/// as unbookable. Provider onboarding and the service/pincode mapping table
/// are entirely separate and manually maintained today - surfaced to admins
/// as a warning to investigate and map, not derived/auto-enabled
/// automatically, matching <see cref="UnmappedActiveServiceResponse"/>'s
/// same non-destructive approach (see
/// <see cref="IServiceabilityMappingManagementService.ListPincodesWithProviderCoverageButNoServiceMappingAsync"/>).
/// </summary>
public sealed record ServiceabilityCoverageGapResponse(
    Guid ServiceId,
    string ServiceName,
    Guid PincodeId,
    string PincodeCode);

/// <summary>
/// docs/OPEN-FIXES-FEATURES.csv "Admin Web, Proposed new page, Coverage gap
/// map": the third grid category - an active
/// <see cref="Nestly.Domain.ServicePincodeMapping"/> exists for this
/// (service, pincode) pair, so the pincode shows the service as bookable, but
/// no active provider actually has matching skill + area coverage to fulfil
/// it - a mapping that looks serviceable but isn't. Purely informational
/// (see <see cref="IServiceabilityMappingManagementService.ListMappedPincodesWithoutProviderCoverageAsync"/>'s
/// doc comment for why there is no create/fix action here, unlike
/// <see cref="UnmappedActiveServiceResponse"/> and
/// <see cref="ServiceabilityCoverageGapResponse"/>): the gap is in provider
/// onboarding, not in the mapping table.
/// </summary>
public sealed record MappedPincodeWithoutProviderCoverageResponse(
    Guid MappingId,
    Guid ServiceId,
    string ServiceName,
    Guid PincodeId,
    string PincodeCode);
