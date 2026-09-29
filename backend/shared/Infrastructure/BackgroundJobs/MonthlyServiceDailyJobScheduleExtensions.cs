using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nestly.Application.MonthlyService;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.BackgroundJobs;

/// <summary>
/// Registers the Monthly Service daily sweep (docs/MONTHLY-SERVICE.md) as a
/// Hangfire recurring job, mirrors <see cref="ProviderReferralExpirySweepJobScheduleExtensions"/>.
/// </summary>
public static class MonthlyServiceDailyJobScheduleExtensions
{
    private const string JobId = "monthly-service-daily";

    /// <summary>Call only from the process that actually runs a Hangfire server (see RecurringBookingJobScheduleExtensions' doc comment for why).</summary>
    public static IApplicationBuilder ScheduleMonthlyServiceDailyJob(this IApplicationBuilder app)
    {
        var backgroundJobOptions = app.ApplicationServices.GetRequiredService<IOptions<BackgroundJobOptions>>().Value;
        if (!backgroundJobOptions.ServerEnabled)
        {
            return app;
        }

        var recurringJobManager = app.ApplicationServices.GetRequiredService<IRecurringJobManager>();

        // 19:00 UTC = 00:30 in Asia/Kolkata: just after the business day ends,
        // so yesterday's unmarked visits close and the new day is scheduled
        // before anyone starts work. Every step is idempotent.
        recurringJobManager.AddOrUpdate<IMonthlyServiceDailyJob>(
            JobId,
            job => job.RunAsync(CancellationToken.None),
            Cron.Daily(19));

        return app;
    }
}
