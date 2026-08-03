using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Application.ProviderManagement;

/// <summary>
/// Admin-triggered payout batch management (PROVIDER.md Financial Domain,
/// task 148). OPEN DECISIONS #3: v1 is manual bank transfer - a batch is
/// created from the earning ledger, then an admin walks it through
/// Pending -&gt; Processing -&gt; Paid/Failed by hand; there is no gateway
/// integration.
/// </summary>
public interface IProviderPayoutService
{
    /// <summary>Sums the provider's earning ledger over the period and creates a Pending payout for the net amount (must be positive).</summary>
    Task<Result<ProviderPayoutResponse>> CreateBatchAsync(Guid providerId, CreateProviderPayoutRequest request);

    Task<Result<ProviderPayoutResponse>> GetByIdAsync(Guid payoutId);

    Task<Result<ProviderPayoutSearchResponse>> SearchAsync(Guid? providerId, ProviderPayoutStatus? status, int page, int pageSize);

    /// <summary>Advances a payout's status (Pending -&gt; Processing -&gt; Paid, or -&gt; Failed) - see <see cref="Domain.ProviderPayout"/>'s transition methods for the exact legal moves.</summary>
    Task<Result<ProviderPayoutResponse>> UpdateStatusAsync(Guid payoutId, UpdateProviderPayoutStatusRequest request);
}
