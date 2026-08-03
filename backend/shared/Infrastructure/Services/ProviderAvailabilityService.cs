using Nestly.Application;
using Nestly.Application.ProviderAvailability;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// CRUD over a provider's own recurring availability windows and blackout
/// dates (task 149b, PROVIDER.md API surface "Availability").
/// </summary>
public class ProviderAvailabilityService : IProviderAvailabilityService
{
    private readonly IProviderRepository _providerRepository;
    private readonly IProviderAvailabilityWindowRepository _windowRepository;
    private readonly IProviderBlackoutDateRepository _blackoutDateRepository;

    public ProviderAvailabilityService(
        IProviderRepository providerRepository,
        IProviderAvailabilityWindowRepository windowRepository,
        IProviderBlackoutDateRepository blackoutDateRepository)
    {
        _providerRepository = providerRepository;
        _windowRepository = windowRepository;
        _blackoutDateRepository = blackoutDateRepository;
    }

    public async Task<ProviderAvailabilityResponse> GetAsync(Guid providerId)
    {
        var windows = await _windowRepository.GetByProviderAsync(providerId);
        var blackoutDates = await _blackoutDateRepository.GetByProviderAsync(providerId);

        return new ProviderAvailabilityResponse(
            windows.Select(ToResponse).ToList(),
            blackoutDates.Select(ToResponse).ToList());
    }

    public async Task<Result<IReadOnlyList<ProviderAvailabilityWindowResponse>>> UpdateWindowsAsync(
        Guid providerId, UpdateProviderAvailabilityWindowsRequest request)
    {
        if (!await _providerRepository.ExistsAsync(providerId))
        {
            return Result.Failure<IReadOnlyList<ProviderAvailabilityWindowResponse>>(
                Error.NotFound("ProviderAvailability.NotFound", "The specified provider does not exist."));
        }

        var windows = request.Windows
            .Select(w => new ProviderAvailabilityWindow(Guid.NewGuid(), providerId, w.DayOfWeek, w.StartTime, w.EndTime))
            .ToList();
        await _windowRepository.ReplaceForProviderAsync(providerId, windows);

        return Result.Success<IReadOnlyList<ProviderAvailabilityWindowResponse>>(windows.Select(ToResponse).ToList());
    }

    public async Task<IReadOnlyList<ProviderBlackoutDateResponse>> GetBlackoutDatesAsync(Guid providerId)
    {
        var blackoutDates = await _blackoutDateRepository.GetByProviderAsync(providerId);
        return blackoutDates.Select(ToResponse).ToList();
    }

    public async Task<Result<ProviderBlackoutDateResponse>> AddBlackoutDateAsync(
        Guid providerId, AddProviderBlackoutDateRequest request)
    {
        if (!await _providerRepository.ExistsAsync(providerId))
        {
            return Result.Failure<ProviderBlackoutDateResponse>(
                Error.NotFound("ProviderAvailability.NotFound", "The specified provider does not exist."));
        }

        var blackoutDate = new ProviderBlackoutDate(Guid.NewGuid(), providerId, request.StartDate, request.EndDate, request.Reason);
        await _blackoutDateRepository.AddAsync(blackoutDate);

        return Result.Success(ToResponse(blackoutDate));
    }

    public async Task<Result> DeleteBlackoutDateAsync(Guid providerId, Guid blackoutDateId)
    {
        var blackoutDate = await _blackoutDateRepository.GetByIdAsync(blackoutDateId);
        if (blackoutDate is null || blackoutDate.ProviderId != providerId)
        {
            // Same "not found" response whether the row doesn't exist at all or
            // belongs to a different provider - never confirms another
            // provider's blackout date exists (SRS 28.3 IDOR).
            return Result.Failure(Error.NotFound(
                "ProviderAvailability.BlackoutDateNotFound", "The specified blackout date does not exist."));
        }

        await _blackoutDateRepository.DeleteAsync(blackoutDate);
        return Result.Success();
    }

    private static ProviderAvailabilityWindowResponse ToResponse(ProviderAvailabilityWindow window) => new(
        window.Id, window.ProviderId, window.DayOfWeek, window.StartTime, window.EndTime, window.IsActive);

    private static ProviderBlackoutDateResponse ToResponse(ProviderBlackoutDate blackoutDate) => new(
        blackoutDate.Id, blackoutDate.ProviderId, blackoutDate.StartDate, blackoutDate.EndDate, blackoutDate.Reason);
}
