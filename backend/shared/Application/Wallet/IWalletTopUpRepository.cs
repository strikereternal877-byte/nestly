using Nestly.Domain;

namespace Nestly.Application.Wallet;

public interface IWalletTopUpRepository
{
    Task AddAsync(WalletTopUp topUp);

    Task UpdateAsync(WalletTopUp topUp);

    Task<WalletTopUp?> GetByIdAsync(Guid id);

    /// <summary>Used by the webhook handler to resolve which top-up a callback belongs to.</summary>
    Task<WalletTopUp?> GetByGatewayOrderIdAsync(string gatewayOrderId);

    /// <summary>
    /// A still-Pending top-up of exactly this amount created at or after <paramref name="createdAfterUtc"/>, if any -
    /// so a double-click or a retry reuses the checkout already in flight instead of opening a second one.
    /// </summary>
    Task<WalletTopUp?> FindRecentPendingAsync(Guid customerId, decimal amount, DateTime createdAfterUtc);

    /// <summary>How many top-ups this customer started at or after <paramref name="sinceUtc"/>, whatever their outcome - the daily velocity limit's input.</summary>
    Task<int> CountCreatedSinceAsync(Guid customerId, DateTime sinceUtc);

    /// <summary>The total of this customer's still-Pending top-ups started at or after <paramref name="createdAfterUtc"/> - money that may yet land in the wallet.</summary>
    Task<decimal> SumRecentPendingAsync(Guid customerId, DateTime createdAfterUtc);

    /// <summary>Pending top-ups created before <paramref name="olderThanUtc"/> and no earlier than <paramref name="newerThanUtc"/>, oldest first - the reconciliation sweep's work list.</summary>
    Task<IReadOnlyList<WalletTopUp>> ListPendingBetweenAsync(DateTime newerThanUtc, DateTime olderThanUtc, int take);

    /// <summary>
    /// Atomically moves the top-up to <paramref name="newStatus"/> with a single conditional UPDATE - never a
    /// read-then-write (same race guard as <c>IPaymentTransactionRepository.TryMarkAttemptResolvedAsync</c>).
    /// A Success may replace Pending <i>or</i> Failed (a late payment after a write-off must still credit); a Failed
    /// replaces only Pending. False means another delivery already applied it and nothing must be applied a second time.
    /// </summary>
    Task<bool> TryMarkResolvedAsync(Guid id, WalletTopUpStatus newStatus);
}
