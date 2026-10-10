using Nestly.Domain;

namespace Nestly.Application;

/// <summary>
/// Persistence for <see cref="ProviderBankAccount"/> - one upsert-in-place row
/// per provider (mirrors <see cref="IProviderPayoutRepository"/>'s shape,
/// plus <see cref="IProviderKycDocumentRepository.ListPendingAsync"/>'s admin
/// queue read for the same reason: an admin needs a cross-provider worklist
/// of everything still awaiting a verdict).
/// </summary>
public interface IProviderBankAccountRepository
{
    Task<ProviderBankAccount?> GetByProviderIdAsync(Guid providerId);

    Task<ProviderBankAccount?> GetByIdAsync(Guid id);

    Task AddAsync(ProviderBankAccount entity);

    Task UpdateAsync(ProviderBankAccount entity);

    /// <summary>The admin bank-account verification queue: every row still Pending, oldest submission first (mirrors <see cref="IProviderKycDocumentRepository.ListPendingAsync"/>).</summary>
    Task<IReadOnlyList<ProviderBankAccount>> ListPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>Batched lookup for the payout screen (task brief: "a direct repository call, not a new cross-cutting abstraction") - avoids an N+1 when a payout search page returns many providers.</summary>
    Task<IReadOnlyDictionary<Guid, ProviderBankAccount>> GetByProviderIdsAsync(IReadOnlyCollection<Guid> providerIds);
}
