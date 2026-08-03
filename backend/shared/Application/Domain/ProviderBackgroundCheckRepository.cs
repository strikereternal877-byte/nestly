using Nestly.Domain;

namespace Nestly.Application;

/// <summary>Persistence for <see cref="ProviderBackgroundCheck"/> (task 160).</summary>
public interface IProviderBackgroundCheckRepository
{
    Task AddAsync(ProviderBackgroundCheck entity);

    /// <summary>Full check history for a provider, newest first.</summary>
    Task<IReadOnlyList<ProviderBackgroundCheck>> ListByProviderAsync(Guid providerId);

    /// <summary>The most recent check outcome, or null if none was ever recorded (treated as still Pending). Used by the Provider activation gate.</summary>
    Task<ProviderBackgroundCheck?> GetLatestByProviderAsync(Guid providerId);
}
