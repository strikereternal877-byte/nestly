using Nestly.Domain;

namespace Nestly.Application;

/// <summary>Persistence for <see cref="ProviderEarningLedgerEntry"/> (task 148), mirroring <c>IWalletLedgerRepository</c>.</summary>
public interface IProviderEarningLedgerRepository
{
    /// <summary>Entries are append-only - there is deliberately no Update/Delete method (mirrors WalletLedgerEntry, SRS 14.5's convention).</summary>
    Task AddAsync(ProviderEarningLedgerEntry entry);

    /// <summary>The most recent entry for a provider, whose BalanceAfter is the provider's current earnings balance (null when the provider has no earning activity yet).</summary>
    Task<ProviderEarningLedgerEntry?> GetLatestAsync(Guid providerId);

    /// <summary>Full ledger for a provider, newest first.</summary>
    Task<IReadOnlyList<ProviderEarningLedgerEntry>> ListByProviderAsync(Guid providerId);

    /// <summary>Entries within a date range (inclusive), for payout batch calculation (task 148).</summary>
    Task<IReadOnlyList<ProviderEarningLedgerEntry>> ListByProviderAndPeriodAsync(Guid providerId, DateOnly periodStart, DateOnly periodEnd);

    /// <summary>Nestly Coins' monthly earn cap (docs/NESTLY-COINS.md FRAUD/ABUSE PREVENTION, task 201): total credited for one source type within a date range, computed as a DB-side SUM (mirrors <c>IWalletLedgerRepository</c>'s equivalent).</summary>
    Task<decimal> SumCreditsBySourceTypeInRangeAsync(Guid providerId, ProviderEarningSourceType sourceType, DateTime fromUtc, DateTime toUtc);

    /// <summary>Nestly Coins' clawback lookup (task 201): the credit entry issued for one source event, if any.</summary>
    Task<ProviderEarningLedgerEntry?> FindBySourceAsync(ProviderEarningSourceType sourceType, Guid sourceReferenceId);

    /// <summary>Nestly Coins' admin issued/clawed-back report (task 202): program-wide total (every provider) for one source type + entry type within a date range - unlike <see cref="SumCreditsBySourceTypeInRangeAsync"/>, which is scoped to a single provider for the earn cap check.</summary>
    Task<decimal> SumBySourceTypeInRangeAsync(ProviderEarningSourceType sourceType, ProviderEarningEntryType entryType, DateTime fromUtc, DateTime toUtc);
}
