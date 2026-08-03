using Nestly.Domain;

namespace Nestly.Application;

/// <summary>
/// Persistence for a provider's blackout dates (PROVIDER.md "provider_availability
/// ... blackout dates", task 149b). Individual add/delete rather than
/// replace-all — mirrors <c>ISlotBlackoutRepository</c>, whose city-scoped
/// equivalent this structurally matches.
/// </summary>
public interface IProviderBlackoutDateRepository
{
    Task<IReadOnlyList<ProviderBlackoutDate>> GetByProviderAsync(Guid providerId);

    Task<ProviderBlackoutDate?> GetByIdAsync(Guid id);

    Task AddAsync(ProviderBlackoutDate entity);

    Task DeleteAsync(ProviderBlackoutDate entity);
}
