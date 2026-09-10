using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.ProviderProfile;

/// <summary>
/// Provider profile, service-area and skill management (task 149a,
/// PROVIDER.md API surface "Profile/Onboarding"). Every method takes the
/// caller's own provider id, resolved from the JWT by the controller — never
/// from a route or body parameter (SRS 28.3 IDOR), mirroring
/// <c>ICustomerProfileService</c>.
/// </summary>
public interface IProviderProfileService
{
    Task<Result<ProviderProfileResponse>> GetAsync(Guid providerId);

    Task<Result<ProviderProfileResponse>> UpdateAsync(Guid providerId, UpdateProviderProfileRequest request);

    /// <summary>
    /// Sets or clears the provider's profile photo (task 293). Setting one
    /// always puts it back into moderation - customers only ever see an
    /// approved photo (<see cref="Nestly.Domain.Provider.PublicPhotoUrl"/>).
    /// </summary>
    Task<Result<ProviderProfileResponse>> UpdatePhotoAsync(Guid providerId, UpdateProviderPhotoRequest request);

    Task<IReadOnlyList<ProviderServiceAreaResponse>> GetServiceAreasAsync(Guid providerId);

    Task<Result<IReadOnlyList<ProviderServiceAreaResponse>>> UpdateServiceAreasAsync(Guid providerId, UpdateProviderServiceAreasRequest request);

    Task<IReadOnlyList<ProviderSkillResponse>> GetSkillsAsync(Guid providerId);

    Task<Result<IReadOnlyList<ProviderSkillResponse>>> UpdateSkillsAsync(Guid providerId, UpdateProviderSkillsRequest request);

    /// <summary>
    /// Self-service right-to-erasure account deletion (mirrors
    /// <c>ICustomerProfileService.DeleteAccountAsync</c>). Terminal and
    /// irreversible.
    /// </summary>
    Task<Result> DeleteAccountAsync(Guid providerId);

    /// <summary>
    /// Go-live checklist (docs/OPEN-FIXES-FEATURES.csv "Provider Web,
    /// Proposed new page, Onboarding checklist and go-live status"): the
    /// specific prerequisites a provider is missing before they can start
    /// receiving work, so provider-web can name the exact gap instead of
    /// leaving a signed-in provider staring at an unexplained empty jobs
    /// list. Default weekly availability is now seeded at registration
    /// (<see cref="Nestly.Infrastructure.Services.ProviderRegistrationService"/>),
    /// so the availability check should normally already be satisfied for new
    /// providers - it stays part of this list because it can still be
    /// emptied later (<c>PUT /availability/windows</c> with no windows) and
    /// because this is meant as a general "why am I not getting work"
    /// diagnostic, not just a first-run screen.
    /// </summary>
    Task<Result<ProviderGoLiveStatusResponse>> GetGoLiveStatusAsync(Guid providerId);
}
