namespace Nestly.Application.Payments;

/// <summary>
/// Vendor-agnostic capability for the real PayU Payouts integration (PayU
/// Payouts task brief) - initiates an automated bank transfer for a provider
/// payout. Deliberately separate from <see cref="IPaymentGateway"/>: PayU
/// Payouts is a different PayU product with its own credentials/endpoints
/// (OAuth client id/secret, not the Hosted Checkout merchant key/salt), and
/// the two capabilities (take a customer's payment vs. pay a provider out)
/// never need to vary together.
/// </summary>
public interface IProviderPayoutGateway
{
    /// <summary>
    /// Whether this gateway is backed by real, usable PayU Payouts
    /// credentials right now - mirrors <c>PayUOptions.IsConfigured</c>'s
    /// role for <see cref="IPaymentGateway"/>. <see cref="ProviderManagement.IProviderPayoutService.PayViaPayUAsync"/>
    /// checks this before ever calling <see cref="InitiateTransferAsync"/>,
    /// and admin-web only shows the "Pay via PayU" button when it is true
    /// (product decision) - so a caller reaching <see cref="InitiateTransferAsync"/>
    /// while this is false should not be reachable in practice.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Asks PayU to move money to a provider's bank account. PayU's real
    /// behaviour is asynchronous even on acceptance (its own documented
    /// response: "Requests are in process... Will send response of
    /// individual request on webhooks set by you") - a <see cref="PayoutTransferResult.Accepted"/>
    /// <c>true</c> means only that PayU queued the request, never that money
    /// has moved; the real outcome arrives later via the transfer webhook
    /// (<see cref="ProviderManagement.IProviderPayoutService.HandlePayUTransferWebhookAsync"/>).
    /// </summary>
    Task<PayoutTransferResult> InitiateTransferAsync(PayoutTransferRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether <paramref name="payoutMerchantId"/> matches this deployment's
    /// own configured PayU Payouts merchant id - PayU does not document an
    /// HMAC/signature scheme for its transfer webhook the way Hosted
    /// Checkout's callback has, so this echoed id is the only lightweight
    /// authenticity check <see cref="ProviderManagement.IProviderPayoutService.HandlePayUTransferWebhookAsync"/>
    /// has available (it rules out a stray/misdirected call, not a forged
    /// one from a party who already knows the configured id - see
    /// <see cref="ProviderManagement.PayUPayoutWebhookPayload"/>'s own doc
    /// comment). Always <c>false</c> when this gateway is not configured -
    /// a webhook cannot legitimately match a merchant id nothing is
    /// configured with.
    /// </summary>
    bool MatchesConfiguredMerchantId(string? payoutMerchantId);
}

/// <summary>
/// <paramref name="MerchantReferenceId"/> is this project's own opaque
/// reference (the payout's id, formatted so it fits PayU's documented
/// <c>merchantRefId</c> 40-character limit) - PayU echoes it back on the
/// later transfer webhook, which is how <see cref="IProviderPayoutGateway"/>'s
/// caller correlates that webhook back to the payout that triggered it, the
/// same role <c>PaymentAttempt.GatewayOrderId</c> plays for Hosted Checkout.
/// <paramref name="BeneficiaryAccountNumber"/>/<paramref name="BeneficiaryIfscCode"/>
/// come from the provider's <c>ProviderBankAccount</c> (bank-transfer mode
/// only - this project has no UPI VPA on file for a provider today, so VPA
/// transfers are out of scope here even though PayU's API supports them).
/// </summary>
public sealed record PayoutTransferRequest(
    string MerchantReferenceId,
    decimal Amount,
    string BeneficiaryAccountNumber,
    string BeneficiaryIfscCode,
    string BeneficiaryName,
    string? BeneficiaryEmail = null,
    string? BeneficiaryMobile = null);

/// <summary>
/// <paramref name="Accepted"/> false covers both an explicit PayU decline and
/// any failure to even reach PayU (auth failure, transport error, malformed
/// response) - <see cref="ProviderManagement.IProviderPayoutService.PayViaPayUAsync"/>
/// treats all of these identically: the payout stays Pending, so a retry
/// (fix the bank details, retry the button, or fall back to the manual flow)
/// is always safe. <paramref name="FailureReason"/> is populated only then.
/// </summary>
public sealed record PayoutTransferResult(bool Accepted, string? FailureReason = null);
