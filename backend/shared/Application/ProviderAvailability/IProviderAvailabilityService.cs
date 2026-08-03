using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.ProviderAvailability;

/// <summary>
/// CRUD over a provider's own recurring availability windows and blackout
/// dates (task 149b, PROVIDER.md API surface "Availability"). Thin service
/// over <c>ProviderAvailabilityWindow</c>/<c>ProviderBlackoutDate</c>, the same
/// shape as <c>ISlotManagementService</c>'s window/blackout sections but
/// scoped to one provider instead of admin-wide city configuration.
/// </summary>
public interface IProviderAvailabilityService
{
    Task<ProviderAvailabilityResponse> GetAsync(Guid providerId);

    Task<Result<IReadOnlyList<ProviderAvailabilityWindowResponse>>> UpdateWindowsAsync(Guid providerId, UpdateProviderAvailabilityWindowsRequest request);

    Task<IReadOnlyList<ProviderBlackoutDateResponse>> GetBlackoutDatesAsync(Guid providerId);

    Task<Result<ProviderBlackoutDateResponse>> AddBlackoutDateAsync(Guid providerId, AddProviderBlackoutDateRequest request);

    Task<Result> DeleteBlackoutDateAsync(Guid providerId, Guid blackoutDateId);
}
