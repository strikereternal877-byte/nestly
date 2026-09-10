using Nestly.Application.Serviceability;
using Nestly.Domain;

namespace Nestly.Application;

/// <summary>Admin CRUD over the service/pincode serviceability mapping (SRS 12.9.2).</summary>
public interface IServicePincodeMappingRepository : IRepository<ServicePincodeMapping>
{
    /// <summary>
    /// Mappings joined with service/pincode details for display, optionally
    /// filtered by service and/or pincode. Joined here rather than resolved
    /// per-row by the caller, to avoid N+1 lookups on a listing endpoint.
    /// </summary>
    Task<IReadOnlyList<ServicePincodeMappingResponse>> ListAsync(Guid? serviceId, Guid? pincodeId);

    /// <summary>The existing mapping for this (service, pincode) pair, if any - the pair is unique.</summary>
    Task<ServicePincodeMapping?> FindAsync(Guid serviceId, Guid pincodeId);

    /// <summary>
    /// Every active service with zero active service/pincode mappings (see
    /// <see cref="UnmappedActiveServiceResponse"/>'s doc comment). A service
    /// with only suspended (IsActive false) mappings counts as unmapped too -
    /// a suspended mapping is not currently serviceable, which is exactly the
    /// condition this warns about.
    /// </summary>
    Task<IReadOnlyList<UnmappedActiveServiceResponse>> ListUnmappedActiveServicesAsync();

    /// <summary>
    /// Every (service, pincode) pair where an active provider already has
    /// matching skill + area coverage (see
    /// <see cref="ServiceabilityCoverageGapResponse"/>'s doc comment) but no
    /// active <see cref="ServicePincodeMapping"/> exists for that pair - so
    /// the pincode looks unserviceable to a customer despite a qualified
    /// provider already being onboarded there.
    /// </summary>
    Task<IReadOnlyList<ServiceabilityCoverageGapResponse>> ListPincodesWithProviderCoverageButNoServiceMappingAsync();

    /// <summary>
    /// Same eligibility as <see cref="ListPincodesWithProviderCoverageButNoServiceMappingAsync"/>,
    /// scoped to the one provider whose skill or service-area coverage just
    /// changed - the trigger query behind auto-enabling serviceability (see
    /// <c>IServiceabilityMappingManagementService.AutoEnableProviderCoverageAsync</c>).
    /// </summary>
    Task<IReadOnlyList<ServiceabilityCoverageGapResponse>> ListCoverablePairsForProviderAsync(Guid providerId);

    /// <summary>
    /// (Service, pincode) pairs that ARE currently actively mapped and that
    /// this provider's current <c>ProviderSkillMapping</c>/<c>ProviderServiceArea</c>
    /// rows would satisfy - deliberately ignores the provider's own
    /// <see cref="Provider.Status"/> (unlike <see cref="ListCoverablePairsForProviderAsync"/>),
    /// since this is the "before" query auto-disable takes right before a
    /// skill/area replace or a status change removes exactly those rows -
    /// see <c>IServiceabilityMappingManagementService.AutoDisableUnservedMappingsAsync</c>.
    /// </summary>
    Task<IReadOnlyList<ServiceabilityCoverageGapResponse>> ListMappedPairsCoveredByProviderAsync(Guid providerId);

    /// <summary>
    /// Whether any active provider still has matching skill + area coverage
    /// for this exact (service, pincode) pair - same eligibility as
    /// <see cref="ListPincodesWithProviderCoverageButNoServiceMappingAsync"/>,
    /// scoped to one known pair instead of scanning every service/pincode
    /// combination, for auto-disable's per-candidate recheck after a
    /// provider's coverage changes.
    /// </summary>
    Task<bool> HasActiveProviderCoverageAsync(Guid serviceId, Guid pincodeId);

    /// <summary>
    /// Every currently-active <see cref="ServicePincodeMapping"/> for which
    /// no active provider has matching skill + area coverage (see
    /// <see cref="MappedPincodeWithoutProviderCoverageResponse"/>'s doc
    /// comment) - the "mapped but not actually fulfillable" quadrant of the
    /// coverage gap map, the inverse of
    /// <see cref="ListPincodesWithProviderCoverageButNoServiceMappingAsync"/>.
    /// </summary>
    Task<IReadOnlyList<MappedPincodeWithoutProviderCoverageResponse>> ListMappedPincodesWithoutProviderCoverageAsync();
}
