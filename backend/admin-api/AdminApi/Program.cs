using System.Threading.RateLimiting;
using Asp.Versioning;
using Hangfire;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.ProviderManagement;
using Nestly.Application.Amc;
using Nestly.Application.Subscriptions;
using Nestly.Application.Wallet;
using Nestly.BuildingBlocks.Middleware;
using Nestly.Infrastructure;
using Nestly.Infrastructure.BackgroundJobs;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Migrations;
using Nestly.Infrastructure.Persistence.Readiness;
using Nestly.Infrastructure.Persistence.Seed;
using Nestly.Infrastructure.Realtime;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Logging — structured, configuration-driven (see appsettings*.json).
builder.Host.UseSerilog((context, loggerConfiguration) =>
    loggerConfiguration.ReadFrom.Configuration(context.Configuration));

// Application layers.
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Admin panel JWT bearer auth (SRS 12.1, tasks 95a/95e) — its own scheme and
// signing key, kept deliberately separate from the customer one.
builder.Services.AddAdminJwtAuthentication(builder.Configuration);
builder.Services.AddNestlyCors(builder.Configuration);

// API surface.
builder.Services.AddControllers();
builder.Services
    .AddApiVersioning(options =>
    {
        options.DefaultApiVersion = new ApiVersion(1);
        options.AssumeDefaultVersionWhenUnspecified = true;
        options.ReportApiVersions = true;
        options.ApiVersionReader = new UrlSegmentApiVersionReader();
    })
    .AddMvc();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Login throttling (SRS 12.1.1, task 95c): partitioned by client IP, same
// approach as consumer-api's "login" policy. Per-account throttling/lockout
// (task 95d) is handled separately, inside AdminLoginService itself.
var rateLimits = builder.Configuration
    .GetSection(RateLimitOptions.SectionName)
    .Get<RateLimitOptions>() ?? new RateLimitOptions();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(rateLimits.Login.WindowMinutes),
            PermitLimit = rateLimits.Login.PermitLimit
        }));
});

var app = builder.Build();

// Opt-in schema catch-up (Migrations:ApplyOnStartup) - off unless an
// operator has explicitly switched it on for this run; see
// StartupMigrationExtensions for why this runs before every other startup
// step below, including the permission reconciliation right after it.
app.ApplyPendingMigrationsIfConfigured();

// Task 332 (QA-REPORT-2026-08-18 bug #5): fills in admin_permission rows for
// any module added since the last seed migration, and the default-role grants
// for them, so a fresh deployment's Super Admin really does have access to
// every module without an operator opening the permission-matrix UI once per
// module. Idempotent, and never re-grants a permission an operator has
// deliberately revoked - see AdminPermissionReconciler's doc comment.
app.ReconcileAdminPermissions();

// Task 389 (PRODUCTION-READINESS.md 5.1, QA-REPORT-2026-08-18 Phase 1):
// reports whether this database can serve a booking at all. Unlike the
// reconciliation above it writes nothing - which cities a deployment serves
// is a business decision with no catalog in code to reconcile against - so it
// names the missing rows and leaves the fix to the operator, who does it from
// this very API's admin panel or from
// database/seed/bootstrap-launch-city.sql. See BookabilityProbe.
app.ReportBookabilityReadiness();

// Pipeline order: correlation first so all downstream logs carry the id,
// then exception shielding, then request logging.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<GlobalExceptionHandlingMiddleware>();
app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// NESTLY-012: HSTS tells the browser to only ever use HTTPS for this host
// going forward - skipped in Development since local dev typically runs
// over plain HTTP.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseCors(Nestly.Infrastructure.DependencyInjection.NestlyCorsPolicy);

