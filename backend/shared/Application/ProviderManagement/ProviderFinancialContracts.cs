using Nestly.Domain;

namespace Nestly.Application.ProviderManagement;

/// <summary>Admin-recorded manual adjustment to a provider's earning ledger (task 148) - a credit (e.g. a correction) or a debit (a penalty).</summary>
public sealed record RecordProviderEarningAdjustmentRequest(
    ProviderEarningEntryType EntryType,
    decimal Amount,
    ProviderEarningSourceType SourceType,
    Guid? SourceReferenceId,
    string Description);

public sealed record ProviderEarningLedgerEntryResponse(
    Guid Id,
    Guid ProviderId,
    ProviderEarningEntryType EntryType,
    decimal Amount,
    decimal BalanceAfter,
    ProviderEarningSourceType SourceType,
    Guid? SourceReferenceId,
    string Description,
    DateTime CreatedAtUtc);

public sealed record ProviderEarningsSummaryResponse(
    Guid ProviderId,
    decimal CurrentBalance,
    IReadOnlyList<ProviderEarningLedgerEntryResponse> Entries);

/// <summary>Admin runs a payout batch for a provider over a period (PROVIDER.md API surface "run payout batch", task 148). Sums the earning ledger for that period - no gateway call, OPEN DECISIONS #3.</summary>
public sealed record CreateProviderPayoutRequest(DateOnly PeriodStart, DateOnly PeriodEnd);

public sealed record UpdateProviderPayoutStatusRequest(ProviderPayoutStatus Status, string? PayoutReference, string? Notes);

public sealed record ProviderPayoutResponse(
    Guid Id,
    Guid ProviderId,
    string ProviderDisplayName,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal TotalAmount,
    ProviderPayoutStatus Status,
    string? PayoutReference,
    string? Notes,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ProviderPayoutSearchResponse(IReadOnlyList<ProviderPayoutResponse> Items, int TotalCount, int Page, int PageSize);
