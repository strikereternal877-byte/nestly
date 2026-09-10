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
}
