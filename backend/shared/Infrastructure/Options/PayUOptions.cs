namespace Nestly.Infrastructure.Options;

/// <summary>
/// Strongly typed binding of the "PayU" configuration section (SRS 30.1).
/// PayU Hosted Checkout has no test-vs-production SDK split - both
/// environments use the same merchant-key/salt scheme, only the endpoint
/// host and the credentials differ, so <see cref="UseProductionEnvironment"/>
/// is what actually switches host, not a separate options class.
/// </summary>
/// <remarks>
/// A plain class, not a record: a record's synthesized <c>ToString()</c>
/// would print <see cref="MerchantSalt"/> into any careless log call - same
/// reasoning <c>GoogleMapsOptions</c> documents for its own API key.
/// </remarks>
public class PayUOptions
{
    public const string SectionName = "PayU";

    /// <summary>PayU's "Merchant Key" - not secret on its own (it is posted in every checkout form), but meaningless without <see cref="MerchantSalt"/>.</summary>
    public string? MerchantKey { get; set; }

    /// <summary>PayU's "Merchant Salt" - the actual secret; never sent to the client, only used server-side to compute/verify hashes.</summary>
    public string? MerchantSalt { get; set; }

    /// <summary>
    /// The customer-web origin PayU redirects back to after checkout, e.g.
    /// "https://app.nestly.app". <see cref="PayUPaymentGateway.CreateOrderAsync"/>
    /// appends its own success/failure paths - required because PayU's
    /// hosted checkout form has no notion of "no return URL configured yet."
    /// </summary>
    public string? CheckoutReturnBaseUrl { get; set; }

    /// <summary>False (default) targets PayU's test host (test.payu.in) - true switches to the live host (secure.payu.in / info.payu.in). Independent of ASP.NET's own environment name so a Staging deployment can still exercise PayU's test host.</summary>
    public bool UseProductionEnvironment { get; set; }

    public bool Enabled { get; set; } = true;

    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(MerchantKey)
        && !string.IsNullOrWhiteSpace(MerchantSalt)
        && !string.IsNullOrWhiteSpace(CheckoutReturnBaseUrl);
}
