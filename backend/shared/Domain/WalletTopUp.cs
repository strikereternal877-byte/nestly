using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>
/// A customer adding their own money to their wallet through the payment gateway. It is the wallet
/// counterpart of a booking's <see cref="PaymentTransaction"/>, and exists separately because that
/// record is tied to a booking and a top-up has none.
///
/// <para>
/// The wallet is credited exactly once, when the gateway confirms the payment: the resolution flips
/// <see cref="Status"/> from Pending with a conditional UPDATE (so a redelivered webhook or a racing
/// verify call cannot credit twice) and appends the ledger credit in the same database transaction.
/// <see cref="WalletLedgerEntryId"/> records which ledger row that was.
/// </para>
/// </summary>
public class WalletTopUp : AggregateRoot<Guid>
{
    public Guid CustomerId { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = "INR";

    /// <summary>The order id sent to the gateway (PayU's txnid). Unique.</summary>
    public string GatewayOrderId { get; private set; } = string.Empty;

    public WalletTopUpStatus Status { get; private set; }

    public string? GatewayPaymentRef { get; private set; }

    public string? FailureReason { get; private set; }

    /// <summary>The wallet ledger credit this top-up produced; null until it succeeds.</summary>
    public Guid? WalletLedgerEntryId { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime? CompletedAtUtc { get; private set; }

    /// <summary>
    /// Why a person must look at this top-up, or null when nothing is wrong. Set when a gateway callback
    /// disagrees with what was asked for (the wallet is never credited from the callback's figures), so the
    /// problem shows up in the admin top-up list instead of only in a log line. Cleared once the top-up resolves.
    /// </summary>
    public string? ReviewReason { get; private set; }

    public DateTime? ReviewFlaggedAtUtc { get; private set; }

    public bool NeedsReview => ReviewReason is not null;

    protected WalletTopUp() { }

    public WalletTopUp(Guid id, Guid customerId, decimal amount, string currency, string gatewayOrderId)
        : base(id)
    {
        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "A top-up amount must be positive.");
        }

        CustomerId = customerId;
        Amount = amount;
        Currency = currency ?? throw new ArgumentException("Currency is required.", nameof(currency));
        GatewayOrderId = gatewayOrderId ?? throw new ArgumentException("Gateway order id is required.", nameof(gatewayOrderId));
        Status = WalletTopUpStatus.Pending;
        CreatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Pending -> Success, and also Failed -> Success: a top-up the reconciliation sweep wrote off as
    /// abandoned can still be paid late (a slow netbanking session, a closed tab that was reopened), and when
    /// the gateway then confirms that payment the money must reach the wallet. Failed is therefore not
    /// final for success - only Success is.
    /// </summary>
    public void MarkSucceeded(string gatewayPaymentRef, Guid walletLedgerEntryId)
    {
        if (Status == WalletTopUpStatus.Success)
        {
            return;
        }

        Status = WalletTopUpStatus.Success;
        GatewayPaymentRef = gatewayPaymentRef;
        WalletLedgerEntryId = walletLedgerEntryId;
        FailureReason = null;
        CompletedAtUtc = DateTime.UtcNow;
        ClearReviewFlag();
    }

    public void MarkFailed(string? reason)
    {
        if (Status != WalletTopUpStatus.Pending)
        {
            return;
        }

        Status = WalletTopUpStatus.Failed;
        FailureReason = reason;
        CompletedAtUtc = DateTime.UtcNow;
        ClearReviewFlag();
    }

    /// <summary>
    /// Marks the top-up as needing a person's attention. Idempotent: a redelivered callback that disagrees the
    /// same way keeps the original reason and time rather than rewriting them. A top-up that already succeeded
    /// has nothing left to review.
    /// </summary>
    public void FlagForReview(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A review reason is required.", nameof(reason));
        }

        if (Status == WalletTopUpStatus.Success || NeedsReview)
        {
            return;
        }

        ReviewReason = reason.Length > MaxReviewReasonLength ? reason[..MaxReviewReasonLength] : reason;
        ReviewFlaggedAtUtc = DateTime.UtcNow;
    }

    private void ClearReviewFlag()
    {
        ReviewReason = null;
        ReviewFlaggedAtUtc = null;
    }

    /// <summary>Column length of <see cref="ReviewReason"/>.</summary>
    public const int MaxReviewReasonLength = 300;
}
