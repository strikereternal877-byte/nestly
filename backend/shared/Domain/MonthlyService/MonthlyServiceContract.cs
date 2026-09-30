using Nestly.BuildingBlocks.Primitives;
using Nestly.Domain.Events;

namespace Nestly.Domain.MonthlyService;

/// <summary>
/// A customer's month-long engagement with one professional
/// (docs/MONTHLY-SERVICE.md) - "Sunita comes Mon-Sat at 8am". The aggregate
/// root for the module: owns the schedule, the assigned professional and the
/// lifecycle. The day-by-day record of work lives in
/// <see cref="MonthlyServiceAttendance"/> rows, queried independently (the
/// same reasoning <see cref="AmcServiceVisit"/> gives for not being a
/// navigation collection - a year of daily rows is not something to load
/// with the contract).
///
/// Every plan term is snapshotted at request time, same convention as
/// <see cref="CustomerAmcContract"/>: an admin repricing a plan never changes
/// an existing customer's rate mid-engagement.
/// </summary>
public class MonthlyServiceContract : AggregateRoot<Guid>
{
    public const int MaxNoteLength = 500;

    private List<string> _includedTasksSnapshot = [];

    public Guid CustomerId { get; private set; }

    /// <summary>Traceability only - see the class doc comment.</summary>
    public Guid PlanId { get; private set; }

    public string PlanNameSnapshot { get; private set; } = string.Empty;

    public Guid ServiceIdSnapshot { get; private set; }

    public Guid CityIdSnapshot { get; private set; }

    public MonthlyServicePlanBasis BasisSnapshot { get; private set; }

    public decimal? HoursPerVisitSnapshot { get; private set; }

    public IReadOnlyList<string> IncludedTasksSnapshot
    {
        get => _includedTasksSnapshot;
        private set => _includedTasksSnapshot = value.ToList();
    }

    public decimal RatePerVisitSnapshot { get; private set; }

    public decimal CommissionPercentSnapshot { get; private set; }

    public Guid AddressId { get; private set; }

    public MonthlyServiceFrequency FrequencySnapshot { get; private set; }

    public int? TimesPerPeriodSnapshot { get; private set; }

    /// <summary>Visit weekdays - used by <see cref="MonthlyServiceFrequency.Weekdays"/> and <see cref="MonthlyServiceFrequency.TimesPerWeek"/>; None for a per-month schedule.</summary>
    public MonthlyServiceWeekdays Weekdays { get; private set; }

    /// <summary>Visit dates of the month as a <see cref="MonthDays"/> bitmask - used only by <see cref="MonthlyServiceFrequency.TimesPerMonth"/>; 0 otherwise.</summary>
    public int MonthDaysMask { get; private set; }

    /// <summary>Visit start, business-local time of day.</summary>
    public TimeOnly VisitStartTime { get; private set; }

    public DateOnly StartDate { get; private set; }

    /// <summary>Null = runs until cancelled.</summary>
    public DateOnly? EndDate { get; private set; }

    /// <summary>The one professional for this engagement (docs/MONTHLY-SERVICE.md BUSINESS DECISIONS #3). Null only while <see cref="MonthlyServiceContractStatus.PendingAssignment"/>.</summary>
    public Guid? ProviderId { get; private set; }

    public DateTime? ProviderAssignedAtUtc { get; private set; }

    public MonthlyServiceContractStatus Status { get; private set; }

    public MonthlyServicePauseReason? PauseReason { get; private set; }

    public string? CustomerNote { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    public DateTime? CancelledAtUtc { get; private set; }

    public string? CancellationReason { get; private set; }

    protected MonthlyServiceContract() { }

    public MonthlyServiceContract(
        Guid id,
        Guid customerId,
        MonthlyServicePlan plan,
        Guid addressId,
        MonthlyServiceWeekdays weekdays,
        TimeOnly visitStartTime,
        DateOnly startDate,
        DateOnly? endDate,
        string? customerNote,
        DateTime nowUtc,
        int monthDaysMask = 0)
        : base(id)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.IsActive)
        {
            throw new InvalidOperationException("This plan is not open to new requests.");
        }

        ValidateSchedule(plan, weekdays, monthDaysMask);

        if (endDate is { } end && end < startDate)
        {
            throw new ArgumentException("End date cannot be before the start date.", nameof(endDate));
        }

        if (customerNote is { Length: > MaxNoteLength })
        {
            throw new ArgumentOutOfRangeException(nameof(customerNote), $"Note must be at most {MaxNoteLength} characters.");
        }

        CustomerId = customerId;
        PlanId = plan.Id;
        PlanNameSnapshot = plan.Name;
        ServiceIdSnapshot = plan.ServiceId;
        CityIdSnapshot = plan.CityId;
        BasisSnapshot = plan.Basis;
        HoursPerVisitSnapshot = plan.HoursPerVisit;
        _includedTasksSnapshot = plan.IncludedTasks.ToList();
        RatePerVisitSnapshot = plan.RatePerVisit;
        CommissionPercentSnapshot = plan.CommissionPercent;
        AddressId = addressId;
        FrequencySnapshot = plan.Frequency;
        TimesPerPeriodSnapshot = plan.TimesPerPeriod;
        Weekdays = plan.Frequency == MonthlyServiceFrequency.TimesPerMonth ? MonthlyServiceWeekdays.None : weekdays;
        MonthDaysMask = plan.Frequency == MonthlyServiceFrequency.TimesPerMonth ? monthDaysMask : 0;
        VisitStartTime = visitStartTime;
        StartDate = startDate;
        EndDate = endDate;
        CustomerNote = string.IsNullOrWhiteSpace(customerNote) ? null : customerNote.Trim();
        Status = MonthlyServiceContractStatus.PendingAssignment;
        CreatedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>
    /// Visit length used for schedule-conflict checks: the hourly plan's hours,
    /// or one hour for a task-based plan (whose length is not fixed).
    /// </summary>
    public TimeSpan VisitDuration => TimeSpan.FromHours((double)(HoursPerVisitSnapshot ?? 1m));

