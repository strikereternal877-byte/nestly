using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nestly.Application.Payments;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Real PayU Payouts integration (PayU Payouts task brief), swapped in for
/// <see cref="NoOpProviderPayoutGateway"/> once <see cref="PayUPayoutOptions"/>
/// is fully configured - see <see cref="ProviderPayoutGatewayRegistration"/>
/// for the swap condition. Structurally mirrors <see cref="PayUPaymentGateway"/>
/// (named <see cref="HttpClient"/>, full-URL POSTs, small private DTOs for
/// the JSON exchanged) even though this is a completely separate PayU
/// product with its own OAuth-based auth, not a merchant-key/salt hash.
/// </summary>
/// <remarks>
/// Every endpoint, field name, and response shape below was checked against
/// docs.payu.in (authentication, initiate-transfer-api) while this class was
/// written, but - unlike <see cref="PayUPaymentGateway.CreateOrderAsync"/>,
/// whose own doc comment notes a real POST to PayU's test host round-tripped
/// successfully - none of it has been exercised against a real PayU Payouts
/// sandbox account (no test credentials exist for this session). Confirm the
/// exact field/header names one more time against a real PayU Payouts test
/// account before this ever points at production credentials -
/// <see cref="GetAccessTokenAsync"/> is kept as one small, clearly-marked
/// method for exactly that final check.
/// </remarks>
public sealed class PayUProviderPayoutGateway : IProviderPayoutGateway
{
    /// <summary>Named <see cref="HttpClient"/> registration - see <see cref="PayUPaymentGateway.HttpClientName"/> for why named rather than typed.</summary>
    public const string HttpClientName = "PayU.Payouts";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PayUPayoutOptions _options;
    private readonly ILogger<PayUProviderPayoutGateway> _logger;

