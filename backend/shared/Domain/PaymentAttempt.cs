using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>
/// One gateway order round-trip belonging to a <see cref="PaymentTransaction"/>
/// (SRS 23.4 payment_attempt). A booking's payment can span several of these -
/// a failed attempt followed by a retry (task 70) - while the owning
/// transaction preserves the single booking-payment mapping throughout.
/// </summary>
public class PaymentAttempt : Entity<Guid>
{
    public Guid PaymentTransactionId { get; private set; }

    /// <summary>1-based, increments per retry on the owning transaction.</summary>
    public int AttemptNumber { get; private set; }

    /// <summary>The sandbox/gateway order identifier this attempt was created against. Unique - a gateway never reuses an order id.</summary>
    public string GatewayOrderId { get; private set; } = string.Empty;

    /// <summary>Populated once the gateway callback identifies the actual payment made against this order.</summary>
    public string? GatewayPaymentRef { get; private set; }

    /// <summary>
    /// Set when this attempt is one member of a <see cref="PaymentGroup"/> -
    /// several bookings paid through a single gateway order. Such an attempt's
    /// <see cref="GatewayOrderId"/> is a synthetic per-member id; the id the
    /// gateway actually knows belongs to the group. Null for an ordinary
    /// one-booking payment.
    /// </summary>
    public Guid? PaymentGroupId { get; private set; }

    public PaymentAttemptStatus Status { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime? CompletedAtUtc { get; private set; }

    protected PaymentAttempt() { }

    public PaymentAttempt(Guid id, Guid paymentTransactionId, int attemptNumber, string gatewayOrderId, Guid? paymentGroupId = null)
        : base(id)
    {
        PaymentGroupId = paymentGroupId;
        PaymentTransactionId = paymentTransactionId;
        AttemptNumber = attemptNumber;
        GatewayOrderId = gatewayOrderId ?? throw new ArgumentException("Gateway order id is required.", nameof(gatewayOrderId));
        Status = PaymentAttemptStatus.Created;
        CreatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Applies the gateway callback outcome. Idempotent by design at the
    /// caller (task 69b) - this method itself only ever runs once per
    /// attempt because the webhook handler checks <see cref="Status"/> before
    /// invoking it, but it still guards here as a second line of defense.
    /// </summary>
    public void MarkSucceeded(string gatewayPaymentRef)
    {
        if (Status != PaymentAttemptStatus.Created)
        {
            return;
        }

        Status = PaymentAttemptStatus.Success;
        GatewayPaymentRef = gatewayPaymentRef ?? throw new ArgumentException("Gateway payment reference is required.", nameof(gatewayPaymentRef));
        CompletedAtUtc = DateTime.UtcNow;
    }

    public void MarkFailed(string reason)
    {
        if (Status != PaymentAttemptStatus.Created)
        {
            return;
        }

        Status = PaymentAttemptStatus.Failed;
        FailureReason = reason;
        CompletedAtUtc = DateTime.UtcNow;
    }
}
