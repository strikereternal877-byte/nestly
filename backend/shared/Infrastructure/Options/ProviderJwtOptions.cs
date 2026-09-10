using System.ComponentModel.DataAnnotations;

namespace Nestly.Infrastructure.Options;

/// <summary>
/// Strongly typed binding of the "ProviderJwt" configuration section. Own
/// signing key/issuer/audience, same reasoning as <see cref="AdminJwtOptions"/>:
/// a customer or admin token must never be replayable against the provider
/// API. No <c>ValidateOnStart</c> on its registration in
/// <c>DependencyInjection.AddInfrastructure</c> - neither admin-api nor
/// consumer-api define a "ProviderJwt" section, only the future provider-api
/// (task 149) will, so eager validation would fail their startup for a
/// section they have no reason to configure.
/// </summary>
public class ProviderJwtOptions
{
    public const string SectionName = "ProviderJwt";

    [Required, MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>
    /// Post-rebrand default (docs/OPEN-FIXES-FEATURES.csv "Token issuer and
    /// audience"). A token issued before this change still carries "Nestly" -
    /// <see cref="DependencyInjection.AddProviderJwtAuthentication"/> validates
    /// against both values during the rollout window so those tokens keep
    /// working until they expire naturally.
    /// </summary>
    [Required]
    public string Issuer { get; set; } = "Glavyx";

    [Required]
    public string Audience { get; set; } = "Glavyx.Providers";

    /// <summary>Pre-rebrand values, still accepted on validation - see <see cref="Issuer"/>.</summary>
    public const string LegacyIssuer = "Nestly";

    public const string LegacyAudience = "Nestly.Providers";

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 30;
}
