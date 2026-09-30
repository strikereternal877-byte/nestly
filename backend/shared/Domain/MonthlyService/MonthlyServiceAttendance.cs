using System.Security.Cryptography;
using Nestly.BuildingBlocks.Primitives;
using Nestly.Domain.Events;

namespace Nestly.Domain.MonthlyService;

/// <summary>
/// The time rules every attendance transition is checked against
/// (docs/MONTHLY-SERVICE.md ATTENDANCE SYSTEM). Bound from configuration by
/// the application layer and passed in, so the entity stays free of I/O.
/// </summary>
public sealed record MonthlyServiceAttendancePolicy(
    TimeSpan SkipCutoffBeforeVisit,
    TimeSpan CheckInOpensBeforeVisit,
    TimeSpan CheckInClosesAfterVisit,
    bool BillCustomerUnavailable);

/// <summary>
/// One scheduled day of a <see cref="MonthlyServiceContract"/> and what
/// actually happened on it - the attendance register that month-end billing
/// is computed from. Created ahead of time as
/// <see cref="MonthlyServiceAttendanceStatus.Scheduled"/> so both sides see
/// the upcoming schedule.
///
/// Every time comparison takes <c>nowLocal</c> - the business-timezone wall
/// clock - because <see cref="Date"/> and <see cref="VisitStartTime"/> are
/// business-local values; stamps take <c>nowUtc</c>.
///
/// Once <see cref="InvoiceId"/> is set the row is locked: its month has been
/// billed and nothing about it may change.
/// </summary>
public class MonthlyServiceAttendance : AggregateRoot<Guid>
{
    public const int DayCodeLength = 4;
    public const int MaxNoteLength = 500;

    public Guid ContractId { get; private set; }

    public Guid CustomerId { get; private set; }

    /// <summary>The professional expected that day - snapshotted, so a later reassignment never rewrites who was expected on past days.</summary>
    public Guid ProviderId { get; private set; }

    public DateOnly Date { get; private set; }

    public TimeOnly VisitStartTime { get; private set; }

    public MonthlyServiceAttendanceStatus Status { get; private set; }

    /// <summary>Random 4-digit code shown only in the customer's app; the professional enters it at check-in as proof of presence.</summary>
    public string DayCode { get; private set; } = string.Empty;

    public DateTime? CheckedInAtUtc { get; private set; }

    public DateTime? CheckedOutAtUtc { get; private set; }

    public decimal? CheckInLatitude { get; private set; }

    public decimal? CheckInLongitude { get; private set; }

    public MonthlyServiceAttendanceActor? MarkedBy { get; private set; }

    public DateTime? MarkedAtUtc { get; private set; }

    public string? Note { get; private set; }

    public MonthlyServiceDisputeStatus DisputeStatus { get; private set; }

    public string? DisputeReason { get; private set; }

    public DateTime? DisputeRaisedAtUtc { get; private set; }

    public DateTime? DisputeResolvedAtUtc { get; private set; }

    public string? DisputeResolutionNote { get; private set; }