// Serves CMS media (task 314) LocalDiskFileStorageService writes. Read-only
// static serving, no authentication - refs are unguessable (GUID
// filenames), same convention as provider-api's completion-photo serving
// (see that Program.cs's identical block for the full rationale).
{
    var fileStorageOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Nestly.Infrastructure.Options.FileStorageOptions>>().Value;
    var uploadsDirectory = Path.Combine(app.Environment.ContentRootPath, fileStorageOptions.UploadsPath);
    Directory.CreateDirectory(uploadsDirectory);
    app.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(uploadsDirectory),
        RequestPath = fileStorageOptions.RequestPath,
    });
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Background-job dashboard (T018) — admin API only, and after authorization so
// the dashboard's admin-role filter has a populated principal to check.
app.UseBackgroundJobsDashboard();

// Task 175's wallet-credit expiry sweep: registered only when this process
// actually runs a Hangfire server (admin-api is the sole ServerEnabled=true
// process, see BackgroundJobOptions.ServerEnabled's doc comment) -
// scheduling metadata from a process that will never execute it would be
// pointless and, in Testing config (ServerEnabled=false, no live Postgres
// guaranteed at startup), would fail for no benefit. Idempotent
// re-execution is required by the retry convention
// (BackgroundJobRegistration) and satisfied by WalletCreditExpirySweepJob
// itself (see its doc comment).
if (app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BackgroundJobOptions>>().Value.ServerEnabled)
{
    RecurringJob.AddOrUpdate<IWalletCreditExpirySweepJob>(
        "wallet-credit-expiry-sweep",
        job => job.SweepAsync(CancellationToken.None),
        Cron.Daily);
}

// Task 185: registers the recurring-booking occurrence scheduler with
// Hangfire. Admin API is the only process with BackgroundJobs:ServerEnabled
// set (see appsettings.json across the three API processes), so it's the
// only one that should own this registration.
app.ScheduleRecurringBookingJob();

// PROVIDER-REFERRAL.md: closes out provider referrals that never reached
// their qualifying completed-job count within the configured expiry window.
app.ScheduleProviderReferralExpirySweepJob();

// Task 178: the subscription recurring-billing sweep, same
// ServerEnabled-guarded, idempotent-by-design registration pattern as
// wallet-credit-expiry-sweep above.
if (app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BackgroundJobOptions>>().Value.ServerEnabled)
{
    RecurringJob.AddOrUpdate<ISubscriptionBillingJob>(
        "subscription-billing-sweep",
        job => job.ProcessDueBillingAsync(CancellationToken.None),
        Cron.Daily);

    // docs/AMC.md's scheduled expiry sweep: moves overdue Active contracts to
    // Expired and raises the expiring-soon reminder. Daily, same cadence as
    // subscription-billing-sweep - an AMC term runs on a months-long scale,
    // so there is nothing to gain from a tighter cron. Same
    // ServerEnabled-guarded, idempotent-by-design registration pattern.
    RecurringJob.AddOrUpdate<IAmcContractExpirySweepJob>(
        "amc-contract-expiry-sweep",
        job => job.SweepAsync(CancellationToken.None),
        Cron.Daily);
}

