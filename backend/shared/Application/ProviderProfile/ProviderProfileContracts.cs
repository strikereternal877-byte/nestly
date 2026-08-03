namespace Nestly.Application.ProviderProfile;

/// <summary>Provider's own profile view (PROVIDER.md API surface "get/update profile").</summary>
public record ProviderProfileResponse(
    Guid Id,
    string LegalName,
    string DisplayName,
    string Phone,
    string? Email,
    string Status,
    string OnboardingStatus);

public record UpdateProviderProfileRequest(string LegalName, string DisplayName, string? Email);

/// <summary>One geography a provider covers (PROVIDER.md "provider_service_area").</summary>
public record ProviderServiceAreaResponse(
    Guid Id,
    Guid ProviderId,
    Guid CityId,
    Guid? ZoneId,
    Guid? PincodeId,
    bool IsActive);

public record ProviderServiceAreaInput(Guid CityId, Guid? ZoneId, Guid? PincodeId);

/// <summary>Full replacement of a provider's coverage set (PROVIDER.md API surface "update service areas").</summary>
public record UpdateProviderServiceAreasRequest(IReadOnlyList<ProviderServiceAreaInput> Areas);

/// <summary>A category/service a provider is qualified for (PROVIDER.md "provider_skill_mapping").</summary>
public record ProviderSkillResponse(
    Guid Id,
    Guid ProviderId,
    Guid CategoryId,
    Guid? ServiceId,
    bool IsActive);

public record ProviderSkillInput(Guid CategoryId, Guid? ServiceId);

/// <summary>Full replacement of a provider's declared skills (PROVIDER.md API surface "update skills").</summary>
public record UpdateProviderSkillsRequest(IReadOnlyList<ProviderSkillInput> Skills);
