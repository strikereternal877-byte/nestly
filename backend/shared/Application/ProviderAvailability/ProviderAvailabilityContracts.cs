namespace Nestly.Application.ProviderAvailability;

/// <summary>One recurring weekly working window (PROVIDER.md "provider_availability").</summary>
public record ProviderAvailabilityWindowResponse(
    Guid Id,
    Guid ProviderId,
    DayOfWeek DayOfWeek,
    TimeSpan StartTime,
    TimeSpan EndTime,
    bool IsActive);

public record ProviderAvailabilityWindowInput(DayOfWeek DayOfWeek, TimeSpan StartTime, TimeSpan EndTime);

/// <summary>Full replacement of a provider's weekly schedule (PROVIDER.md API surface "update availability").</summary>
public record UpdateProviderAvailabilityWindowsRequest(IReadOnlyList<ProviderAvailabilityWindowInput> Windows);

/// <summary>One date range in which a provider is unavailable (PROVIDER.md "blackout dates").</summary>
public record ProviderBlackoutDateResponse(
    Guid Id,
    Guid ProviderId,
    DateOnly StartDate,
    DateOnly EndDate,
    string? Reason);

/// <summary>PROVIDER.md API surface "set blackout dates".</summary>
public record AddProviderBlackoutDateRequest(DateOnly StartDate, DateOnly EndDate, string? Reason);

/// <summary>Combined view returned by the availability "get" endpoint.</summary>
public record ProviderAvailabilityResponse(
    IReadOnlyList<ProviderAvailabilityWindowResponse> Windows,
    IReadOnlyList<ProviderBlackoutDateResponse> BlackoutDates);