    public PayUProviderPayoutGateway(IHttpClientFactory httpClientFactory, IOptions<PayUPayoutOptions> options, ILogger<PayUProviderPayoutGateway> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => _options.IsConfigured;

    public bool MatchesConfiguredMerchantId(string? payoutMerchantId) =>
        IsConfigured
        && !string.IsNullOrWhiteSpace(payoutMerchantId)
        && string.Equals(payoutMerchantId, _options.PayoutMerchantId, StringComparison.Ordinal);

    public async Task<PayoutTransferResult> InitiateTransferAsync(PayoutTransferRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            // Unreachable in practice - IProviderPayoutService.PayViaPayUAsync
            // checks IsConfigured first, and admin-web hides the "Pay via
            // PayU" button whenever it is false (product decision) - but a
            // loud, explicit failure here is far safer than silently trying
            // to call PayU with credentials that do not exist.
            throw new InvalidOperationException(
                "PayUProviderPayoutGateway.InitiateTransferAsync was called while PayU Payouts is not configured.");
        }

        string? accessToken;
        try
        {
            accessToken = await GetAccessTokenAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogError(ex, "PayU Payouts token request failed.");
            return new PayoutTransferResult(false, "Could not authenticate with PayU Payouts.");
        }

        if (string.IsNullOrEmpty(accessToken))
        {
            _logger.LogError("PayU Payouts token request did not return an access token.");
            return new PayoutTransferResult(false, "PayU Payouts did not return an access token.");
        }

        var payload = new PayUInitiateTransferApiRequest
        {
            PaymentType = "IMPS",
            Purpose = "Provider payout",
            Amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture),
            DisableApprovalFlow = true,
            BeneficiaryAccountNumber = request.BeneficiaryAccountNumber,
            BeneficiaryIfscCode = request.BeneficiaryIfscCode,
            BeneficiaryName = request.BeneficiaryName,
            BeneficiaryEmail = request.BeneficiaryEmail,
            BeneficiaryMobile = request.BeneficiaryMobile,
            MerchantRefId = request.MerchantReferenceId,
        };

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.BaseUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(httpRequest, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "PayU Payouts initiate-transfer request failed.");
            return new PayoutTransferResult(false, "Could not reach PayU Payouts.");
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("PayU Payouts initiate-transfer request failed with status {StatusCode}.", (int)response.StatusCode);
            return new PayoutTransferResult(false, $"PayU returned HTTP {(int)response.StatusCode}.");
        }

        PayUInitiateTransferApiResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<PayUInitiateTransferApiResponse>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "PayU Payouts initiate-transfer response could not be parsed.");
            return new PayoutTransferResult(false, "PayU's response could not be parsed.");
        }

        // PayU's documented accepted response is status 0 ("Requests are in
        // process... Will send response of individual request on webhooks
        // set by you") - genuinely different from PayU Hosted Checkout's
        // postservice commands, where status 1 means success. Anything else
        // is treated as an outright decline, never a partial success.
        if (parsed is null || parsed.Status != 0)
        {
            string reason = parsed?.Msg ?? "PayU declined the transfer request.";
            _logger.LogWarning("PayU Payouts declined a transfer for merchant ref {MerchantRefId}: {Reason}.", request.MerchantReferenceId, reason);
            return new PayoutTransferResult(false, reason);
        }

        return new PayoutTransferResult(true);
    }

    /// <summary>
    /// PayU Payouts' OAuth token fetch (docs.payu.in "Generate Token using
    /// Private Client ID") - client-credentials grant, scoped to
    /// <c>create_payout_transactions</c> (the scope documented for the
    /// initiate-transfer API; a bank-verification call would instead need
    /// <c>verify_bank_account</c>, out of scope for this method). No
    /// caching: a payout is an infrequent, admin-triggered action rather
    /// than a hot path, so trading a slightly slower call for "never risk
    /// serving a stale/expired token" is the simpler and safer choice.
    /// Isolated in its own small method, per this class's own doc comment,
    /// as the one place to re-check field/header names against a real PayU
    /// Payouts sandbox before this ever runs against production credentials.
    /// </summary>
    private async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.AuthBaseUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _options.ClientId ?? string.Empty,
                ["client_secret"] = _options.ClientSecret ?? string.Empty,
                ["scope"] = "create_payout_transactions",
            }),
        };

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("PayU Payouts token request failed with status {StatusCode}.", (int)response.StatusCode);
            return null;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        var parsed = JsonSerializer.Deserialize<PayUTokenApiResponse>(body, JsonOptions);
        return parsed?.AccessToken;
    }

    private sealed class PayUTokenApiResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }
    }

    private sealed class PayUInitiateTransferApiRequest
    {
        [JsonPropertyName("paymentType")]
        public string PaymentType { get; set; } = "IMPS";

        [JsonPropertyName("purpose")]
        public string Purpose { get; set; } = string.Empty;

        [JsonPropertyName("amount")]
        public string Amount { get; set; } = string.Empty;

        [JsonPropertyName("disableApprovalFlow")]
        public bool DisableApprovalFlow { get; set; }

        [JsonPropertyName("beneficiaryAccountNumber")]
        public string BeneficiaryAccountNumber { get; set; } = string.Empty;

        [JsonPropertyName("beneficiaryIfscCode")]
        public string BeneficiaryIfscCode { get; set; } = string.Empty;

        [JsonPropertyName("beneficiaryName")]
        public string BeneficiaryName { get; set; } = string.Empty;

        [JsonPropertyName("beneficiaryEmail")]
        public string? BeneficiaryEmail { get; set; }

        [JsonPropertyName("beneficiaryMobile")]
        public string? BeneficiaryMobile { get; set; }

        [JsonPropertyName("merchantRefId")]
        public string MerchantRefId { get; set; } = string.Empty;
    }

    private sealed class PayUInitiateTransferApiResponse
    {
        public int Status { get; set; }
        public string? Msg { get; set; }
    }
}

/// <summary>
/// Safe default used whenever <see cref="PayUPayoutOptions"/> is not fully
/// configured (see <see cref="ProviderPayoutGatewayRegistration"/>) - mirrors
/// how <c>SandboxPaymentGateway</c> is always available for <see cref="IPaymentGateway"/>,
/// except there is no sandbox simulation to fall back to for a real bank
/// transfer, so this one throws rather than pretending to move money.
/// <see cref="IsConfigured"/> being <c>false</c> is what admin-web's "Pay via
/// PayU" button visibility and <see cref="ProviderManagement.IProviderPayoutService.PayViaPayUAsync"/>'s
/// own guard both key off - reaching <see cref="InitiateTransferAsync"/> at
/// all means one of those checks was bypassed, which should never happen.
/// </summary>
public sealed class NoOpProviderPayoutGateway : IProviderPayoutGateway
{
    public bool IsConfigured => false;

    public bool MatchesConfiguredMerchantId(string? payoutMerchantId) => false;

    public Task<PayoutTransferResult> InitiateTransferAsync(PayoutTransferRequest request, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "PayU Payouts is not configured. NoOpProviderPayoutGateway.InitiateTransferAsync should be unreachable - " +
            "IProviderPayoutService.PayViaPayUAsync checks IsConfigured before calling this, and admin-web hides the " +
            "\"Pay via PayU\" action whenever it is false.");
}
