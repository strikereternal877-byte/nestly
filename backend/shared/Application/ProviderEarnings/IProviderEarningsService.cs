using Nestly.Application.ProviderManagement;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Application.ProviderEarnings;

/// <summary>
/// The provider's own self-service view of their earnings and payouts (task
/// 149c, PROVIDER.md API surface "Earnings"). A thin, ownership-safe facade
/// over the admin-facing <see cref="IProviderEarningLedgerService"/>/
/// <see cref="IProviderPayoutService"/> (task 148) rather than a second copy
/// of that logic - the underlying ledger/payout entities and their
/// transitions have exactly one owner. This facade's only added
/// responsibility is scoping every read to the caller's own provider id (SRS
/// 28.3 IDOR) - in particular <see cref="GetPayoutDetailAsync"/>, since the
/// admin-facing <c>IProviderPayoutService.GetByIdAsync</c> takes no provider
/// id to check against.
/// </summary>
public interface IProviderEarningsService
{
    Task<Result<ProviderEarningsSummaryResponse>> GetSummaryAsync(Guid providerId);

    /// <summary>The caller's own append-only ledger entries, newest first.</summary>
    Task<Result<IReadOnlyList<ProviderEarningLedgerEntryResponse>>> GetLedgerAsync(Guid providerId);

    /// <summary>
    /// The caller's own job-level earnings breakdown (docs/OPEN-FIXES-FEATURES.csv
    /// "Earnings detail and payouts") - one row per completed job with its
    /// gross/commission/net figures and payout status. Passed straight
    /// through to <see cref="IProviderEarningLedgerService.GetJobEarningsAsync"/>
    /// with no extra IDOR check needed - <paramref name="providerId"/> already
    /// scopes the query, same reasoning as <see cref="GetSummaryAsync"/>/<see cref="GetLedgerAsync"/>.
    /// </summary>
    Task<Result<ProviderEarningJobSearchResponse>> GetJobEarningsAsync(
        Guid providerId, DateOnly? fromDate, DateOnly? toDate, int page, int pageSize);

    Task<Result<ProviderPayoutSearchResponse>> ListPayoutsAsync(Guid providerId, ProviderPayoutStatus? status, int page, int pageSize);

    /// <summary>One payout's detail - 404s (rather than the underlying service's plain not-found) when the payout exists but belongs to a different provider, so a caller can never probe another provider's payout by id.</summary>
    Task<Result<ProviderPayoutResponse>> GetPayoutDetailAsync(Guid providerId, Guid payoutId);
}
