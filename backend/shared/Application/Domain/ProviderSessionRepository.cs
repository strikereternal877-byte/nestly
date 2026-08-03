using Nestly.Domain;

namespace Nestly.Application;

public interface IProviderSessionRepository
{
    Task AddAsync(ProviderSession entity);
    Task UpdateAsync(ProviderSession entity);
    Task<ProviderSession?> GetByRefreshTokenHashAsync(string refreshTokenHash);

    /// <summary>Revokes every still-active session for a provider (mirrors <c>ICustomerSessionRepository.RevokeAllForCustomerAsync</c>).</summary>
    Task<int> RevokeAllForProviderAsync(Guid providerId);
}
