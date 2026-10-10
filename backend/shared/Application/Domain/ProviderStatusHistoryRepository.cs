using Nestly.Domain;

namespace Nestly.Application;

/// <summary>Read side of <see cref="ProviderStatusHistory"/> - writes only ever happen through <see cref="Provider"/>'s own aggregate methods, persisted via <c>IProviderRepository.UpdateAsync</c>.</summary>
public interface IProviderStatusHistoryRepository
{
    /// <summary>Most recent change first, matching <c>ProviderDetailMapper</c>'s ordering.</summary>
    Task<IReadOnlyList<ProviderStatusHistory>> ListByProviderAsync(Guid providerId);
}
