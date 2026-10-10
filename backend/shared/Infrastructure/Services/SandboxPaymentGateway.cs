using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Nestly.Application.Payments;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Internal sandbox payment gateway (SRS 30.1, task 68b) - this project has
/// no real payment vendor credentials, so this stands in for one. It is a
/// self-contained fake: order creation always succeeds (a real gateway
/// almost never rejects merely creating an order), and payment outcome is
/// decided deterministically by <see cref="DetermineOutcome"/> rather than
/// randomly, so tests and callers can force either branch on demand.
///
/// Amount convention (documented here since it is the single source of
/// truth): an amount whose paisa component is exactly 13 (e.g. ₹499.13)
/// deterministically fails; every other amount succeeds. This is a sandbox-
/// only convention with no real-gateway equivalent - real integrations would
/// simply remove this class and implement <see cref="IPaymentGateway"/>
/// against the vendor's SDK instead.
/// </summary>
public class SandboxPaymentGateway : IPaymentGateway, ISandboxPaymentSimulator
{
    private const int FailureTriggerPaisa = 13;
    private readonly SandboxGatewayOptions _options;

    public SandboxPaymentGateway(IOptions<SandboxGatewayOptions> options)
    {
        _options = options.Value;
    }

    public Task<GatewayOrderResult> CreateOrderAsync(GatewayCreateOrderRequest request, CancellationToken cancellationToken = default)
    {
        string gatewayOrderId = $"sandbox_order_{Guid.NewGuid():N}";
        return Task.FromResult(new GatewayOrderResult(gatewayOrderId, "created"));
    }

    public Task<GatewayRefundResult> RefundAsync(GatewayRefundRequest request, CancellationToken cancellationToken = default)
    {
        // The sandbox always honours a refund request - there is no real
        // settlement network here to reject it. Refund policy (eligibility,
        // amount caps) is enforced by IRefundService before this is ever
        // called, not by the gateway.
        string gatewayRefundId = $"sandbox_refund_{Guid.NewGuid():N}";
        return Task.FromResult(new GatewayRefundResult(gatewayRefundId, "processed"));
    }

    /// <summary>
    /// The sandbox has no real gateway-side state to poll - an order only
    /// ever resolves via an explicit <c>/orders/simulate</c> call, which goes
    /// straight through <see cref="IPaymentWebhookService"/> and already
    /// leaves nothing for this to discover. Always reporting "pending" is the
    /// honest simulation of that: this feature exists for a real gateway's
    /// abandoned-checkout gap, which the sandbox simply does not have.
    /// </summary>
    public Task<GatewayVerifyResult> VerifyOrderStatusAsync(string gatewayOrderId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GatewayVerifyResult("pending"));

    public SandboxPaymentOutcome DetermineOutcome(decimal amount)
    {
        int paisa = (int)(Math.Round(amount, 2) * 100m % 100m);
        if (paisa == FailureTriggerPaisa)
        {
            return new SandboxPaymentOutcome(false, GatewayPaymentRef: string.Empty, FailureReason: "Sandbox declined the payment (amount matched the deterministic failure convention).");
        }

        return new SandboxPaymentOutcome(true, GatewayPaymentRef: $"sandbox_pay_{Guid.NewGuid():N}", FailureReason: null);
    }

    public string BuildCanonicalPayload(PaymentWebhookRequest request) =>
        PaymentWebhookPayload.Build(request.GatewayOrderId, request.GatewayPaymentRef, request.Status);

    public bool VerifyWebhookSignature(string canonicalPayload, string signature)
    {
        if (string.IsNullOrEmpty(signature))
        {
            return false;
        }

        string expected = SignPayload(canonicalPayload);

        // Constant-time comparison - a callback signature is effectively a
        // secret check (SRS 28.3 "payment callback abuse"), so it must not
        // leak timing information about how many leading characters matched.
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        byte[] actualBytes = Encoding.UTF8.GetBytes(signature);
        return expectedBytes.Length == actualBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    public string SignPayload(string canonicalPayload)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(_options.WebhookSigningSecret);
        byte[] payloadBytes = Encoding.UTF8.GetBytes(canonicalPayload);
        byte[] hash = HMACSHA256.HashData(keyBytes, payloadBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
