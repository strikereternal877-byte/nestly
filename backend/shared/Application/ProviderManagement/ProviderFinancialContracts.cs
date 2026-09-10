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

/// <summary>
/// The provider-visible payout status of one completed job's earning
/// (docs/OPEN-FIXES-FEATURES.csv "Earnings detail and payouts"). Not a
/// persisted enum - derived at read time the same way <c>ProviderJobStatus</c>
/// is, by checking whether the job's credit date falls inside one of the
/// provider's <see cref="ProviderPayout"/> batches and, if so, mirroring that
/// batch's own <see cref="ProviderPayoutStatus"/>. v1 payouts are manual
/// batches an admin runs over a period (OPEN DECISIONS #3) - a completed job
/// is credited to the ledger immediately (<see cref="EscrowReleaseOnCompletionHandler"/>-equivalent
/// wording), but stays un-batched until an admin actually runs a payout
/// covering its date, so <see cref="AwaitingBatch"/> is a real, common state
/// here rather than a placeholder.
/// </summary>
public enum ProviderJobPayoutStatus
{
    /// <summary>Credited to the ledger, but no payout batch has been run yet for a period covering this job's completion date.</summary>
    AwaitingBatch,

    /// <summary>Included in a payout batch that has been created but not yet marked Processing/Paid.</summary>
    PendingSettlement,

    /// <summary>Included in a payout batch whose bank transfer is underway.</summary>
    Processing,

    /// <summary>Included in a payout batch whose bank transfer has been completed.</summary>
    Paid,

    /// <summary>Included in a payout batch whose bank transfer failed - awaiting a retry batch.</summary>
    Failed
}

/// <summary>
/// One completed job's earning breakdown (docs/OPEN-FIXES-FEATURES.csv
/// "Earnings detail and payouts") - the gross-to-net figures
/// <c>ProviderJobDetailResponse</c> already shows on the job screen
/// (<see cref="GrossAmount"/>/<see cref="CommissionAmount"/>/<see cref="NetAmountToProvider"/>,
/// same <c>ProviderJobService.ResolvePayoutAsync</c> source of truth), plus
/// this job's place in the payout timeline - so a provider can tell not just
/// what a job paid but when they will actually receive it.
/// </summary>
public sealed record ProviderEarningJobResponse(
    Guid BookingId,
    string BookingReference,
    string ServiceName,
    DateOnly CompletionDate,
    decimal GrossAmount,
    decimal CommissionAmount,
    decimal NetAmountToProvider,
    ProviderJobPayoutStatus PayoutStatus,
    DateTime CreditedAtUtc);

/// <summary>
/// Paginated response for the job-level earnings ledger, plus a summary over
/// the *entire filtered period* (not just the returned page) - cheap to
/// compute alongside the list since it is the same filtered query, summed.
/// </summary>
public sealed record ProviderEarningJobSearchResponse(
    IReadOnlyList<ProviderEarningJobResponse> Items,
    int TotalCount,
    int Page,
    int PageSize,
    decimal TotalNetAmount,
    int JobCount);
