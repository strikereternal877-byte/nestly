using System.ComponentModel.DataAnnotations;

namespace Nestly.Infrastructure.Options;

/// <summary>
/// Strongly typed binding of the "Otp" configuration section. <see cref="Pepper"/>
/// is a server-side secret mixed into every OTP hash via HMAC-SHA256 so a
/// stolen <c>CodeHash</c> row cannot be reversed with a precomputed table over
/// all 1,000,000 six-digit codes (NESTLY-005) - must come from configuration/
/// user-secrets/environment, never a literal in source, same rule as
/// <see cref="JwtOptions.SigningKey"/>.
/// </summary>
public class OtpOptions
{
    public const string SectionName = "Otp";

    [Required, MinLength(32)]
    public string Pepper { get; set; } = string.Empty;

    /// <summary>
    /// Lets <c>OtpService</c>/<c>ProviderOtpService</c> accept a fixed
    /// well-known code instead of the real one - for local testing only,
    /// where the sandbox notification provider deliberately never exposes
    /// the real code (no-secrets-in-logs rule). Defaults to false and is set
    /// true only in appsettings.Development.json, never in appsettings.json
    /// or any deployed environment's config, so it cannot reach staging/production.
    /// </summary>
    public bool AllowDevBypass { get; set; }
}
