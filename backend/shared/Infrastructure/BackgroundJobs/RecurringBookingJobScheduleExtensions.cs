using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nestly.Application.RecurringBookings;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.BackgroundJobs;

/// <summary>
/// Registers the recurring-booking occurrence scheduler (task 185) as a
/// Hangfire <em>recurring</em> job - this codebase's first use of
/// <see cref="IRecurringJobManager"/> (every prior job, e.g.
/// <c>ExportJobService</c>, is enqueued once, on demand). Registration has to
/// happen after <c>WebApplication.Build()</c> (it needs a built
/// <see cref="IServiceProvider"/> to resolve <see cref="IRecurringJobManager"/>),
/// which is why this is an <see cref="IApplicationBuilder"/> extension called
/// from <c>Program.cs</c>, the same shape <see cref="BackgroundJobDashboardExtensions.UseBackgroundJobsDashboard"/>
/// already uses for the same reason.
/// </summary>
public static class RecurringBookingJobScheduleExtensions
{
    /// <summary>Job id Hangfire's storage tracks this recurring registration under - stable across deploys so re-registering on every startup updates the same entry rather than accumulating duplicates.</summary>
    private const string JobId = "recurring-booking-occurrence-scheduler";

    /// <summary>Second registration for open-ended prepaid plans - see <see cref="IRecurringBookingSchedulerService.ProcessPrepaidRenewalsAsync"/>. Kept separate from the daily occurrence job so a failure in one never delays the other.</summary>
    private const string PrepaidRenewalJobId = "recurring-prepaid-renewal-scheduler";

    /// <summary>
    /// Call only from the process that actually runs a Hangfire server
    /// (<see cref="BackgroundJobOptions.ServerEnabled"/> - today, only
    /// admin-api). Registering from a process with no server would still
    /// write the schedule to shared storage, but nothing here would ever
    /// execute it, and duplicate registrations across every API process
    /// carry no benefit since Hangfire recurring jobs are storage-global,
    /// not per-process.
    /// </summary>
    public static IApplicationBuilder ScheduleRecurringBookingJob(this IApplicationBuilder app)
    {
        var backgroundJobOptions = app.ApplicationServices.GetRequiredService<IOptions<BackgroundJobOptions>>().Value;
        if (!backgroundJobOptions.ServerEnabled)
        {
            return app;
        }

        var recurringJobManager = app.ApplicationServices.GetRequiredService<IRecurringJobManager>();

        // Runs once a day, at a low-traffic hour - the sweep itself decides,
        // per plan, whether anything is actually due within its configured
        // lead time (RecurringBookingOptions.LeadTimeDays); most invocations
        // on most days will find nothing to do for a given plan.
        recurringJobManager.AddOrUpdate<IRecurringBookingSchedulerService>(
            JobId,
            scheduler => scheduler.ProcessDueOccurrencesAsync(CancellationToken.None),
            Cron.Daily(2));

        // An hour after the occurrence job, once a day: a renewal only ever needs
        // creating when a paid cycle is within PrepaidRenewalLeadDays of running out.
        recurringJobManager.AddOrUpdate<IRecurringBookingSchedulerService>(
            PrepaidRenewalJobId,
            scheduler => scheduler.ProcessPrepaidRenewalsAsync(CancellationToken.None),
            Cron.Daily(3));

        return app;
    }
}
