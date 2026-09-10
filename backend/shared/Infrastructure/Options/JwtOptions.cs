using System.ComponentModel.DataAnnotations;

namespace Nestly.Infrastructure.Options;

/// <summary>
/// Strongly typed binding of the "Jwt" configuration section (SRS 11.2.2:
/// JWT access+refresh tokens). <see cref="SigningKey"/> must come from
/// configuration/user-secrets/environment, never a literal in source.
/// </summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required, MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>
    /// Post-rebrand default (docs/OPEN-FIXES-FEATURES.csv "Token issuer and
    /// audience"). A token issued before this change still carries "Nestly" -
    /// <see cref="DependencyInjection.AddJwtAuthentication"/> validates
    /// against both values during the rollout window so those tokens keep
    /// working until they expire naturally.
    /// </summary>
    [Required]
    public string Issuer { get; set; } = "Glavyx";

    [Required]
    public string Audience { get; set; } = "Glavyx.Customers";

    /// <summary>Pre-rebrand values, still accepted on validation - see <see cref="Issuer"/>.</summary>
    public const string LegacyIssuer = "Nestly";

    public const string LegacyAudience = "Nestly.Customers";

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 30;
}
