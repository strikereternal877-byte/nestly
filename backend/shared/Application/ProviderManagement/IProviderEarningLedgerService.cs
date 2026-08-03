using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.ProviderManagement;

/// <summary>
/// A provider's append-only earning ledger (PROVIDER.md Financial Domain,
/// task 148). Credits/debits are recorded here as an admin-triggered manual
/// adjustment today (e.g. a penalty, or a correction) - automatic
/// crediting on job completion is wired in once the provider-facing "complete
/// job" flow (tasks 149c/151) exists, by calling this same interface.
/// </summary>
public interface IProviderEarningLedgerService
{
    Task<Result<ProviderEarningLedgerEntryResponse>> RecordAdjustmentAsync(Guid providerId, RecordProviderEarningAdjustmentRequest request);

    Task<Result<ProviderEarningsSummaryResponse>> GetSummaryAsync(Guid providerId);
}