// Task 240: expires abandoned PaymentPending bookings and releases their slot
// seat. Runs every 5 minutes (unlike the daily sweeps above) since the
// default expiry window itself is only 20 minutes (BookingExpiryOptions) -
// a daily cadence would hold seats far longer than the window intends. Same
// ServerEnabled-guarded, idempotent-by-design registration pattern.
if (app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BackgroundJobOptions>>().Value.ServerEnabled)
{
    RecurringJob.AddOrUpdate<IBookingExpirySweepJob>(
        "booking-expiry-sweep",
        job => job.SweepAsync(CancellationToken.None),
        "*/5 * * * *");

    // Wallet top-ups: the safety net under the gateway webhook. A pending top-up old enough to have been
    // abandoned (or whose webhook never arrived) is checked directly with the gateway and given its real
    // outcome. Every ten minutes - a lost callback is worth resolving within the hour, not the day, but there
    // is no need to ask the gateway more often than that. Registered whether or not top-ups are enabled: with
    // the feature off there are simply no pending rows and a pass is one empty indexed query.
    RecurringJob.AddOrUpdate<IWalletTopUpSweepJob>(
        "wallet-top-up-reconciliation",
        job => job.SweepAsync(CancellationToken.None),
        "*/10 * * * *");

    // Expires system-assigned BookingProviderAssignment rows a provider never
    // answered within AutoAssignmentOptions.ResponseWindowMinutes, and hands
    // the booking back to ProviderAutoAssignmentHandler for the next
    // candidate - the automated counterpart to an explicit provider reject.
    // Same 5-minute cadence as the booking-expiry sweep above: frequent
    // enough that a timed-out assignment is caught quickly without hammering
    // the database on every tick.
    RecurringJob.AddOrUpdate<IAssignmentResponseExpirySweepJob>(
        "assignment-response-expiry-sweep",
        job => job.SweepAsync(CancellationToken.None),
        "*/5 * * * *");

    // Recurring-booking payment-timing fix: attempts off-session auto-charge
    // for recurring occurrences whose plan opted in. Every 15 minutes rather
    // than the 5-minute sweeps above - attempts are gated by
    // RecurringBookingOptions.AutoChargeInitialDelayHours/RetryBackoffHours
    // (hours, not minutes), so a tighter cadence would only add empty ticks.
    RecurringJob.AddOrUpdate<IRecurringOccurrenceAutoChargeJob>(
        "recurring-occurrence-auto-charge",
        job => job.ProcessDueAttemptsAsync(CancellationToken.None),
        "*/15 * * * *");
}

// Task 333: promotes Confirmed bookings to AwaitingFulfilment as their slot
// approaches - the transition nothing performed before, and therefore the
// trigger that makes tasks 246-248's automatic assignment engine reachable on
// an ordinary booking rather than only after a reschedule or a rejection. Same
// ServerEnabled-guarded registration pattern as the sweeps above; see the
// extension's own doc comment for why the cadence is 5 minutes rather than
// daily.
app.ScheduleBookingFulfilmentPromotion();

// docs/OPEN-FIXES-FEATURES.csv "Service to pincode mapping" follow-up: the
// periodic backstop for AutoDisableUnservedMappingsAsync's grace period - see
// IServiceabilityAutoDisableSweepJob's doc comment for why a mapping cannot
// simply rely on being touched again by the next provider event.
app.ScheduleServiceabilityAutoDisableSweepJob();

// Task 294: delivers customer notifications whose in-process, post-commit
// dispatch never completed. This is the "and a retry path that does not depend
// on the in-process handler having run" half of the rule docs/ARCHITECTURE.md
// sets out under "DOMAIN EVENT DISPATCH AND DELIVERY" - without it the durable
// intent rows would accumulate and nothing would ever send them.
app.ScheduleNotificationIntentSweep();

app.MapControllers();

// Task 190/193: the same ChatHub type consumer-api maps, at the same path -
// see its doc comment for why one shared hub type (behind the Redis
// backplane, not two independent per-API hubs) is required for cross-process
// delivery between a customer's connection and an admin's.
app.MapHub<ChatHub>(HubRoutes.ChatPath);

// Task 273: live order tracking for an admin supervising fulfilment - same
// hub type as the other two APIs, gated on the bookings-read permission
// claim (BookingTrackingAuthorizer).
app.MapHub<BookingTrackingHub>(HubRoutes.TrackingPath);

// Liveness: process is up. Readiness: critical dependencies reachable.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
// Bootstrap: the database holds enough data for a customer to book something
// (task 389). Separate from /health/ready because the plain-text writer those
// two use reports only an aggregate word - this one names each missing link.
app.MapBookabilityHealthCheck();

// Task 137a-c (SRS 29.6, DEVOPS.md OBSERVABILITY): Prometheus scrape
// endpoint for the payment/booking/notification counters and histograms
// registered in AddInfrastructure - unauthenticated, same as the health
// endpoints above, since this is meant for an internal scraper behind the
// network boundary rather than a public consumer.
app.MapPrometheusScrapingEndpoint("/metrics");

app.Run();
