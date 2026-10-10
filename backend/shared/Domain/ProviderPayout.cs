using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>Lifecycle of a payout batch (PROVIDER.md Financial Domain "provider_payout").</summary>
public enum ProviderPayoutStatus
{
    Pending,
    Processing,
    Paid,
    Failed
}

/// <summary>
/// How a payout's Pending -&gt; Processing transition was driven (real PayU
/// Payouts integration). This is NOT a third payout status and never gates
/// anything - <see cref="ProviderPayoutStatus"/>'s own four values remain the
/// only state machine; this is purely an admin-visibility/audit label
/// recording which of the two always-available paths (manual bank transfer,
/// or the automated "Pay via PayU" action) actually moved this payout,
/// stamped once at <see cref="ProviderPayout.MarkProcessingViaPayU"/> and left
/// unchanged for the rest of the payout's life.
/// </summary>
public enum ProviderPayoutChannel
{
    /// <summary>The existing admin-typed-it-by-hand flow (<see cref="ProviderPayout.MarkProcessing"/> via the unchanged <c>/status</c> action) - the default for every payout, including every one created before this field existed.</summary>
    Manual,

    /// <summary>Driven by a real PayU Payouts transfer (<see cref="ProviderPayout.MarkProcessingViaPayU"/>).</summary>
    PayUAutomated
}

/// <summary>
/// A payout batch owed to a provider for a period (PROVIDER.md Financial
/// Domain "provider_payout": period_start/end, total_amount, status,
/// payout_reference). OPEN DECISIONS #3: v1 payouts are manual bank
/// transfers - an admin runs a batch, then records the bank transfer
/// reference by hand as the status advances; there is no payment-gateway
/// webhook driving these transitions.
/// </summary>
public class ProviderPayout : Entity<Guid>
{
    public Guid ProviderId { get; private set; }
    public DateOnly PeriodStart { get; private set; }
    public DateOnly PeriodEnd { get; private set; }
    public decimal TotalAmount { get; private set; }
    public ProviderPayoutStatus Status { get; private set; }

    /// <summary>
    /// Free-text bank transfer reference. For the manual flow (OPEN DECISIONS
    /// #3), an admin types this in when calling <see cref="MarkPaid"/>. For a
    /// PayU-driven payout (<see cref="ProcessedVia"/> = <see cref="ProviderPayoutChannel.PayUAutomated"/>),
    /// the PayU Payouts transfer webhook calls the same <see cref="MarkPaid"/>
    /// with PayU's own <c>payuRefId</c> (the UTR-bearing reference) instead -
    /// one field, two writers, same meaning either way: "the reference that
    /// proves this transfer happened."
    /// </summary>
    public string? PayoutReference { get; private set; }

    /// <summary>Which of the two always-available paths moved this payout into Processing - see <see cref="ProviderPayoutChannel"/>'s own doc comment. Defaults to <see cref="ProviderPayoutChannel.Manual"/> for every payout unless <see cref="MarkProcessingViaPayU"/> was called.</summary>
    public ProviderPayoutChannel ProcessedVia { get; private set; } = ProviderPayoutChannel.Manual;

    public string? Notes { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    protected ProviderPayout() { }

    public ProviderPayout(Guid id, Guid providerId, DateOnly periodStart, DateOnly periodEnd, decimal totalAmount)
        : base(id)
    {
        if (periodEnd < periodStart)
        {
            throw new ArgumentException("Payout period end cannot be before its start.", nameof(periodEnd));
        }

        if (totalAmount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalAmount), "A payout must have a positive total amount.");
        }

        ProviderId = providerId;
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        TotalAmount = totalAmount;
        Status = ProviderPayoutStatus.Pending;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Admin marks the bank transfer as initiated (task 148).</summary>
    public void MarkProcessing()
    {
        EnsureTransition(ProviderPayoutStatus.Pending, ProviderPayoutStatus.Processing);
        Status = ProviderPayoutStatus.Processing;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// The real-PayU-Payouts counterpart to <see cref="MarkProcessing"/> -
    /// called once <see cref="Application.Payments.IProviderPayoutGateway.InitiateTransferAsync"/>
    /// has been accepted by PayU (not yet settled - that arrives later via the
    /// transfer webhook, which resolves to <see cref="MarkPaid"/>/<see cref="MarkFailed"/>
    /// exactly as the manual flow does). Delegates to the existing,
    /// unmodified <see cref="MarkProcessing"/> for the actual Pending -&gt;
    /// Processing transition (same legal move, same guard, same effect) and
    /// only additionally stamps <see cref="ProcessedVia"/> - this is not a
    /// second state machine, just a second way to drive the one that already
    /// exists (see <see cref="ProviderPayoutChannel"/>'s doc comment).
    /// </summary>
    public void MarkProcessingViaPayU()
    {
        MarkProcessing();
        ProcessedVia = ProviderPayoutChannel.PayUAutomated;
    }

    /// <summary>Admin records the completed manual bank transfer and its reference (task 148, OPEN DECISIONS #3).</summary>
    public void MarkPaid(string payoutReference)
    {
        EnsureTransition(ProviderPayoutStatus.Processing, ProviderPayoutStatus.Paid);
        PayoutReference = string.IsNullOrWhiteSpace(payoutReference)
            ? throw new ArgumentException("A payout reference is required to mark a payout paid.", nameof(payoutReference))
            : payoutReference;
        Status = ProviderPayoutStatus.Paid;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Admin records that the manual transfer failed (e.g. bad account details), so it can be retried.</summary>
    public void MarkFailed(string? notes)
    {
        if (Status is ProviderPayoutStatus.Paid or ProviderPayoutStatus.Failed)
        {
            throw new InvalidOperationException($"Cannot mark a {Status} payout as failed.");
        }

        Status = ProviderPayoutStatus.Failed;
        Notes = notes;
        UpdatedAt = DateTime.UtcNow;
    }

    private void EnsureTransition(ProviderPayoutStatus expectedFrom, ProviderPayoutStatus to)
    {
        if (Status != expectedFrom)
        {
            throw new InvalidOperationException($"Cannot move a payout from {Status} to {to} (expected {expectedFrom}).");
        }
    }
}
