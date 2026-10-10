using System.Threading.RateLimiting;
using Asp.Versioning;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Nestly.Application;
using Nestly.BuildingBlocks.Middleware;
using Nestly.Infrastructure;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Migrations;
using Nestly.Infrastructure.Persistence.Readiness;
using Nestly.Infrastructure.Realtime;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Logging — structured, configuration-driven (see appsettings*.json).
builder.Host.UseSerilog((context, loggerConfiguration) =>
    loggerConfiguration.ReadFrom.Configuration(context.Configuration));

// Application layers.
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddJwtAuthentication(builder.Configuration);
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

// Rate limiting (SRS 11.2.2, 26): partitioned by client IP since these
// endpoints are unauthenticated — there is no customer identity yet to key
// on. The per-identifier lockout in CustomerLoginService is what actually
// stops a slow, distributed brute force; this stops the fast, single-IP one.
//
// Limits come from the "RateLimiting" section so an end-to-end suite or load
// test can be given headroom without changing the production behaviour; the
// defaults in RateLimitOptions are the production values.
var rateLimits = builder.Configuration
    .GetSection(RateLimitOptions.SectionName)
    .Get<RateLimitOptions>() ?? new RateLimitOptions();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("otp", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(rateLimits.Otp.WindowMinutes),
            PermitLimit = rateLimits.Otp.PermitLimit
        }));

    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(rateLimits.Login.WindowMinutes),
            PermitLimit = rateLimits.Login.PermitLimit
        }));

    // Task 134 (SRS 28.1): the public catalog search endpoint has no
    // customer identity to key on either, so this is IP-partitioned like
    // otp/login above.
    options.AddPolicy("search", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(rateLimits.Search.WindowMinutes),
            PermitLimit = rateLimits.Search.PermitLimit
        }));

    // Task 134 (SRS 28.1, 28.3 "payment callback abuse"): order creation is
    // authenticated, but still partitioned by IP rather than customer id - a
    // compromised or malicious account probing for fraud shares an IP with
    // itself far more reliably than it shares a stable identity across
    // freshly-registered accounts.
    options.AddPolicy("payment", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(rateLimits.Payment.WindowMinutes),
            PermitLimit = rateLimits.Payment.PermitLimit
        }));

    // Separate, more generous policy for the gateway webhook - see
    // RateLimitOptions.PaymentWebhook for why this must not share the
    // "payment" policy's tighter limit.
    options.AddPolicy("payment-webhook", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(rateLimits.PaymentWebhook.WindowMinutes),
            PermitLimit = rateLimits.PaymentWebhook.PermitLimit
        }));
});

var app = builder.Build();

// Opt-in schema catch-up (Migrations:ApplyOnStartup) - off unless an
// operator has explicitly switched it on for this run; see
// StartupMigrationExtensions for why this runs before every other startup
// step below, including the bookability report right after it.
app.ApplyPendingMigrationsIfConfigured();

// Task 389 (PRODUCTION-READINESS.md 5.1, QA-REPORT-2026-08-18 Phase 1): says
// out loud whether this database can serve a booking at all. A deployment
// restored from migrations plus the seed scripts has no service/pincode
// mappings and no slot-window rules, which every API reports as a correct,
// empty answer - so without this line the first signal is a customer who
// cannot book. Reports and returns; it never blocks startup, since an
// un-seeded database is the legitimate state of a deployment that has been
// migrated and not yet bootstrapped. See BookabilityProbe.
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
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();

// Task 190: real-time chat transport - see ChatHub's doc comment for the
// JWT-over-query-string auth and cross-process (Redis backplane) design.
app.MapHub<ChatHub>(HubRoutes.ChatPath);

// Task 273: live order tracking for the customer watching their booking.
// The same hub type provider-api and admin-api map, for the same
// cross-process reason chat has - the location pings this customer sees are
// ingested by provider-api, not here.
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
