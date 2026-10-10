using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Application.ProviderManagement;

/// <summary>
/// Admin-triggered payout batch management (PROVIDER.md Financial Domain,
/// task 148). OPEN DECISIONS #3: v1 is manual bank transfer - a batch is
/// created from the earning ledger, then an admin walks it through
/// Pending -&gt; Processing -&gt; Paid/Failed by hand; there is no gateway
/// integration for that path.
///
/// The real PayU Payouts integration (task: automated bank verification +
/// automated bank transfer) adds a second, always-available way to drive the
/// SAME Pending -&gt; Processing -&gt; Paid/Failed machine - <see cref="PayViaPayUAsync"/>
/// and <see cref="HandlePayUTransferWebhookAsync"/> below - product decision:
/// manual stays exactly as it is (see <see cref="UpdateStatusAsync"/>'s own
/// doc comment), PayU is an addition, never a replacement.
/// </summary>
public interface IProviderPayoutService
{
    /// <summary>Sums the provider's earning ledger over the period and creates a Pending payout for the net amount (must be positive).</summary>
    Task<Result<ProviderPayoutResponse>> CreateBatchAsync(Guid providerId, CreateProviderPayoutRequest request);

    Task<Result<ProviderPayoutResponse>> GetByIdAsync(Guid payoutId);

    Task<Result<ProviderPayoutSearchResponse>> SearchAsync(Guid? providerId, ProviderPayoutStatus? status, int page, int pageSize);

    /// <summary>
    /// Advances a payout's status (Pending -&gt; Processing -&gt; Paid, or -&gt;
    /// Failed) by the EXISTING manual, admin-typed-it-by-hand path - see
    /// <see cref="Domain.ProviderPayout"/>'s transition methods for the exact
    /// legal moves. Unchanged by the PayU Payouts integration: this remains
    /// fully available regardless of whether PayU is configured (product
    /// decision, PayU Payouts task brief).
    /// </summary>
    Task<Result<ProviderPayoutResponse>> UpdateStatusAsync(Guid payoutId, UpdateProviderPayoutStatusRequest request);

    /// <summary>
    /// The automated counterpart to <see cref="UpdateStatusAsync"/>'s manual
    /// Pending -&gt; Processing move: validates the payout is Pending and the
    /// provider has a <see cref="ProviderBankAccountVerificationStatus.Verified"/>
    /// bank account on file, then calls the real PayU Payouts transfer API
    /// (<see cref="Payments.IProviderPayoutGateway.InitiateTransferAsync"/>).
    /// On PayU accepting the request, moves the payout to Processing via
    /// <see cref="Domain.ProviderPayout.MarkProcessingViaPayU"/> - the actual
    /// bank transfer is asynchronous from here (PayU's own documented
    /// behaviour: "Requests are in process..."); the real outcome only
    /// arrives later via <see cref="HandlePayUTransferWebhookAsync"/>. A
    /// Business/NotFound error when the gateway is not configured, the
    /// payout is not Pending, no Verified bank account exists, or PayU
    /// declines the request outright.
    /// </summary>
    Task<Result<ProviderPayoutResponse>> PayViaPayUAsync(Guid payoutId);

    /// <summary>
    /// PayU Payouts' Transfer Success/Failed webhook (see <see cref="PayUPayoutWebhookRequest"/>'s
    /// doc comment for the full field/security notes). Idempotent against
    /// redelivery by construction: only a payout currently Processing is
    /// resolved (via the existing, unmodified <see cref="Domain.ProviderPayout.MarkPaid"/>/
    /// <see cref="Domain.ProviderPayout.MarkFailed"/>) - a redelivered webhook
    /// for an already-Paid/Failed payout, or one that was never moved to
    /// Processing via PayU at all, is a safe no-op success, never an error.
    /// Every other documented event (deposit/low-balance/downtime/Smart Send
    /// notifications) is also a no-op success - this integration only acts on
    /// TRANSFER_SUCCESS/TRANSFER_FAILED.
    /// </summary>
    Task<Result> HandlePayUTransferWebhookAsync(PayUPayoutWebhookRequest request);
}
