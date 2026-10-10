namespace Nestly.Application.Payments;

/// <summary>
/// Vendor-agnostic payment gateway capability (SRS 30.1): create order,
/// verify a callback's signature, and refund. There is no mandated real
/// vendor for this project (no Razorpay/Stripe credentials exist) - the only
/// implementation is <c>SandboxPaymentGateway</c>, which simulates a real
/// gateway's behaviour deterministically. A production implementation would
/// satisfy this same interface without any caller needing to change.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>Creates an order/intent for the given amount (SRS 30.1 "create payment order", task 68a).</summary>
    Task<GatewayOrderResult> CreateOrderAsync(GatewayCreateOrderRequest request, CancellationToken cancellationToken = default);

    /// <summary>Issues a refund against a previously captured payment (SRS 30.1 "refund API", task 75b/c).</summary>
    Task<GatewayRefundResult> RefundAsync(GatewayRefundRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Actively asks the gateway for an order's real current status, instead
    /// of passively waiting for its webhook callback. Needed because a
    /// hosted-checkout gateway does not necessarily call back at all for a
    /// checkout the customer abandoned or explicitly cancelled before
    /// submitting a payment method - there was no completed attempt for it to
    /// report, so the webhook this project otherwise relies on exclusively
    /// never arrives, and the attempt is left stuck "Created" indefinitely. Used by
    /// <see cref="IPaymentWebhookService"/>'s equivalent verify method to give
    /// a customer sitting on the return page a real resolution within
    /// seconds, rather than the 20-minute PaymentPending expiry sweep.
    /// </summary>
    Task<GatewayVerifyResult> VerifyOrderStatusAsync(string gatewayOrderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies that <paramref name="signature"/> is a valid signature of
    /// <paramref name="canonicalPayload"/> (SRS 30.1 "webhook/callback
    /// support"; SRS 28.3 "payment callback abuse") - HMAC-style, computed
    /// against a shared secret the caller never sees.
    /// </summary>
    bool VerifyWebhookSignature(string canonicalPayload, string signature);

    /// <summary>
    /// Builds the exact string a callback's signature is computed over, from
    /// the fields a caller (a webhook controller action) extracted out of the
    /// gateway's raw callback. This is vendor-specific - the sandbox's
    /// canonical string is 3 fields joined by "|"; PayU's is a 12-field
    /// reverse-hash sequence - so it lives behind the gateway abstraction
    /// rather than hardcoded in <c>PaymentWebhookService</c>, which only
    /// needs to call <see cref="VerifyWebhookSignature"/> with whatever this
    /// returns.
    /// </summary>
    string BuildCanonicalPayload(PaymentWebhookRequest request);
}

/// <summary>
/// <paramref name="Receipt"/> is an opaque merchant reference (the booking
/// id) the gateway echoes back, not used for lookups. The <c>Customer*</c>
/// fields are optional (default null): the sandbox ignores them entirely,
/// and an off-session caller with no customer contact on hand (e.g.
/// <c>SubscriptionBillingJob</c>) can simply omit them - a real hosted-
/// checkout gateway that needs them (PayU requires firstname/email in its
/// signed hash) falls back to a synthetic placeholder rather than failing.
/// <paramref name="ExistingGatewayOrderId"/> is set when the caller wants a
/// checkout form regenerated for an <i>already-minted</i> order rather than
/// a brand new one - e.g. a customer navigating back to a still-pending
/// payment: <see cref="PaymentAttempt.GatewayOrderId"/> must stay stable
/// (the webhook looks attempts up by it), so re-running
/// <see cref="IPaymentGateway.CreateOrderAsync"/> from scratch would mint a
/// second, orphaned order id. Null (the default) means "mint a fresh one."
///
/// <para>
/// <paramref name="BookingId"/> identifies what is being paid for: a booking's
/// id for a booking payment (the original and still ordinary use), a wallet
/// top-up's id for a top-up. <paramref name="ReturnPath"/> and
/// <paramref name="ProductInfo"/> let a payment that is not a booking say where
/// the customer comes back to and what the gateway should call it; both default
/// to the booking payment's own values.
/// </para>
/// </summary>
public sealed record GatewayCreateOrderRequest(
    Guid BookingId,
    decimal Amount,
    string Currency,
    string Receipt,
    string? CustomerName = null,
    string? CustomerMobile = null,
    string? CustomerEmail = null,
    string? ExistingGatewayOrderId = null,
    string? ReturnPath = null,
    string? ProductInfo = null);

/// <summary>
/// <paramref name="CheckoutRedirectUrl"/>/<paramref name="CheckoutFormFields"/>
/// are populated only by a hosted-checkout-style gateway (the browser must
/// be redirected there to actually pay) - null for the sandbox, which has no
/// real checkout page to redirect to.
/// </summary>
public sealed record GatewayOrderResult(
    string GatewayOrderId,
    string Status,
    string? CheckoutRedirectUrl = null,
    IReadOnlyDictionary<string, string>? CheckoutFormFields = null);

public sealed record GatewayRefundRequest(string GatewayPaymentRef, decimal Amount, string Currency, string Receipt);

/// <summary><paramref name="FailureReason"/> is populated only when <paramref name="Status"/> indicates the refund did not go through - a real gateway's refund can genuinely fail (insufficient balance, already refunded, bank rejection), unlike the sandbox's unconditional success.</summary>
public sealed record GatewayRefundResult(string GatewayRefundId, string Status, string? FailureReason = null);

/// <summary>
/// <paramref name="Status"/> is one of three normalized outcomes a caller
/// must branch on: <c>"success"</c> (matches <see cref="PaymentWebhookPayload.SuccessStatus"/>,
/// resolve the attempt as succeeded), <c>"pending"</c> (the gateway is still
/// processing - do not resolve anything yet, the customer's own polling or a
/// later webhook will eventually settle it), or anything else, which is
/// treated as a definitive failure - including the case where the gateway
/// has no record of this order ever being attempted at all (the real
/// abandoned-checkout case this method exists for). <paramref name="FailureReason"/>
/// is populated only in that last case.
/// </summary>
public sealed record GatewayVerifyResult(string Status, string? GatewayPaymentRef = null, string? FailureReason = null);
