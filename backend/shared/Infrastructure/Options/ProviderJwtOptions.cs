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

    [Required]
    public string Issuer { get; set; } = "Nestly";

    [Required]
    public string Audience { get; set; } = "Nestly.Providers";

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 30;
}
