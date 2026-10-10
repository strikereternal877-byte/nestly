using Nestly.Application.Payments;
using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.Wallet;

/// <summary>
/// Adding money to the wallet through the payment gateway. Switched off by default
/// (<c>WalletTopUp:Enabled</c>): it holds customers' money, which needs the business and legal groundwork to
/// be in place before it is turned on in production.
/// </summary>
public interface IWalletTopUpService
{
    Task<WalletTopUpConfigResponse> GetConfigAsync();

    /// <summary>Starts a top-up: validates it against the limits, creates the gateway order and returns where to send the customer.</summary>
    Task<Result<WalletTopUpOrderResponse>> CreateAsync(Guid customerId, decimal amount);

    Task<Result<WalletTopUpResponse>> GetAsync(Guid customerId, Guid topUpId);

    /// <summary>
    /// Asks the gateway directly how a still-pending top-up ended, instead of waiting for a webhook that an
    /// abandoned or cancelled checkout may never send. A safe no-op once resolved or while the gateway itself
    /// still says pending.
    /// </summary>
    Task<Result<WalletTopUpResponse>> VerifyPendingAsync(Guid customerId, Guid topUpId);

    /// <summary>Sandbox only: completes a top-up the way the gateway's callback would. Refused when a real gateway is configured.</summary>
    Task<Result<WalletTopUpResponse>> SimulateAsync(Guid customerId, Guid topUpId);

    /// <summary>
    /// Applies a gateway callback. NotFound ("Payment.OrderNotFound") when the order id is not a top-up's, so the
    /// caller can tell "not mine" from a real failure - see <see cref="IPaymentCallbackRouter"/>.
    /// </summary>
    Task<Result> HandleCallbackAsync(PaymentWebhookRequest request);

    /// <summary>The reconciliation sweep's per-top-up step: verify with the gateway and apply a definite outcome. Returns whether the top-up was resolved.</summary>
    Task<bool> ReconcileAsync(Guid topUpId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The admin's "Reconcile now": the same gateway check as the sweep, but for one named top-up, whatever its age,
    /// and also for a Failed one (a late payment after a write-off still credits), reporting what happened.
    /// NotFound when the top-up does not exist.
    /// </summary>
    Task<Result<WalletTopUpReconcileOutcome>> ReconcileNowAsync(Guid topUpId, CancellationToken cancellationToken = default);
}

/// <summary>What asking the gateway about a top-up changed. Crosses the wire as its ordinal - append only.</summary>
public enum WalletTopUpReconcileOutcome
{
    /// <summary>The gateway confirmed the payment and the wallet was credited.</summary>
    Credited,

    /// <summary>The gateway said the payment did not happen; the top-up is now Failed and nothing was credited.</summary>
    MarkedFailed,

    /// <summary>The gateway has no final answer yet (or could not be reached); nothing changed.</summary>
    StillPending,

    /// <summary>Nothing to apply: already credited, or already Failed and the gateway agrees.</summary>
    Unchanged
}
