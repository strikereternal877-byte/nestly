using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nestly.Application.Payments;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Real PayU Hosted Checkout integration (SRS 30.1), swapped in for the
/// sandbox once <see cref="PayUOptions"/> is fully configured - see
/// <see cref="PaymentGatewayRegistration"/> for the swap condition.
/// </summary>
/// <remarks>
/// <para>
/// PayU's classic Hosted Checkout has no server-side "create order" API call
/// - the "order" is just a merchant-generated <c>txnid</c> plus a signed
/// form the browser is redirected to PayU with (PayU creates and resolves
/// the transaction entirely on its own side once that form is posted). So
/// <see cref="CreateOrderAsync"/> does no I/O; it only computes the hash and
/// hands back what a caller needs to build that redirect.
/// </para>
/// <para>
/// Every field name, hash formula, and endpoint below was verified against
/// PayU's real test environment (not just documentation) while this class
/// was written: POSTing a correctly-hashed form to
/// https://test.payu.in/_payment returned a 302 to PayU's actual checkout
/// page rather than PayU's own "incorrectly calculated hash" error page.
/// </para>
/// </remarks>
public sealed class PayUPaymentGateway : IPaymentGateway
{
    /// <summary>Named <see cref="HttpClient"/> registration - see <see cref="SupabaseFileStorageService.HttpClientName"/> for why named rather than typed.</summary>
    public const string HttpClientName = "PayU.Postservice";

    private const string RefundCommand = "cancel_refund_transaction";

    private static readonly JsonSerializerOptions RefundResponseJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PayUOptions _options;
    private readonly ILogger<PayUPaymentGateway> _logger;

    public PayUPaymentGateway(IHttpClientFactory httpClientFactory, IOptions<PayUOptions> options, ILogger<PayUPaymentGateway> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    private string PaymentPageUrl => _options.UseProductionEnvironment ? "https://secure.payu.in/_payment" : "https://test.payu.in/_payment";

    private string PostserviceUrl => _options.UseProductionEnvironment
        ? "https://info.payu.in/merchant/postservice.php?form=2"
        : "https://test.payu.in/merchant/postservice.php?form=2";

    public Task<GatewayOrderResult> CreateOrderAsync(GatewayCreateOrderRequest request, CancellationToken cancellationToken = default)
    {
        string txnid = request.ExistingGatewayOrderId ?? $"NST{Guid.NewGuid():N}";
        string amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture);
        string productinfo = request.ProductInfo ?? $"Nestly booking {request.BookingId:N}";
        string firstname = string.IsNullOrWhiteSpace(request.CustomerName) ? "Nestly Customer" : request.CustomerName;
        string phone = request.CustomerMobile ?? string.Empty;
        // PayU's hash requires a non-empty email; a customer who registered
        // by mobile only (SRS 11.2.1) may genuinely have none on file. A
        // synthetic, clearly-non-deliverable placeholder keeps the hash
        // valid without inventing a real-looking address - same convention
        // Customer.Delete already uses for its own placeholder email.
        string email = string.IsNullOrWhiteSpace(request.CustomerEmail)
            ? $"{(string.IsNullOrWhiteSpace(phone) ? Guid.NewGuid().ToString("N") : phone)}@customer.glavyx.invalid"
            : request.CustomerEmail;

        string hash = ComputeSha512Hex(string.Join('|', new[]
        {
            _options.MerchantKey ?? string.Empty,
            txnid,
            amount,
            productinfo,
            firstname,
            email,
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, // udf1-udf5
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, // udf6-udf10
            _options.MerchantSalt ?? string.Empty,
        }));

        // One return URL for both outcomes: the source of truth for what
        // actually happened is our own webhook-updated booking/payment
        // state, not the URL PayU happened to redirect to (PayU's redirect
        // can race the webhook either way) - so the return page's job is
        // always "go re-check the real status," identical regardless of
        // which of surl/furl PayU chose. Matches customer-web's existing
        // booking/payment/[id] page, which already re-fetches and branches
        // on status rather than trusting anything client-side.
        string baseUrl = _options.CheckoutReturnBaseUrl?.TrimEnd('/') ?? string.Empty;
        // Standard (dashed) GUID formatting - matches every other booking id
        // in a customer-web URL (e.g. /booking/payment/{id}), which is what
        // this page's own [id] route param parses.
        // A wallet top-up has no booking, so it names its own return page; every booking payment keeps this one.
        string returnUrl = $"{baseUrl}{request.ReturnPath ?? $"/booking/payment/{request.BookingId}/return"}";
        var formFields = new Dictionary<string, string>
        {
            ["key"] = _options.MerchantKey ?? string.Empty,
            ["txnid"] = txnid,
            ["amount"] = amount,
            ["productinfo"] = productinfo,
            ["firstname"] = firstname,
            ["email"] = email,
            ["phone"] = phone,
            ["surl"] = returnUrl,
            ["furl"] = returnUrl,
            ["hash"] = hash,
        };

        return Task.FromResult(new GatewayOrderResult(txnid, "created", PaymentPageUrl, formFields));
    }

