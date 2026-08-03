using Nestly.Domain;

namespace Nestly.Application;

/// <summary>
/// Persistence for a provider's geography coverage (PROVIDER.md
/// "provider_service_area", task 149a "update service areas"). Replace-all
/// semantics rather than individual add/remove, mirroring
/// <c>ISlotWindowRepository.ReplaceRulesAsync</c> — a provider submits their
/// whole coverage set at once, so a full replace avoids reconciling a partial
/// diff against the unique (provider, city, zone, pincode) index.
/// </summary>
public interface IProviderServiceAreaRepository
{
    Task<IReadOnlyList<ProviderServiceArea>> GetByProviderAsync(Guid providerId);

    Task ReplaceForProviderAsync(Guid providerId, IReadOnlyList<ProviderServiceArea> areas);
}
