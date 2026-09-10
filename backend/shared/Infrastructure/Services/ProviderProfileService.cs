using Nestly.Application;
using Nestly.Application.ProviderProfile;
using Nestly.Application.Reviews;
using Nestly.Application.Serviceability;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Provider's own profile, service-area and skill management (task 149a,
/// PROVIDER.md API surface "Profile/Onboarding"). KYC document submission and
/// status are handled separately by <see cref="IProviderKycService"/> — kept
/// out of this service so KYC review workflow (task 150b) stays independent
/// of general profile editing.
/// </summary>
public class ProviderProfileService : IProviderProfileService
{
    private readonly IProviderRepository _providerRepository;
    private readonly IProviderServiceAreaRepository _serviceAreaRepository;
    private readonly IProviderSkillMappingRepository _skillMappingRepository;
    private readonly IReviewRepository _reviewRepository;
    private readonly IProviderSessionRepository _sessionRepository;
    private readonly IServiceabilityMappingManagementService _serviceabilityMappingManagementService;

    public ProviderProfileService(
        IProviderRepository providerRepository,
        IProviderServiceAreaRepository serviceAreaRepository,
        IProviderSkillMappingRepository skillMappingRepository,
        IReviewRepository reviewRepository,
        IProviderSessionRepository sessionRepository,
        IServiceabilityMappingManagementService serviceabilityMappingManagementService)
    {
        _providerRepository = providerRepository;
        _serviceAreaRepository = serviceAreaRepository;
        _skillMappingRepository = skillMappingRepository;
        _reviewRepository = reviewRepository;
        _sessionRepository = sessionRepository;
        _serviceabilityMappingManagementService = serviceabilityMappingManagementService;
    }

    public async Task<Result<ProviderProfileResponse>> GetAsync(Guid providerId)
    {
        var provider = await _providerRepository.GetByIdAsync(providerId);
        if (provider is null)
        {
            return Result.Failure<ProviderProfileResponse>(
                Error.NotFound("ProviderProfile.NotFound", "The specified provider does not exist."));
        }

        return Result.Success(await ToResponseAsync(provider));
    }

    public async Task<Result<ProviderProfileResponse>> UpdateAsync(Guid providerId, UpdateProviderProfileRequest request)
    {
        var provider = await _providerRepository.GetByIdAsync(providerId);
        if (provider is null)
        {
            return Result.Failure<ProviderProfileResponse>(
                Error.NotFound("ProviderProfile.NotFound", "The specified provider does not exist."));
        }

        provider.UpdateProfile(request.LegalName, request.DisplayName, request.Email);
        await _providerRepository.UpdateAsync(provider);

        return Result.Success(await ToResponseAsync(provider));
    }

    /// <inheritdoc/>
    public async Task<Result<ProviderProfileResponse>> UpdatePhotoAsync(Guid providerId, UpdateProviderPhotoRequest request)
    {
        var provider = await _providerRepository.GetByIdAsync(providerId);
        if (provider is null)
        {
            return Result.Failure<ProviderProfileResponse>(
                Error.NotFound("ProviderProfile.NotFound", "The specified provider does not exist."));
        }

        if (string.IsNullOrWhiteSpace(request.PhotoUrl))
        {
            provider.RemovePhoto();
        }
        else
        {
            provider.SubmitPhoto(request.PhotoUrl);
        }

        await _providerRepository.UpdateAsync(provider);

        return Result.Success(await ToResponseAsync(provider));
    }

    public async Task<IReadOnlyList<ProviderServiceAreaResponse>> GetServiceAreasAsync(Guid providerId)
    {
        var areas = await _serviceAreaRepository.GetByProviderAsync(providerId);
        return areas.Select(ToResponse).ToList();
    }

    public async Task<Result<IReadOnlyList<ProviderServiceAreaResponse>>> UpdateServiceAreasAsync(
        Guid providerId, UpdateProviderServiceAreasRequest request)
    {
        if (!await _providerRepository.ExistsAsync(providerId))
        {
            return Result.Failure<IReadOnlyList<ProviderServiceAreaResponse>>(
                Error.NotFound("ProviderProfile.NotFound", "The specified provider does not exist."));
        }

        // Bug 3 auto-disable: snapshot what this provider is currently
        // propping up BEFORE the replace below deletes their old area rows -
        // see AutoDisableUnservedMappingsAsync's doc comment for why the
        // "before" picture has to be taken here, not after.
        var previouslyCovered = await _serviceabilityMappingManagementService.ListMappedPairsCoveredByProviderAsync(providerId);

        var areas = request.Areas
            .Select(a => new ProviderServiceArea(Guid.NewGuid(), providerId, a.CityId, a.ZoneId, a.PincodeId))
            .ToList();
        await _serviceAreaRepository.ReplaceForProviderAsync(providerId, areas);

        // Bug 3 auto-enable (docs/OPEN-FIXES-FEATURES.csv "Service to
        // pincode mapping"): new/re-added coverage here can newly make a
        // service fulfillable in a pincode - auto-create or reactivate the
        // matching ServicePincodeMapping(s) rather than leaving it to a
        // warning an admin has to notice. See the management service's doc
        // comment for why this is safe to call unconditionally (idempotent,
        // reuses the existing create-or-reactivate path).
        await _serviceabilityMappingManagementService.AutoEnableProviderCoverageAsync(providerId);

        // Bug 3 auto-disable: the reverse - an area dropped here can be the
        // last thing keeping a mapping bookable; deactivate any mapping that
        // depended on the coverage snapshotted above and now has no active
        // provider (this one or any other) left covering it.
        await _serviceabilityMappingManagementService.AutoDisableUnservedMappingsAsync(providerId, previouslyCovered);

        return Result.Success<IReadOnlyList<ProviderServiceAreaResponse>>(areas.Select(ToResponse).ToList());
    }

