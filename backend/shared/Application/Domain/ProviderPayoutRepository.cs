using Nestly.Domain;

namespace Nestly.Application;

/// <summary>Persistence for <see cref="ProviderPayout"/> (task 148).</summary>
public interface IProviderPayoutRepository
{
    Task AddAsync(ProviderPayout entity);
    Task UpdateAsync(ProviderPayout entity);
    Task<ProviderPayout?> GetByIdAsync(Guid id);
    Task<IReadOnlyList<ProviderPayout>> ListByProviderAsync(Guid providerId);

    /// <summary>Admin-facing, paginated payout list (task 150c "run payout batch, list payouts").</summary>
    Task<(IReadOnlyList<ProviderPayout> Rows, int TotalCount)> SearchAsync(Guid? providerId, ProviderPayoutStatus? status, int page, int pageSize);
}
