using System.ComponentModel.DataAnnotations;
using Nestly.Domain.MonthlyService;

namespace Nestly.Infrastructure.Options;

/// <summary>Strongly typed binding of the "MonthlyService" configuration section (docs/MONTHLY-SERVICE.md).</summary>
public class MonthlyServiceOptions
{
    public const string SectionName = "MonthlyService";

    /// <summary>How many days ahead attendance rows are created, so both sides see the upcoming schedule.</summary>
    [Range(1, 60)]
    public int ScheduleHorizonDays { get; set; } = 14;

    /// <summary>A customer can skip (or restore) a visit until this many minutes before it starts.</summary>
    [Range(0, 1440)]
    public int SkipCutoffMinutes { get; set; } = 120;

    [Range(0, 600)]
    public int CheckInOpensMinutesBefore { get; set; } = 60;

    [Range(0, 720)]
    public int CheckInClosesMinutesAfter { get; set; } = 180;

    /// <summary>Whether a day the professional came but the customer was not available is charged.</summary>
    public bool BillCustomerUnavailable { get; set; } = true;

    /// <summary>Day of the month the previous month's invoices are issued (gives disputes a couple of days).</summary>
    [Range(1, 28)]
    public int InvoiceDayOfMonth { get; set; } = 2;

    [Range(0, 60)]
    public int InvoiceDueDays { get; set; } = 7;

    /// <summary>Days after the due date an unpaid invoice pauses its contract.</summary>
    [Range(0, 60)]
    public int OverdueGraceDays { get; set; } = 7;

    public MonthlyServiceAttendancePolicy ToPolicy() => new(
        TimeSpan.FromMinutes(SkipCutoffMinutes),
        TimeSpan.FromMinutes(CheckInOpensMinutesBefore),
        TimeSpan.FromMinutes(CheckInClosesMinutesAfter),
        BillCustomerUnavailable);
}
