namespace Nestly.Infrastructure.Options;

/// <summary>
/// Strongly typed binding of the "PayUPayouts" configuration section (real
/// PayU Payouts integration, PayU Payouts task brief). A separate
/// product/credential set from <see cref="PayUOptions"/> (PayU Hosted
/// Checkout) - different OAuth client id/secret, different merchant id,
/// different base host - never reused between the two, same reasoning
/// <see cref="PayUOptions"/> itself documents for why it is not a record
/// (its synthesized <c>ToString()</c> would print <see cref="ClientSecret"/>
/// into any careless log call).
/// </summary>
public class PayUPayoutOptions
{
    public const string SectionName = "PayUPayouts";

    /// <summary>PayU Payouts' OAuth "Client ID" - paired with <see cref="ClientSecret"/> to obtain an access token (docs.payu.in "Generate Token using Private Client ID"), not PayU Hosted Checkout's merchant key.</summary>
    public string? ClientId { get; set; }

    /// <summary>The OAuth client secret - never sent to the client, only used server-side to obtain an access token.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// PayU's own merchant id for the Payouts product, echoed back on every
    /// transfer webhook as <c>payoutMerchantId</c> - the only lightweight
    /// authenticity check available for that webhook (see
    /// <see cref="Application.ProviderManagement.PayUPayoutWebhookPayload"/>'s
    /// doc comment for why it is not a full signature scheme).
    /// </summary>
    public string? PayoutMerchantId { get; set; }

    /// <summary>
    /// The FULL Initiate Transfer endpoint URL, not just a host - test
    /// (<c>https://uatoneapi.payu.in/payout/v2/payment</c>) and production
    /// (<c>https://payout.payumoney.com/payout/payment</c>) differ in their
    /// path, not just their host (verified against docs.payu.in this
    /// session), unlike <see cref="PayUOptions"/>'s single <c>_payment</c>
    /// path that is identical in both environments. Configuring the whole
    /// URL sidesteps having to encode that path difference in code.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// The FULL OAuth token endpoint URL - test
    /// (<c>https://uat-accounts.payu.in/oauth/token</c>) or production
    /// (<c>https://accounts.payu.in/oauth/token</c>), a different subdomain
    /// entirely from <see cref="BaseUrl"/>'s transfer API. Both are required
    /// together, see <see cref="IsConfigured"/>.
    /// </summary>
    public string? AuthBaseUrl { get; set; }

    public bool Enabled { get; set; } = true;

    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(PayoutMerchantId)
        && !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(AuthBaseUrl);
}
