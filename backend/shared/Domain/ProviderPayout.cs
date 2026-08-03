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

    /// <summary>Free-text bank transfer reference, set by the admin once the transfer has actually been made (manual, not gateway-issued - OPEN DECISIONS #3).</summary>
    public string? PayoutReference { get; private set; }

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
