using Nestly.Domain;

namespace Nestly.Application;

/// <summary>
/// Persistence for a provider's recurring weekly availability (PROVIDER.md
/// "provider_availability", task 149b). Replace-all semantics, same reasoning
/// as <see cref="IProviderServiceAreaRepository"/> — a provider submits their
/// whole weekly schedule at once.
/// </summary>
public interface IProviderAvailabilityWindowRepository
{
    Task<IReadOnlyList<ProviderAvailabilityWindow>> GetByProviderAsync(Guid providerId);

    Task ReplaceForProviderAsync(Guid providerId, IReadOnlyList<ProviderAvailabilityWindow> windows);
}
