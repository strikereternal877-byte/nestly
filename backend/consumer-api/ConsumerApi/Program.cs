using System.Threading.RateLimiting;
using Asp.Versioning;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Nestly.Application;
using Nestly.BuildingBlocks.Middleware;
using Nestly.Infrastructure;
using Nestly.Infrastructure.Options;
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

// Task 175: wallet credit expiry sweep, registered once here (not inside
// AddInfrastructure, which admin-api/partner-api also call) - see
// WalletCreditExpirySweepHostedService's doc comment for why consumer-api
// is the single owner of this recurring job.
builder.Services.AddHostedService<Nestly.Infrastructure.Services.WalletCreditExpirySweepHostedService>();

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

app.UseHttpsRedirection();

app.UseCors(Nestly.Infrastructure.DependencyInjection.NestlyCorsPolicy);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();

// Liveness: process is up. Readiness: critical dependencies reachable.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

// Task 137a-c (SRS 29.6, DEVOPS.md OBSERVABILITY): Prometheus scrape
// endpoint for the payment/booking/notification counters and histograms
// registered in AddInfrastructure - unauthenticated, same as the health
// endpoints above, since this is meant for an internal scraper behind the
// network boundary rather than a public consumer.
app.MapPrometheusScrapingEndpoint("/metrics");

app.Run();
