using Nestly.Application;
using Nestly.Application.ProviderProfile;
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

    public ProviderProfileService(
        IProviderRepository providerRepository,
        IProviderServiceAreaRepository serviceAreaRepository,
        IProviderSkillMappingRepository skillMappingRepository)
    {
        _providerRepository = providerRepository;
        _serviceAreaRepository = serviceAreaRepository;
        _skillMappingRepository = skillMappingRepository;
    }

    public async Task<Result<ProviderProfileResponse>> GetAsync(Guid providerId)
    {
        var provider = await _providerRepository.GetByIdAsync(providerId);
        if (provider is null)
        {
            return Result.Failure<ProviderProfileResponse>(
                Error.NotFound("ProviderProfile.NotFound", "The specified provider does not exist."));
        }

        return Result.Success(ToResponse(provider));
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

        return Result.Success(ToResponse(provider));
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

        var areas = request.Areas
            .Select(a => new ProviderServiceArea(Guid.NewGuid(), providerId, a.CityId, a.ZoneId, a.PincodeId))
            .ToList();
        await _serviceAreaRepository.ReplaceForProviderAsync(providerId, areas);

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

        var skills = request.Skills
            .Select(s => new ProviderSkillMapping(Guid.NewGuid(), providerId, s.CategoryId, s.ServiceId))
            .ToList();
        await _skillMappingRepository.ReplaceForProviderAsync(providerId, skills);

        return Result.Success<IReadOnlyList<ProviderSkillResponse>>(skills.Select(ToResponse).ToList());
    }

    private static ProviderProfileResponse ToResponse(Provider provider) => new(
        provider.Id, provider.LegalName, provider.DisplayName, provider.Phone, provider.Email,
        provider.Status.ToString(), provider.OnboardingStatus.ToString());

    private static ProviderServiceAreaResponse ToResponse(ProviderServiceArea area) => new(
        area.Id, area.ProviderId, area.CityId, area.ZoneId, area.PincodeId, area.IsActive);

    private static ProviderSkillResponse ToResponse(ProviderSkillMapping skill) => new(
        skill.Id, skill.ProviderId, skill.CategoryId, skill.ServiceId, skill.IsActive);
}
