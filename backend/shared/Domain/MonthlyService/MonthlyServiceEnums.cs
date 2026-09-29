namespace Nestly.Domain.MonthlyService;

/// <summary>How a <see cref="MonthlyServicePlan"/> defines one visit (docs/MONTHLY-SERVICE.md BUSINESS DECISIONS #1).</summary>
public enum MonthlyServicePlanBasis
{
    /// <summary>A fixed number of hours per visit (e.g. 2 hours of house help).</summary>
    Hourly,

    /// <summary>A fixed list of tasks per visit (e.g. sweeping, mopping, dishes).</summary>
    TaskBased
}

/// <summary>Days of the week a contract's professional visits. All seven = daily.</summary>
[Flags]
public enum MonthlyServiceWeekdays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    All = Monday | Tuesday | Wednesday | Thursday | Friday | Saturday | Sunday
}

public static class MonthlyServiceWeekdaysExtensions
{
    public static MonthlyServiceWeekdays ToWeekdayFlag(this DayOfWeek dayOfWeek) => dayOfWeek switch
    {
        DayOfWeek.Monday => MonthlyServiceWeekdays.Monday,
        DayOfWeek.Tuesday => MonthlyServiceWeekdays.Tuesday,
        DayOfWeek.Wednesday => MonthlyServiceWeekdays.Wednesday,
        DayOfWeek.Thursday => MonthlyServiceWeekdays.Thursday,
        DayOfWeek.Friday => MonthlyServiceWeekdays.Friday,
        DayOfWeek.Saturday => MonthlyServiceWeekdays.Saturday,
        _ => MonthlyServiceWeekdays.Sunday
    };

    public static bool Includes(this MonthlyServiceWeekdays weekdays, DayOfWeek dayOfWeek) =>
        (weekdays & dayOfWeek.ToWeekdayFlag()) != 0;
}

public enum MonthlyServiceContractStatus
{
    /// <summary>Requested by the customer; no professional assigned yet.</summary>
    PendingAssignment,

    Active,

    /// <summary>No new attendance is scheduled. See <see cref="MonthlyServicePauseReason"/>.</summary>
    Paused,

    /// <summary>Terminal.</summary>
    Cancelled
}

public enum MonthlyServicePauseReason
{
    Admin,

    /// <summary>An invoice stayed unpaid past its grace period; paying it resumes the contract.</summary>
    OverdueInvoice
}

public enum MonthlyServiceAttendanceStatus
{
    Scheduled,
    Present,
    CustomerSkipped,
    ProviderLeave,
    CustomerUnavailable,
    Absent
}

public enum MonthlyServiceAttendanceActor
{
    System,
    Customer,
    Provider,
    Admin
}

public enum MonthlyServiceDisputeStatus
{
    None,
    Open,
    Upheld,
    Rejected
}

public enum MonthlyServiceInvoiceStatus
{
    Issued,
    Overdue,
    Paid
}

public enum MonthlyServicePaymentMethod
{
    Online,
    Cash,
    Upi,
    BankTransfer
}
