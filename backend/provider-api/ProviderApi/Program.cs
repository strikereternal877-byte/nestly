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

// Provider JWT bearer auth (task 146b, PROVIDER.md API surface) — its own
// scheme and signing key, kept deliberately separate from the customer and
// admin ones (see DependencyInjection.AddProviderJwtAuthentication).
builder.Services.AddProviderJwtAuthentication(builder.Configuration);
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

// Rate limiting (mirrors consumer-api): partitioned by client IP since the
// OTP/login endpoints are unauthenticated — there is no provider identity yet
// to key on. The per-mobile-number lockout inside ProviderLoginService is what
// actually stops a slow, distributed brute force; this stops the fast,
// single-IP one.
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
