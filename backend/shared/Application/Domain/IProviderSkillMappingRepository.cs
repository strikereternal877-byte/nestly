using Nestly.Domain;

namespace Nestly.Application;

/// <summary>
/// Persistence for a provider's declared skills/capabilities (PROVIDER.md
/// "provider_skill_mapping", task 149a "update skills"). Replace-all
/// semantics, same reasoning as <see cref="IProviderServiceAreaRepository"/>.
/// </summary>
public interface IProviderSkillMappingRepository
{
    Task<IReadOnlyList<ProviderSkillMapping>> GetByProviderAsync(Guid providerId);

    Task ReplaceForProviderAsync(Guid providerId, IReadOnlyList<ProviderSkillMapping> skills);
}
