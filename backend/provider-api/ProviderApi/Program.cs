using System.Threading.RateLimiting;
using Asp.Versioning;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Nestly.Application;
using Nestly.Application.ProviderIdentity;
using Nestly.BuildingBlocks.Middleware;
using Nestly.BuildingBlocks.Results;
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

// Opt-in schema catch-up (Migrations:ApplyOnStartup) - off unless an
// operator has explicitly switched it on for this run; see
// StartupMigrationExtensions for why this runs before every other startup
// step below, including the bookability report right after it.
app.ApplyPendingMigrationsIfConfigured();

// Task 389: same bookability report as the other two hosts. It is worth a
// line here too - a provider app with an empty job list looks identical
// whether nobody has booked yet or nobody *can* book, and this is the log an
// operator opens when providers report no work. See BookabilityProbe.
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

    // Dev-only test-auth backdoor for QA/browser-automation (see
    // docs/DEVOPS.md "Dev-only provider test login"): mints a real session
    // for the seeded E2E Test Provider without going through OTP, so tools
    // that cannot type an OTP (or humans running local smoke tests) can still
    // exercise the authenticated provider-web surface.
    //
    // Belt-and-suspenders gating, in order:
    //   1. This whole route only exists on the map when IsDevelopment() is
    //      true — evaluated once at startup, not per-request. In any other
    //      environment app.MapPost below never runs, so the route 404s.
    //   2. Even within Development, the caller must present the
    //      X-Dev-Auth-Key header matching DevAuth:Key from configuration.
    //      That key lives only in appsettings.Development.json — it is never
    //      defined in appsettings.json or appsettings.Production.json, so a
    //      misconfigured non-Development environment has no key to match
    //      against even if it somehow reached this code.
    // This is wholly additive: it does not modify AuthController's real
    // login/otp/verify endpoint or ProviderLoginService.LoginWithOtpAsync in
    // any way — it only calls the same session-issuing helper they use.
    app.MapPost("/api/v1/auth/dev/login-as-provider", async (
        HttpRequest httpRequest,
        DevProviderLoginRequest? request,
        IProviderLoginService loginService,
        IConfiguration configuration,
        ILogger<Program> logger) =>
    {
        var expectedKey = configuration["DevAuth:Key"];
        var providedKey = httpRequest.Headers["X-Dev-Auth-Key"].ToString();

        if (string.IsNullOrEmpty(expectedKey) || !string.Equals(providedKey, expectedKey, StringComparison.Ordinal))
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request?.Mobile))
        {
            return Results.BadRequest(new { detail = "Mobile is required." });
        }

        logger.LogWarning(
            "SECURITY: dev-only auth bypass used (provider-api /auth/dev/login-as-provider) for mobile {Mobile}",
            request.Mobile);

        var result = await loginService.DevLoginAsync(request.Mobile);
        if (result.IsSuccess)
        {
            return Results.Ok(result.Value);
        }

        int statusCode = result.Error.Type switch
        {
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        };
        return Results.Problem(detail: result.Error.Message, statusCode: statusCode, title: result.Error.Code);
    });
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

// Serves completion-verification photos LocalDiskFileStorageService writes
// (job-completion camera upload). Read-only static serving, no
// authentication - the refs themselves are unguessable (GUID filenames),
// matching every other "reference-only" evidence field's existing
// unauthenticated-by-URL assumption (e.g. KYC document refs) rather than
// inventing a new access-control model just for this one asset type.
{
    var fileStorageOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FileStorageOptions>>().Value;
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

app.MapControllers();

// Task 190/193: real-time chat transport for the provider reply view - see
// ChatHub's doc comment for the JWT-over-query-string auth and cross-process
// (Redis backplane) design, and ChatHub.CanProviderAccessAsync for the same
// live-assignment ownership check every other provider job action uses.
app.MapHub<ChatHub>(HubRoutes.ChatPath);

// Task 273: live order tracking. provider-api mapped no hub at all before
// this - the provider side of tracking is the half that produces the data
// (location pings, en-route/arrived), so without this the provider app had
// no socket to receive the acknowledgements and status echoes on.
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

/// <summary>Request body for the dev-only login-as-provider endpoint above.</summary>
public record DevProviderLoginRequest(string? Mobile);