    public Guid? InvoiceId { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    protected MonthlyServiceAttendance() { }

    public MonthlyServiceAttendance(
        Guid id,
        Guid contractId,
        Guid customerId,
        Guid providerId,
        DateOnly date,
        TimeOnly visitStartTime,
        DateTime nowUtc)
        : base(id)
    {
        ContractId = contractId;
        CustomerId = customerId;
        ProviderId = providerId;
        Date = date;
        VisitStartTime = visitStartTime;
        Status = MonthlyServiceAttendanceStatus.Scheduled;
        DayCode = RandomNumberGenerator.GetInt32(0, 10_000).ToString("D4");
        DisputeStatus = MonthlyServiceDisputeStatus.None;
        CreatedAtUtc = nowUtc;
    }

    public DateTime VisitStartLocal => Date.ToDateTime(VisitStartTime);

    public bool IsInvoiced => InvoiceId is not null;

    public bool IsBillable(bool billCustomerUnavailable) =>
        Status == MonthlyServiceAttendanceStatus.Present
        || (billCustomerUnavailable && Status == MonthlyServiceAttendanceStatus.CustomerUnavailable);

    // ---- Customer ----

    public void Skip(DateTime nowLocal, DateTime nowUtc, MonthlyServiceAttendancePolicy policy)
    {
        EnsureNotInvoiced();
        EnsureStatus(MonthlyServiceAttendanceStatus.Scheduled, "Only a scheduled visit can be skipped.");
        if (nowLocal > VisitStartLocal - policy.SkipCutoffBeforeVisit)
        {
            throw new InvalidOperationException(
                $"Visits can be skipped up to {FormatSpan(policy.SkipCutoffBeforeVisit)} before the visit starts.");
        }

        SetStatus(MonthlyServiceAttendanceStatus.CustomerSkipped, MonthlyServiceAttendanceActor.Customer, nowUtc, null);
        RaiseDomainEvent(new MonthlyServiceVisitSkippedEvent(Id, ContractId, CustomerId, ProviderId, Date));
    }

    public void Unskip(DateTime nowLocal, DateTime nowUtc, MonthlyServiceAttendancePolicy policy)
    {
        EnsureNotInvoiced();
        EnsureStatus(MonthlyServiceAttendanceStatus.CustomerSkipped, "This visit is not skipped.");
        if (nowLocal > VisitStartLocal - policy.SkipCutoffBeforeVisit)
        {
            throw new InvalidOperationException("It is too late to restore this visit.");
        }

        Status = MonthlyServiceAttendanceStatus.Scheduled;
        MarkedBy = null;
        MarkedAtUtc = null;
    }

    /// <summary>Fallback when the code flow fails: the customer confirms the professional came, on the day itself.</summary>
    public void ConfirmByCustomer(DateTime nowLocal, DateTime nowUtc, MonthlyServiceAttendancePolicy policy)
    {
        EnsureNotInvoiced();
        EnsureStatus(MonthlyServiceAttendanceStatus.Scheduled, "Only a scheduled visit can be confirmed.");
        if (DateOnly.FromDateTime(nowLocal) != Date || nowLocal < VisitStartLocal - policy.CheckInOpensBeforeVisit)
        {
            throw new InvalidOperationException("A visit can only be confirmed on its own day, once it is due.");
        }

        SetStatus(MonthlyServiceAttendanceStatus.Present, MonthlyServiceAttendanceActor.Customer, nowUtc, null);
    }

    public void RaiseDispute(string reason, DateTime nowUtc, MonthlyServiceAttendancePolicy policy)
    {
        EnsureNotInvoiced();
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Tell us what went wrong.", nameof(reason));
        }

        if (reason.Length > MaxNoteLength)
        {
            throw new ArgumentOutOfRangeException(nameof(reason), $"Reason must be at most {MaxNoteLength} characters.");
        }

        if (!IsBillable(policy.BillCustomerUnavailable))
        {
            throw new InvalidOperationException("Only a charged visit can be disputed.");
        }

        if (DisputeStatus != MonthlyServiceDisputeStatus.None)
        {
            throw new InvalidOperationException("This visit has already been disputed.");
        }

        DisputeStatus = MonthlyServiceDisputeStatus.Open;
        DisputeReason = reason.Trim();
        DisputeRaisedAtUtc = nowUtc;
    }

    // ---- Professional ----

