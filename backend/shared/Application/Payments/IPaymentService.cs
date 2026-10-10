using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.Payments;

/// <summary>
/// Creates gateway orders for a booking's payment (SRS 30.1, 11.11, tasks
/// 68a-d) and reads transaction detail. Every method is scoped to the
/// caller's own customer id.
/// </summary>
public interface IPaymentService
{
    /// <summary>
    /// Creates (or, per idempotency rules, returns the existing) gateway
    /// order for a booking. When the booking's payment previously failed,
    /// this is also the retry entry point (task 70) - it starts a new
    /// attempt on the same transaction rather than creating a new one, which
    /// is what preserves the booking-payment mapping across a retry.
    /// </summary>
    Task<Result<PaymentOrderResponse>> CreateOrderAsync(Guid customerId, CreatePaymentOrderRequest request);

    /// <summary>Full transaction + attempt history for a booking (SRS 11.11.3, task 71).</summary>
    Task<Result<PaymentTransactionResponse>> GetByBookingIdAsync(Guid customerId, Guid bookingId);

    /// <summary>
    /// Customer-facing entry point for <see cref="IPaymentWebhookService.VerifyPendingAttemptAsync"/> -
    /// same ownership check as every other method here, delegated to the
    /// webhook service for the actual gateway call and resolution, since
    /// that is where the real webhook-driven resolution logic already lives.
    /// </summary>
    Task<Result<PaymentTransactionResponse>> VerifyPendingAsync(Guid customerId, Guid bookingId);

    /// <summary>
    /// Sandbox-only (task 68b): since there is no real gateway to complete
    /// payment and call our webhook, this simulates that round trip for an
    /// order the caller owns - determines the deterministic outcome, signs
    /// it, and runs it through the exact same <see cref="IPaymentWebhookService"/>
    /// path a genuine callback would take, so nothing about verification or
    /// idempotency is bypassed.
    /// </summary>
    Task<Result> SimulateAsync(Guid customerId, SimulatePaymentRequest request);
}
