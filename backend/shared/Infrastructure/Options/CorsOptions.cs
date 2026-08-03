namespace Nestly.Infrastructure.Options;

/// <summary>
/// Strongly typed binding of the "Cors" configuration section. Every API is
/// browser-facing (consumer-api/admin-api/provider-api each serve exactly one
/// Next.js frontend origin, task 140a's E2E suite surfaced this had no CORS
/// policy at all - a same-origin curl/server-to-server call never exercises
/// the browser's preflight check, so it went unnoticed until a real browser
/// hit it). Per docs/DEVOPS.md CONFIGURATION AND SECRETS ("all configuration
/// is external... environment specific"), allowed origins are config, not a
/// hardcoded list, since which origin is legitimate differs per environment
/// (localhost in dev, real domains once DEVOPS.md's OPEN DECISIONS on
/// hosting are resolved).
/// </summary>
public class CorsOptions
{
    public const string SectionName = "Cors";

    public string[] AllowedOrigins { get; set; } = [];
}
