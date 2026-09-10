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

    /// <summary>
    /// Job-level breakdown of the provider's <see cref="ProviderEarningSourceType.JobCompletion"/>
    /// ledger credits (docs/OPEN-FIXES-FEATURES.csv "Earnings detail and
    /// payouts") - one row per completed job with its gross/commission/net
    /// figures and payout status, newest first, optionally narrowed to a
    /// completion-date range. <paramref name="page"/>/<paramref name="pageSize"/>
    /// are clamped the same way <c>ProviderPayoutService.SearchAsync</c>
    /// clamps its own (neither endpoint validates its query string).
    /// </summary>
    Task<Result<ProviderEarningJobSearchResponse>> GetJobEarningsAsync(
        Guid providerId, DateOnly? fromDate, DateOnly? toDate, int page, int pageSize);
}
