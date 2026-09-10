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

    /// <summary>
    /// docs/OPEN-FIXES-FEATURES.csv "Service to pincode mapping": service/
    /// pincode pairs where an active provider already has matching skill +
    /// area coverage but no active mapping exists to make it bookable there.
    /// Auto-enabled proactively now by <see cref="AutoEnableProviderCoverageAsync"/>
    /// whenever a provider's skills or service areas change - this listing
    /// stays as an admin-visible audit/history view of what auto-enable has
    /// done, and a safety net for any edge case it does not cover (a mapping
    /// deactivated by an admin after auto-enable created it, a provider
    /// activated by a path that does not call the trigger, etc.).
    /// </summary>
    Task<IReadOnlyList<ServiceabilityCoverageGapResponse>> ListPincodesWithProviderCoverageButNoServiceMappingAsync();

    /// <summary>
    /// docs/OPEN-FIXES-FEATURES.csv "Service to pincode mapping": call this
    /// after a provider's <c>ProviderSkillMapping</c> or
    /// <c>ProviderServiceArea</c> rows are created/activated (provider
    /// onboarding's skill/area replace, or a provider going Active/
    /// reactivated with coverage already on file). Creates or reactivates the
    /// <see cref="Nestly.Domain.ServicePincodeMapping"/> for every (service,
    /// pincode) pair the provider is now newly eligible to fulfil, reusing
    /// <see cref="CreateServicePincodeMappingAsync"/>'s existing
    /// create-or-reactivate path so this is idempotent - re-running it for
    /// coverage that is already mapped, or that adds nothing new, creates no
    /// duplicate and does not touch a mapping an admin deliberately mapped or
    /// suspended for a different reason. Returns the number of mappings
    /// created or reactivated, for logging/testing. The reverse direction -
    /// coverage lost - is <see cref="AutoDisableUnservedMappingsAsync"/>.
    /// </summary>
    Task<int> AutoEnableProviderCoverageAsync(Guid providerId);

    /// <summary>
    /// Snapshot of (service, pincode) pairs that are currently actively
    /// mapped and that this provider is currently propping up (their
    /// existing skill + area rows would still satisfy the pair). Call this
    /// BEFORE a provider's skill/area rows are replaced
    /// (<c>ProviderProfileService.UpdateServiceAreasAsync</c>/
    /// <c>UpdateSkillsAsync</c>) - the replace deletes the very rows this
    /// depends on - then pass the result to
    /// <see cref="AutoDisableUnservedMappingsAsync"/> once the replace has
    /// completed. Not needed before a plain status change (suspend/delete),
    /// where the provider's skill/area rows are untouched - see that
    /// method's default-candidates behaviour.
    /// </summary>
    Task<IReadOnlyList<ServiceabilityCoverageGapResponse>> ListMappedPairsCoveredByProviderAsync(Guid providerId);

    /// <summary>
    /// docs/OPEN-FIXES-FEATURES.csv "Service to pincode mapping" - the
    /// reverse of <see cref="AutoEnableProviderCoverageAsync"/>, added on
    /// follow-up review ("auto deactivating is required as well"). Call this
    /// wherever a provider can lose skill/area coverage or go inactive:
    /// after a skill/area replace (pass <paramref name="candidatePairs"/> -
    /// a snapshot taken from <see cref="ListMappedPairsCoveredByProviderAsync"/>
    /// BEFORE the replace, since by the time this runs the provider's own
    /// rows already reflect the new set and no longer show what was lost),
    /// or after a provider is suspended/deactivated (omit
    /// <paramref name="candidatePairs"/> - their skill/area rows are
    /// untouched by a status change, so the current
    /// <see cref="ListMappedPairsCoveredByProviderAsync"/> snapshot IS the
    /// "before" picture). For every candidate pair, deactivates the
    /// <see cref="Nestly.Domain.ServicePincodeMapping"/> only if NO active
    /// provider - this one or any other - still covers it, via the same
    /// deactivate path <see cref="DeactivateServicePincodeMappingAsync"/>
    /// uses. Idempotent: a pair no longer actively mapped, or still covered
    /// by someone, is left alone; re-running finds nothing left to disable.
    /// Returns the number of mappings deactivated, for logging/testing.
    /// </summary>
    Task<int> AutoDisableUnservedMappingsAsync(Guid providerId, IReadOnlyList<ServiceabilityCoverageGapResponse>? candidatePairs = null);
}
