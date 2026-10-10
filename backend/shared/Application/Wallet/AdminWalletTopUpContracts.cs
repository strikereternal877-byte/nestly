using Nestly.Domain;

namespace Nestly.Application.Wallet;

/// <summary>
/// Filters for the admin wallet top-up list. Every filter is optional and they combine with AND.
/// <paramref name="NeedsAttention"/> true narrows to the rows an admin should act on - see
/// <see cref="AdminWalletTopUpAttention"/>. <paramref name="Search"/> is a case-insensitive substring match on the
/// customer's name or mobile number, or on the gateway order id.
/// </summary>
public sealed record AdminWalletTopUpFilterRequest(
    WalletTopUpStatus? Status = null,
    bool? NeedsAttention = null,
    string? Search = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Page = 1,
    int PageSize = 20);

/// <summary>
/// Why a top-up is flagged for an admin. Crosses the wire as its ordinal - append only.
/// <see cref="NeedsReview"/> outranks <see cref="Stuck"/> when both apply, since it is the one that cannot fix itself.
/// </summary>
public enum AdminWalletTopUpAttention
{
    /// <summary>Nothing to do.</summary>
    None,

    /// <summary>Still Pending well after the reconciliation sweep should have resolved it - the gateway has no final answer, or could not be reached.</summary>
    Stuck,

    /// <summary>A gateway callback disagreed with what was asked for (see <c>WalletTopUp.ReviewReason</c>); nothing was credited and a person must decide.</summary>
    NeedsReview
}

/// <summary>One row of the admin wallet top-up list; also the shape of a single top-up's detail.</summary>
/// <param name="AgeMinutes">Minutes since it was created, for a Pending top-up; null once it has resolved.</param>
/// <param name="AttentionReason">A sentence saying what is wrong, when <paramref name="Attention"/> is not None.</param>
public sealed record AdminWalletTopUpResponse(
    Guid Id,
    Guid CustomerId,
    string CustomerName,
    string CustomerMobile,
    decimal Amount,
    string Currency,
    WalletTopUpStatus Status,
    string GatewayOrderId,
    string? GatewayPaymentRef,
    string? FailureReason,
    Guid? WalletLedgerEntryId,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    int? AgeMinutes,
    AdminWalletTopUpAttention Attention,
    string? AttentionReason);

/// <summary>
/// A page of top-ups, newest first, plus the figures an admin checks every day. The counts and the credited total
/// describe the whole table, not just the filter or the page, so they do not shift as the filters change.
/// </summary>
/// <param name="CreditedLast24HoursCount">Top-ups that succeeded in the last 24 hours.</param>
/// <param name="CreditedLast24HoursAmount">What they added to wallets in total - the figure to set against the gateway's own settlement report.</param>
public sealed record PagedAdminWalletTopUpResponse(
    IReadOnlyList<AdminWalletTopUpResponse> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int PendingCount,
    int StuckCount,
    int NeedsReviewCount,
    int CreditedLast24HoursCount,
    decimal CreditedLast24HoursAmount);

/// <summary>The result of "Reconcile now": what the gateway check did, and the top-up as it is now.</summary>
public sealed record AdminWalletTopUpReconcileResponse(WalletTopUpReconcileOutcome Outcome, AdminWalletTopUpResponse TopUp);