    public async Task<IReadOnlyList<ProviderSkillResponse>> GetSkillsAsync(Guid providerId)
    {
        var skills = await _skillMappingRepository.GetByProviderAsync(providerId);
        return skills.Select(ToResponse).ToList();
    }

    public async Task<Result<IReadOnlyList<ProviderSkillResponse>>> UpdateSkillsAsync(
        Guid providerId, UpdateProviderSkillsRequest request)
    {
        if (!await _providerRepository.ExistsAsync(providerId))
        {
            return Result.Failure<IReadOnlyList<ProviderSkillResponse>>(
                Error.NotFound("ProviderProfile.NotFound", "The specified provider does not exist."));
        }

        // Same Bug 3 auto-disable snapshot as UpdateServiceAreasAsync,
        // taken before the replace below removes this provider's old skill
        // rows.
        var previouslyCovered = await _serviceabilityMappingManagementService.ListMappedPairsCoveredByProviderAsync(providerId);

        var skills = request.Skills
            .Select(s => new ProviderSkillMapping(Guid.NewGuid(), providerId, s.CategoryId, s.ServiceId))
            .ToList();
        await _skillMappingRepository.ReplaceForProviderAsync(providerId, skills);

        // Same Bug 3 auto-enable as UpdateServiceAreasAsync - a newly added
        // skill can be the missing half of coverage for a pincode the
        // provider already serves.
        await _serviceabilityMappingManagementService.AutoEnableProviderCoverageAsync(providerId);

        // Same Bug 3 auto-disable as UpdateServiceAreasAsync - a dropped
        // skill can be the missing half that used to make a mapping
        // bookable.
        await _serviceabilityMappingManagementService.AutoDisableUnservedMappingsAsync(providerId, previouslyCovered);

        return Result.Success<IReadOnlyList<ProviderSkillResponse>>(skills.Select(ToResponse).ToList());
    }

    public async Task<Result> DeleteAccountAsync(Guid providerId)
    {
        var provider = await _providerRepository.GetByIdAsync(providerId);
        if (provider is null)
        {
            return Result.Failure(Error.NotFound("ProviderProfile.NotFound", "The specified provider does not exist."));
        }

        if (provider.Status == ProviderStatus.Deactivated)
        {
            // Already deleted - idempotent from the caller's perspective
            // rather than an error, since the end state they wanted holds.
            return Result.Success();
        }

        provider.SoftDelete();
        await _providerRepository.UpdateAsync(provider);
        await _sessionRepository.RevokeAllForProviderAsync(providerId);

        // Bug 3 auto-disable: self-service deletion is a provider going
        // inactive same as ProviderManagementService.SuspendAsync/DeleteAsync
        // - their skill/area rows are untouched, so no "before" snapshot is
        // needed (see AutoDisableUnservedMappingsAsync's doc comment).
        await _serviceabilityMappingManagementService.AutoDisableUnservedMappingsAsync(providerId);

        return Result.Success();
    }

    private async Task<ProviderProfileResponse> ToResponseAsync(Provider provider)
    {
        var rating = await _reviewRepository.GetProviderRatingAsync(provider.Id);
        return new(
            provider.Id, provider.LegalName, provider.DisplayName, provider.Phone, provider.Email,
            provider.Status.ToString(), provider.OnboardingStatus.ToString(),
            // Deliberately the raw PhotoUrl, not PublicPhotoUrl - see the
            // response's own doc comment: this is the only surface where the
            // provider needs to see their own not-yet-approved photo.
            provider.PhotoUrl, provider.PhotoModerationStatus?.ToString(), provider.PhotoModerationNote,
            rating?.AverageRating, rating?.ReviewCount);
    }

    private static ProviderServiceAreaResponse ToResponse(ProviderServiceArea area) => new(
        area.Id, area.ProviderId, area.CityId, area.ZoneId, area.PincodeId, area.IsActive);

    private static ProviderSkillResponse ToResponse(ProviderSkillMapping skill) => new(
        skill.Id, skill.ProviderId, skill.CategoryId, skill.ServiceId, skill.IsActive);
}
