namespace Nestly.Domain.MonthlyService;

/// <summary>How a <see cref="MonthlyServicePlan"/> defines one visit (docs/MONTHLY-SERVICE.md BUSINESS DECISIONS #1).</summary>
public enum MonthlyServicePlanBasis
{
    /// <summary>A fixed number of hours per visit (e.g. 2 hours of house help).</summary>
    Hourly,

    /// <summary>A fixed list of tasks per visit (e.g. sweeping, mopping, dishes).</summary>
    TaskBased
}

/// <summary>
/// How a plan's visits are scheduled (docs/MONTHLY-SERVICE.md SCHEDULES).
/// A maid comes on whichever weekdays the customer picks; a car wash comes
/// a fixed number of times per week or per month.
/// </summary>
public enum MonthlyServiceFrequency
{
    /// <summary>Customer picks any set of weekdays (all seven = daily).</summary>
    Weekdays,

    /// <summary>Exactly <c>TimesPerPeriod</c> weekdays, chosen by the customer.</summary>
    TimesPerWeek,

    /// <summary>Exactly <c>TimesPerPeriod</c> dates of the month (1-28, so every month has them), chosen by the customer.</summary>
    TimesPerMonth
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

    public static int Count(this MonthlyServiceWeekdays weekdays) =>
        System.Numerics.BitOperations.PopCount((uint)(weekdays & MonthlyServiceWeekdays.All));
}

/// <summary>
/// Dates of the month (1-28) a <see cref="MonthlyServiceFrequency.TimesPerMonth"/>
/// contract is visited on, as a bitmask: bit (day - 1) set = visit that day.
/// Capped at 28 so every month, February included, has every chosen date.
/// </summary>
public static class MonthDays
{
    public const int MaxDay = 28;
    public const int AllMask = (1 << MaxDay) - 1;

    public static int ToMask(IEnumerable<int> days)
    {
        var mask = 0;
        foreach (var day in days)
        {
            if (day is < 1 or > MaxDay)
            {
                throw new ArgumentOutOfRangeException(nameof(days), $"Dates must be between 1 and {MaxDay}.");
            }

            mask |= 1 << (day - 1);
        }

        return mask;
    }

    public static IReadOnlyList<int> FromMask(int mask) =>
        Enumerable.Range(1, MaxDay).Where(day => (mask & (1 << (day - 1))) != 0).ToList();

    public static bool Includes(int mask, int dayOfMonth) =>
        dayOfMonth is >= 1 and <= MaxDay && (mask & (1 << (dayOfMonth - 1))) != 0;

    public static int Count(int mask) => System.Numerics.BitOperations.PopCount((uint)(mask & AllMask));
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