    public async Task<GatewayRefundResult> RefundAsync(GatewayRefundRequest request, CancellationToken cancellationToken = default)
    {
        // var2 is PayU's own idempotency token for this refund attempt - it
        // must be unique per call, unlike request.Receipt (the booking id),
        // which is intentionally the same across every refund/payment call
        // for one booking. Generated here rather than threaded through
        // GatewayRefundRequest since nothing downstream of this call needs
        // to reproduce it - only PayU does, and only once.
        string refundToken = Guid.NewGuid().ToString("N");
        string amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture);
        string hash = ComputeSha512Hex(string.Join('|', new[]
        {
            _options.MerchantKey ?? string.Empty,
            RefundCommand,
            request.GatewayPaymentRef,
            _options.MerchantSalt ?? string.Empty,
        }));

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, PostserviceUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["key"] = _options.MerchantKey ?? string.Empty,
                ["command"] = RefundCommand,
                ["var1"] = request.GatewayPaymentRef,
                ["var2"] = refundToken,
                ["var3"] = amount,
                ["hash"] = hash,
            }),
        };

        using var response = await client.SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("PayU refund request failed with status {StatusCode}.", (int)response.StatusCode);
            return new GatewayRefundResult(refundToken, "failed", $"PayU returned HTTP {(int)response.StatusCode}.");
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        PayURefundApiResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<PayURefundApiResponse>(body, RefundResponseJsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "PayU refund response could not be parsed.");
            return new GatewayRefundResult(refundToken, "failed", "PayU's refund response could not be parsed.");
        }

        if (parsed is null || parsed.Status != 1)
        {
            string reason = parsed?.Msg ?? "PayU declined the refund.";
            _logger.LogWarning("PayU declined a refund for gateway payment ref {GatewayPaymentRef}: {Reason}.", request.GatewayPaymentRef, reason);
            return new GatewayRefundResult(parsed?.RequestId ?? refundToken, "failed", reason);
        }

        return new GatewayRefundResult(parsed.RequestId ?? refundToken, "processed");
    }

    /// <summary>
    /// PayU's <c>verify_payment</c> postservice command - same endpoint and
    /// 4-field hash formula (<c>sha512(key|command|var1|salt)</c>) as
    /// <see cref="RefundAsync"/>'s <c>cancel_refund_transaction</c> above,
    /// with <c>var1</c> being the txnid instead of a gateway payment ref.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="CreateOrderAsync"/> and <see cref="RefundAsync"/> -
    /// whose own doc comments note they were checked against a real request
    /// to PayU's test environment - this method has NOT been exercised
    /// against a live PayU account (no transaction existed to verify against
    /// while writing it). The command name, hash formula, and top-level
    /// <c>status</c>/<c>transaction_details</c> response shape are PayU's
    /// standard, documented postservice contract, but the exact field PayU
    /// returns a human-readable failure reason under is not something this
    /// comment claims certainty on - both a likely name and a safe fallback
    /// are tried below. Test this against a real PayU sandbox transaction
    /// (cancel a checkout, then confirm this call reports it as failed)
    /// before relying on it to resolve real customer payments.
    /// </remarks>
    public async Task<GatewayVerifyResult> VerifyOrderStatusAsync(string gatewayOrderId, CancellationToken cancellationToken = default)
    {
        const string command = "verify_payment";
        string hash = ComputeSha512Hex(string.Join('|', new[]
        {
            _options.MerchantKey ?? string.Empty,
            command,
            gatewayOrderId,
            _options.MerchantSalt ?? string.Empty,
        }));

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, PostserviceUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["key"] = _options.MerchantKey ?? string.Empty,
                ["command"] = command,
                ["var1"] = gatewayOrderId,
                ["hash"] = hash,
            }),
        };

        using var response = await client.SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("PayU verify_payment request failed with status {StatusCode}.", (int)response.StatusCode);
            // A transport/HTTP failure tells us nothing about the payment
            // itself - "pending" leaves the attempt untouched so a transient
            // network error here can never wrongly fail a payment that may
            // still be fine; the customer's next poll or check-again retries.
            return new GatewayVerifyResult("pending");
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        PayUVerifyApiResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<PayUVerifyApiResponse>(body, RefundResponseJsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "PayU verify_payment response could not be parsed.");
            return new GatewayVerifyResult("pending");
        }

        var detail = parsed?.TransactionDetails?.GetValueOrDefault(gatewayOrderId);
        if (parsed is null || parsed.Status != 1 || detail is null)
        {
            // PayU has no record of this txnid ever reaching it at all - the
            // real "customer cancelled before submitting anything" case this
            // method exists to detect.
            return new GatewayVerifyResult("failure", FailureReason: "No amount was deducted. The checkout was not completed.");
        }

        if (string.Equals(detail.Status, "pending", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(detail.Status, "inprogress", StringComparison.OrdinalIgnoreCase))
        {
            return new GatewayVerifyResult("pending");
        }

        if (string.Equals(detail.Status, PaymentWebhookPayload.SuccessStatus, StringComparison.OrdinalIgnoreCase))
        {
            return new GatewayVerifyResult(PaymentWebhookPayload.SuccessStatus, GatewayPaymentRef: detail.Mihpayid);
        }

        return new GatewayVerifyResult("failure", FailureReason: detail.ErrorMessage ?? detail.Field9 ?? "The payment did not go through.");
    }

    public bool VerifyWebhookSignature(string canonicalPayload, string signature)
    {
        if (string.IsNullOrEmpty(signature))
        {
            return false;
        }

        string expected = ComputeSha512Hex(canonicalPayload);

        // Constant-time comparison - see SandboxPaymentGateway.VerifyWebhookSignature for why.
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        byte[] actualBytes = Encoding.UTF8.GetBytes(signature.ToLowerInvariant());
        return expectedBytes.Length == actualBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    /// <summary>
    /// PayU's "reverse hash" - the exact mirror of <see cref="CreateOrderAsync"/>'s
    /// request hash, with <c>SALT</c> first, <c>status</c> inserted, and the
    /// field order reversed: <c>sha512(SALT|status|udf10..udf6|udf5..udf1|email|firstname|productinfo|amount|txnid|key)</c>.
    /// <paramref name="request"/>'s <c>Status</c> must be PayU's own raw
    /// status word ("success"/"failure"/"pending") exactly as PayU posted
    /// it, not this project's normalized success/failed constants - PayU
    /// computed its half of the hash using that raw word, so reconstructing
    /// with anything else would never match.
    /// </summary>
    public string BuildCanonicalPayload(PaymentWebhookRequest request)
    {
        string amount = (request.Amount ?? 0m).ToString("F2", CultureInfo.InvariantCulture);
        return string.Join('|', new[]
        {
            _options.MerchantSalt ?? string.Empty,
            request.Status,
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, // udf10-udf6
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, // udf5-udf1
            request.Email ?? string.Empty,
            request.FirstName ?? string.Empty,
            request.ProductInfo ?? string.Empty,
            amount,
            request.GatewayOrderId,
            _options.MerchantKey ?? string.Empty,
        });
    }

    private static string ComputeSha512Hex(string input) =>
        Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    private sealed class PayURefundApiResponse
    {
        public int Status { get; set; }
        public string? Msg { get; set; }

        [JsonPropertyName("request_id")]
        public string? RequestId { get; set; }
    }

    private sealed class PayUVerifyApiResponse
    {
        public int Status { get; set; }

        [JsonPropertyName("transaction_details")]
        public Dictionary<string, PayUVerifyTransactionDetail>? TransactionDetails { get; set; }
    }

    private sealed class PayUVerifyTransactionDetail
    {
        public string? Status { get; set; }
        public string? Mihpayid { get; set; }

        [JsonPropertyName("error_Message")]
        public string? ErrorMessage { get; set; }

        public string? Field9 { get; set; }
    }
}
