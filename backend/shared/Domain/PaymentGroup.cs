using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>
/// One gateway order that pays for several bookings in a single checkout - the
/// prepaid recurring plan ("pay for all N visits now"). Every payment
/// pipeline downstream of the gateway (commission, escrow hold, refunds,
/// provider payout) stays per booking, so each member keeps its own
/// <see cref="PaymentTransaction"/> and <see cref="PaymentAttempt"/>; what the
/// group adds is the single gateway order the customer actually pays, and the
/// atomic "all members settle together or none do" rule.
///
/// <para>
/// Member attempts point back here through <see cref="PaymentAttempt.PaymentGroupId"/>
/// and carry a synthetic <see cref="PaymentAttempt.GatewayOrderId"/> (unique per
/// attempt); only <see cref="GatewayOrderId"/> is ever sent to the gateway, so a
/// webhook or a verify call resolves the group first and then its members.
/// </para>
///
/// <para>
/// A retry after a failed payment is a new group over the same member
/// transactions (a new attempt each), exactly as a single booking's retry adds
/// an attempt underneath its one transaction.
/// </para>
/// </summary>
public class PaymentGroup : AggregateRoot<Guid>
{
    public Guid CustomerId { get; private set; }

    /// <summary>
    /// The booking the customer is standing on when they pay (the payment page
    /// and the gateway return URL are keyed by it). It is always a member.
    /// </summary>
    public Guid LeadBookingId { get; private set; }

    /// <summary>The order id sent to the gateway (PayU's txnid). Unique.</summary>
    public string GatewayOrderId { get; private set; } = string.Empty;

    /// <summary>Sum of the member transactions' amounts - what the gateway is asked to charge.</summary>
    public decimal TotalAmount { get; private set; }

    public string Currency { get; private set; } = "INR";

    public int VisitCount { get; private set; }

    public PaymentGroupStatus Status { get; private set; }

    /// <summary>The gateway's payment reference once the group succeeded; shared by every member's attempt.</summary>
    public string? GatewayPaymentRef { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime? CompletedAtUtc { get; private set; }

    protected PaymentGroup() { }

    public PaymentGroup(Guid id, Guid customerId, Guid leadBookingId, string gatewayOrderId, decimal totalAmount, string currency, int visitCount)
        : base(id)
    {
        if (totalAmount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalAmount), "A payment group's total must be positive.");
        }

        if (visitCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(visitCount), "A payment group covers at least one booking.");
        }

        CustomerId = customerId;
        LeadBookingId = leadBookingId;
        GatewayOrderId = gatewayOrderId ?? throw new ArgumentException("Gateway order id is required.", nameof(gatewayOrderId));
        TotalAmount = totalAmount;
        Currency = currency ?? throw new ArgumentException("Currency is required.", nameof(currency));
        VisitCount = visitCount;
        Status = PaymentGroupStatus.Pending;
        CreatedAtUtc = DateTime.UtcNow;
    }

    public void MarkSucceeded(string gatewayPaymentRef)
    {
        if (Status != PaymentGroupStatus.Pending)
        {
            return;
        }

        Status = PaymentGroupStatus.Success;
        GatewayPaymentRef = gatewayPaymentRef;
        CompletedAtUtc = DateTime.UtcNow;
    }

    public void MarkFailed()
    {
        if (Status != PaymentGroupStatus.Pending)
        {
            return;
        }

        Status = PaymentGroupStatus.Failed;
        CompletedAtUtc = DateTime.UtcNow;
    }
}
