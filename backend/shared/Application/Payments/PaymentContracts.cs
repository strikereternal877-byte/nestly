using Nestly.Domain;

namespace Nestly.Application.Payments;

/// <summary>
/// Requests a gateway order for a booking (task 68a-d). <paramref name="IdempotencyKey"/>
/// is optional - when the caller omits it, dedup still happens on
/// <see cref="BookingId"/> alone (a booking can have at most one payment
/// transaction), but a client that generates its own key gets an extra guard
/// against, e.g., a doubled button-tap creating two HTTP requests before the
/// first one's response comes back.
/// </summary>
public record CreatePaymentOrderRequest(Guid BookingId, string? IdempotencyKey);

/// <summary>
/// The order to hand to the client-side checkout. <paramref name="CheckoutRedirectUrl"/>/
/// <paramref name="CheckoutFormFields"/> are populated only for a hosted-
/// checkout-style gateway the client must redirect the browser to (PayU) -
/// both null for the sandbox, whose client-side flow is the separate
/// <c>/payments/orders/simulate</c> endpoint instead.
/// </summary>
public record PaymentOrderResponse(
    Guid PaymentTransactionId,
    Guid AttemptId,
    string GatewayOrderId,
    decimal Amount,
    string Currency,
    int AttemptNumber,
    DateTime CreatedAtUtc,
    string? CheckoutRedirectUrl = null,
    IReadOnlyDictionary<string, string>? CheckoutFormFields = null,
    // A prepaid plan's checkout settles several bookings in one payment:
    // Amount is then the whole total, VisitCount how many bookings it covers,
    // and SkippedDates the dates of the purchase that could not be booked (not
    // charged for). Both stay at their single-booking defaults otherwise.
    int VisitCount = 1,
    IReadOnlyList<DateOnly>? SkippedDates = null);

public record PaymentAttemptResponse(
    Guid Id,
    int AttemptNumber,
    string GatewayOrderId,
    string? GatewayPaymentRef,
    PaymentAttemptStatus Status,
    string? FailureReason,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc);

/// <summary>
/// Full transaction detail (SRS 11.11.3 "payment transaction history shall
/// be available to admin"; SRS 14.3 reconciliation, task 71).
/// <paramref name="CommissionRatePercentage"/>/<paramref name="CommissionAmount"/>
/// are null until the payment succeeds and settlement is recorded (task 157).
/// </summary>
public record PaymentTransactionResponse(
    Guid Id,
    Guid BookingId,
    Guid CustomerId,
    decimal Amount,
    string Currency,
    PaymentTransactionStatus Status,
    IReadOnlyList<PaymentAttemptResponse> Attempts,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    decimal? CommissionRatePercentage,
    decimal? CommissionAmount,
    // Set when this booking was paid together with others in one prepaid checkout: what that single
    // payment came to in total and how many bookings it covered. Null for an ordinary payment.
    decimal? PrepaidCheckoutTotal = null,
    int? PrepaidCheckoutVisitCount = null);

/// <summary>
/// The webhook callback shape both the sandbox and PayU are normalized into
/// (task 69a). <paramref name="Signature"/> is verified against whatever
/// <see cref="IPaymentGateway.BuildCanonicalPayload"/> the active gateway
/// computes from these fields - HMAC-SHA256 over just the first four for the
/// sandbox, PayU's SHA-512 reverse-hash over all eight for PayU. The last
/// four are optional and null for the sandbox (its canonical payload never
/// needs them); PayU's webhook controller action always populates them,
/// since PayU's hash formula requires the transaction amount/productinfo/
/// customer name/email exactly as PayU itself hashed them.
/// </summary>
public record PaymentWebhookRequest(
    string GatewayOrderId,
    string GatewayPaymentRef,
    string Status,
    string Signature,
    decimal? Amount = null,
    string? ProductInfo = null,
    string? FirstName = null,
    string? Email = null);

/// <summary>
/// Sandbox-only convenience: since no real gateway exists to complete a
/// payment and call our webhook, this simulates that round trip for a given
/// order (task 68b). Internally it builds and signs the exact same payload
/// <see cref="PaymentWebhookRequest"/> carries and runs it through the real
/// webhook handler - nothing about verification or idempotency is skipped.
/// </summary>
public record SimulatePaymentRequest(string GatewayOrderId);

/// <summary>
/// PayU's callback shape, posted as <c>application/x-www-form-urlencoded</c>
/// (PayU never posts JSON) to a dedicated webhook route rather than the
/// sandbox's JSON one - the two gateways' callbacks have nothing in common
/// on the wire, only once normalized into <see cref="PaymentWebhookRequest"/>.
/// Property names mirror PayU's own field names exactly (verified against
/// PayU's real test environment); ASP.NET's form binder matches them
/// case-insensitively.
/// </summary>
public sealed class PayUWebhookFormPayload
{
    public string? Txnid { get; set; }
    public string? Mihpayid { get; set; }
    public string? Status { get; set; }
    public string? Hash { get; set; }
    public string? Amount { get; set; }
    public string? Productinfo { get; set; }
    public string? Firstname { get; set; }
    public string? Email { get; set; }
}

/// <summary>
/// The canonical string a webhook signature is computed over (task 69a),
/// and the two status values this project's webhook understands. Framework-
/// free and shared by whatever signs a callback (the sandbox gateway) and
/// whatever verifies one (the webhook handler), so the two can never drift
/// apart on payload format.
/// </summary>
public static class PaymentWebhookPayload
{
    public const string SuccessStatus = "success";
    public const string FailedStatus = "failed";

    public static string Build(string gatewayOrderId, string gatewayPaymentRef, string status) =>
        $"{gatewayOrderId}|{gatewayPaymentRef}|{status}";
}
