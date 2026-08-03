using Nestly.Domain;

namespace Nestly.Application;

public interface IProviderAuthIdentityRepository
{
    Task AddAsync(ProviderAuthIdentity entity);
    Task UpdateAsync(ProviderAuthIdentity entity);
    Task<ProviderAuthIdentity?> GetByProviderAsync(AuthProviderType provider, string identifier);
    Task<IReadOnlyList<ProviderAuthIdentity>> GetByProviderAsync(Guid providerId);
    Task<bool> ExistsAsync(AuthProviderType provider, string identifier);
}