    public bool IsScheduledOn(DateOnly date) =>
        date >= StartDate
        && (EndDate is null || date <= EndDate)
        && (FrequencySnapshot == MonthlyServiceFrequency.TimesPerMonth
            ? MonthDays.Includes(MonthDaysMask, date.Day)
            : Weekdays.Includes(date.DayOfWeek));

    private static void ValidateSchedule(MonthlyServicePlan plan, MonthlyServiceWeekdays weekdays, int monthDaysMask)
    {
        switch (plan.Frequency)
        {
            case MonthlyServiceFrequency.TimesPerMonth:
                if ((monthDaysMask & ~MonthDays.AllMask) != 0)
                {
                    throw new ArgumentException($"Dates must be between 1 and {MonthDays.MaxDay}.", nameof(monthDaysMask));
                }

                if (MonthDays.Count(monthDaysMask) != plan.TimesPerPeriod)
                {
                    throw new ArgumentException($"Choose exactly {plan.TimesPerPeriod} date(s) of the month.", nameof(monthDaysMask));
                }

                return;
            default:
                if ((weekdays & MonthlyServiceWeekdays.All) == MonthlyServiceWeekdays.None || (weekdays & ~MonthlyServiceWeekdays.All) != 0)
                {
                    throw new ArgumentException("Choose at least one valid day of the week.", nameof(weekdays));
                }

                if (plan.Frequency == MonthlyServiceFrequency.TimesPerWeek && weekdays.Count() != plan.TimesPerPeriod)
                {
                    throw new ArgumentException($"Choose exactly {plan.TimesPerPeriod} day(s) of the week.", nameof(weekdays));
                }

                return;
        }
    }

    /// <summary>
    /// Admin assigns the professional. From <see cref="MonthlyServiceContractStatus.PendingAssignment"/>
    /// this activates the contract. On a running contract it replaces the
    /// professional - an admin-only action for when the original leaves
    /// (leave days themselves are never covered by a replacement).
    /// </summary>
    public void AssignProvider(Guid providerId, DateTime nowUtc)
    {
        if (providerId == Guid.Empty)
        {
            throw new ArgumentException("A professional is required.", nameof(providerId));
        }

        if (Status == MonthlyServiceContractStatus.Cancelled)
        {
            throw new InvalidOperationException("Cannot assign a professional to a cancelled contract.");
        }

        var previousProviderId = ProviderId;
        ProviderId = providerId;
        ProviderAssignedAtUtc = nowUtc;
        RaiseDomainEvent(new MonthlyServiceProviderAssignedEvent(Id, CustomerId, providerId, previousProviderId is not null, previousProviderId));
        if (Status == MonthlyServiceContractStatus.PendingAssignment)
        {
            Status = MonthlyServiceContractStatus.Active;
        }

        UpdatedAtUtc = nowUtc;
    }

    public void Pause(MonthlyServicePauseReason reason, DateTime nowUtc)
    {
        if (Status != MonthlyServiceContractStatus.Active)
        {
            throw new InvalidOperationException($"Only an active contract can be paused (this one is {Status}).");
        }

        Status = MonthlyServiceContractStatus.Paused;
        PauseReason = reason;
        UpdatedAtUtc = nowUtc;
        if (reason == MonthlyServicePauseReason.OverdueInvoice)
        {
            RaiseDomainEvent(new MonthlyServicePausedForNonPaymentEvent(Id, CustomerId));
        }
    }

    public void Resume(DateTime nowUtc)
    {
        if (Status != MonthlyServiceContractStatus.Paused)
        {
            throw new InvalidOperationException($"Only a paused contract can be resumed (this one is {Status}).");
        }

        Status = ProviderId is null ? MonthlyServiceContractStatus.PendingAssignment : MonthlyServiceContractStatus.Active;
        PauseReason = null;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Terminal. Visits already recorded are still billed at month end; future scheduled days are removed by the application service.</summary>
    public void Cancel(string? reason, DateTime nowUtc)
    {
        if (Status == MonthlyServiceContractStatus.Cancelled)
        {
            throw new InvalidOperationException("This contract is already cancelled.");
        }

        if (reason is { Length: > MaxNoteLength })
        {
            throw new ArgumentOutOfRangeException(nameof(reason), $"Reason must be at most {MaxNoteLength} characters.");
        }

        Status = MonthlyServiceContractStatus.Cancelled;
        PauseReason = null;
        CancellationReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        CancelledAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
        RaiseDomainEvent(new MonthlyServiceCancelledEvent(Id, CustomerId, ProviderId));
    }
}