    public void CheckIn(string code, DateTime nowLocal, DateTime nowUtc, MonthlyServiceAttendancePolicy policy, decimal? latitude, decimal? longitude)
    {
        EnsureNotInvoiced();
        EnsureStatus(MonthlyServiceAttendanceStatus.Scheduled, "Only a scheduled visit can be checked in.");
        if (nowLocal < VisitStartLocal - policy.CheckInOpensBeforeVisit || nowLocal > VisitStartLocal + policy.CheckInClosesAfterVisit)
        {
            throw new InvalidOperationException(
                $"Check-in is open from {FormatSpan(policy.CheckInOpensBeforeVisit)} before to {FormatSpan(policy.CheckInClosesAfterVisit)} after the visit start.");
        }

        var supplied = (code ?? string.Empty).Trim();
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(supplied),
                System.Text.Encoding.ASCII.GetBytes(DayCode)))
        {
            throw new ArgumentException("That code is not correct. Ask the customer for today's code.", nameof(code));
        }

        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), "Invalid coordinates.");
        }

        SetStatus(MonthlyServiceAttendanceStatus.Present, MonthlyServiceAttendanceActor.Provider, nowUtc, null);
        CheckedInAtUtc = nowUtc;
        CheckInLatitude = latitude;
        CheckInLongitude = longitude;
    }

    public void CheckOut(DateTime nowUtc)
    {
        EnsureNotInvoiced();
        if (Status != MonthlyServiceAttendanceStatus.Present || CheckedInAtUtc is null)
        {
            throw new InvalidOperationException("Check in before checking out.");
        }

        if (CheckedOutAtUtc is not null)
        {
            throw new InvalidOperationException("Already checked out.");
        }

        CheckedOutAtUtc = nowUtc;
    }

    public void MarkCustomerUnavailable(string? note, DateTime nowLocal, DateTime nowUtc)
    {
        EnsureNotInvoiced();
        EnsureStatus(MonthlyServiceAttendanceStatus.Scheduled, "Only a scheduled visit can be marked.");
        if (DateOnly.FromDateTime(nowLocal) != Date || nowLocal < VisitStartLocal)
        {
            throw new InvalidOperationException("This can only be reported on the day, after the visit time.");
        }

        SetStatus(MonthlyServiceAttendanceStatus.CustomerUnavailable, MonthlyServiceAttendanceActor.Provider, nowUtc, note);
    }

    public void MarkLeave(string? note, DateTime nowLocal, DateTime nowUtc)
    {
        EnsureNotInvoiced();
        EnsureStatus(MonthlyServiceAttendanceStatus.Scheduled, "Only a scheduled visit can be marked as leave.");
        if (nowLocal >= VisitStartLocal)
        {
            throw new InvalidOperationException("Leave must be marked before the visit time.");
        }

        SetStatus(MonthlyServiceAttendanceStatus.ProviderLeave, MonthlyServiceAttendanceActor.Provider, nowUtc, note);
        RaiseDomainEvent(new MonthlyServiceLeaveMarkedEvent(Id, ContractId, CustomerId, ProviderId, Date));
    }

    public void CancelLeave(DateTime nowLocal)
    {
        EnsureNotInvoiced();
        EnsureStatus(MonthlyServiceAttendanceStatus.ProviderLeave, "This day is not marked as leave.");
        if (nowLocal >= VisitStartLocal)
        {
            throw new InvalidOperationException("It is too late to cancel this leave.");
        }

        Status = MonthlyServiceAttendanceStatus.Scheduled;
        MarkedBy = null;
        MarkedAtUtc = null;
        Note = null;
    }

    // ---- System / admin ----

    /// <summary>Day closed with nothing recorded. No-op for any other status.</summary>
    public void CloseAsAbsent(DateTime nowUtc)
    {
        if (Status != MonthlyServiceAttendanceStatus.Scheduled || IsInvoiced)
        {
            return;
        }

        SetStatus(MonthlyServiceAttendanceStatus.Absent, MonthlyServiceAttendanceActor.System, nowUtc, null);
    }

    /// <summary>
    /// Admin resolution of an open dispute. Upheld requires the corrected,
    /// non-billable status the day should have had; rejected leaves the day
    /// as it was.
    /// </summary>
    public void ResolveDispute(bool upheld, MonthlyServiceAttendanceStatus? correctedStatus, string? note, DateTime nowUtc, MonthlyServiceAttendancePolicy policy)
    {
        EnsureNotInvoiced();
        if (DisputeStatus != MonthlyServiceDisputeStatus.Open)
        {
            throw new InvalidOperationException("There is no open dispute on this visit.");
        }

        if (upheld)
        {
            if (correctedStatus is not { } corrected
                || corrected == MonthlyServiceAttendanceStatus.Scheduled
                || IsBillableStatus(corrected, policy.BillCustomerUnavailable))
            {
                throw new ArgumentException("Choose the non-charged status this visit should have.", nameof(correctedStatus));
            }

            SetStatus(corrected, MonthlyServiceAttendanceActor.Admin, nowUtc, Note);
        }

        DisputeStatus = upheld ? MonthlyServiceDisputeStatus.Upheld : MonthlyServiceDisputeStatus.Rejected;
        DisputeResolutionNote = string.IsNullOrWhiteSpace(note) ? null : TrimNote(note);
        DisputeResolvedAtUtc = nowUtc;
    }

    /// <summary>Admin correction of any past or current day before it is invoiced (e.g. the professional came but nobody recorded it).</summary>
    public void CorrectByAdmin(MonthlyServiceAttendanceStatus status, string? note, DateTime nowUtc)
    {
        EnsureNotInvoiced();
        if (status == MonthlyServiceAttendanceStatus.Scheduled)
        {
            throw new ArgumentException("Choose what actually happened on this day.", nameof(status));
        }

        SetStatus(status, MonthlyServiceAttendanceActor.Admin, nowUtc, note);
        if (DisputeStatus == MonthlyServiceDisputeStatus.Open)
        {
            DisputeStatus = MonthlyServiceDisputeStatus.Upheld;
            DisputeResolvedAtUtc = nowUtc;
            DisputeResolutionNote = Note;
        }
    }

    /// <summary>Moves a not-yet-happened customer skip to the contract's new professional after a replacement.</summary>
    public void ReassignProvider(Guid providerId)
    {
        EnsureNotInvoiced();
        if (Status != MonthlyServiceAttendanceStatus.CustomerSkipped)
        {
            throw new InvalidOperationException("Only a customer-skipped day moves to the new professional.");
        }

        ProviderId = providerId;
    }

    public void AttachToInvoice(Guid invoiceId)
    {
        EnsureNotInvoiced();
        if (Status == MonthlyServiceAttendanceStatus.Scheduled)
        {
            throw new InvalidOperationException("A day still scheduled cannot be invoiced.");
        }

        if (DisputeStatus == MonthlyServiceDisputeStatus.Open)
        {
            throw new InvalidOperationException("A day with an open dispute cannot be invoiced.");
        }

        InvoiceId = invoiceId;
    }

    public static bool IsBillableStatus(MonthlyServiceAttendanceStatus status, bool billCustomerUnavailable) =>
        status == MonthlyServiceAttendanceStatus.Present
        || (billCustomerUnavailable && status == MonthlyServiceAttendanceStatus.CustomerUnavailable);

    private void SetStatus(MonthlyServiceAttendanceStatus status, MonthlyServiceAttendanceActor actor, DateTime nowUtc, string? note)
    {
        Status = status;
        MarkedBy = actor;
        MarkedAtUtc = nowUtc;
        Note = string.IsNullOrWhiteSpace(note) ? null : TrimNote(note);
    }

    private static string TrimNote(string note)
    {
        var trimmed = note.Trim();
        if (trimmed.Length > MaxNoteLength)
        {
            throw new ArgumentOutOfRangeException(nameof(note), $"Note must be at most {MaxNoteLength} characters.");
        }

        return trimmed;
    }

    private void EnsureStatus(MonthlyServiceAttendanceStatus expected, string message)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException(message);
        }
    }

    private void EnsureNotInvoiced()
    {
        if (IsInvoiced)
        {
            throw new InvalidOperationException("This day has already been billed and can no longer change.");
        }
    }

    private static string FormatSpan(TimeSpan span) =>
        span.TotalMinutes % 60 == 0 ? $"{(int)span.TotalHours} hour(s)" : $"{(int)span.TotalMinutes} minutes";
}
