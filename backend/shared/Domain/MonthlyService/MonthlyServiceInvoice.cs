using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain.MonthlyService;

/// <summary>Per-status visit counts for one invoice's period, computed from the attendance register.</summary>
public sealed record MonthlyServiceVisitCounts(
    int Present,
    int CustomerUnavailable,
    int CustomerSkipped,
    int ProviderLeave,
    int Absent);

/// <summary>
/// The month-end bill for one contract, one calendar month and one
/// professional (docs/MONTHLY-SERVICE.md BILLING) - postpaid, computed from
/// attendance: billable visits x the contract's snapshotted rate. Keyed by
/// professional as well as month because an admin replacing the professional
/// mid-month means two people each earned part of that month.
/// </summary>
public class MonthlyServiceInvoice : AggregateRoot<Guid>
{
    public const int MaxReferenceLength = 100;

    public Guid ContractId { get; private set; }

    public Guid CustomerId { get; private set; }

    public Guid ProviderId { get; private set; }

    public DateOnly PeriodStart { get; private set; }

    public DateOnly PeriodEnd { get; private set; }

    public int PresentCount { get; private set; }

    public int CustomerUnavailableCount { get; private set; }

    public int CustomerSkippedCount { get; private set; }

    public int ProviderLeaveCount { get; private set; }

    public int AbsentCount { get; private set; }

    public int BillableVisits { get; private set; }

    public decimal RatePerVisit { get; private set; }

    public decimal Amount { get; private set; }

    public decimal CommissionPercent { get; private set; }

    public decimal CommissionAmount { get; private set; }

    public decimal ProviderNetAmount { get; private set; }

    public MonthlyServiceInvoiceStatus Status { get; private set; }

    public DateTime IssuedAtUtc { get; private set; }

    public DateOnly DueDate { get; private set; }

    public DateTime? PaidAtUtc { get; private set; }

    public MonthlyServicePaymentMethod? PaymentMethod { get; private set; }

    public string? PaymentReference { get; private set; }

    public Guid? RecordedByAdminUserId { get; private set; }

    protected MonthlyServiceInvoice() { }

    public MonthlyServiceInvoice(
        Guid id,
        MonthlyServiceContract contract,
        Guid providerId,
        DateOnly periodStart,
        DateOnly periodEnd,
        MonthlyServiceVisitCounts counts,
        bool billCustomerUnavailable,
        DateOnly issueDate,
        int dueDays,
        DateTime nowUtc)
        : base(id)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(counts);

        if (periodEnd < periodStart)
        {
            throw new ArgumentException("Period end cannot be before its start.", nameof(periodEnd));
        }

        if (dueDays < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dueDays));
        }

        var billable = counts.Present + (billCustomerUnavailable ? counts.CustomerUnavailable : 0);
        if (billable <= 0)
        {
            throw new InvalidOperationException("A month with no charged visits does not get an invoice.");
        }

        ContractId = contract.Id;
        CustomerId = contract.CustomerId;
        ProviderId = providerId;
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        PresentCount = counts.Present;
        CustomerUnavailableCount = counts.CustomerUnavailable;
        CustomerSkippedCount = counts.CustomerSkipped;
        ProviderLeaveCount = counts.ProviderLeave;
        AbsentCount = counts.Absent;
        BillableVisits = billable;
        RatePerVisit = contract.RatePerVisitSnapshot;
        Amount = Math.Round(billable * RatePerVisit, 2, MidpointRounding.AwayFromZero);
        CommissionPercent = contract.CommissionPercentSnapshot;
        CommissionAmount = Math.Round(Amount * CommissionPercent / 100m, 2, MidpointRounding.AwayFromZero);
        ProviderNetAmount = Amount - CommissionAmount;
        Status = MonthlyServiceInvoiceStatus.Issued;
        IssuedAtUtc = nowUtc;
        DueDate = issueDate.AddDays(dueDays);
    }

    public bool IsPaid => Status == MonthlyServiceInvoiceStatus.Paid;

    /// <summary>No-op unless unpaid and past its due date.</summary>
    public void MarkOverdueIfDue(DateOnly today)
    {
        if (Status == MonthlyServiceInvoiceStatus.Issued && today > DueDate)
        {
            Status = MonthlyServiceInvoiceStatus.Overdue;
        }
    }

    public void MarkPaid(MonthlyServicePaymentMethod method, string? reference, Guid? recordedByAdminUserId, DateTime nowUtc)
    {
        if (IsPaid)
        {
            throw new InvalidOperationException("This invoice is already paid.");
        }

        if (reference is { Length: > MaxReferenceLength })
        {
            throw new ArgumentOutOfRangeException(nameof(reference), $"Reference must be at most {MaxReferenceLength} characters.");
        }

        Status = MonthlyServiceInvoiceStatus.Paid;
        PaymentMethod = method;
        PaymentReference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        RecordedByAdminUserId = recordedByAdminUserId;
        PaidAtUtc = nowUtc;
    }
}
