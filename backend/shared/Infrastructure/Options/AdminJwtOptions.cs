using System.ComponentModel.DataAnnotations;

namespace Nestly.Infrastructure.Options;

/// <summary>
/// Strongly typed binding of the "AdminJwt" configuration section (SRS 12.1,
/// 12.1.2, tasks 95a/95e). Deliberately separate from <see cref="JwtOptions"/>
/// (customer tokens): a distinct signing key and audience means a customer
/// token can never be replayed against the admin API, even if one signing
/// key were ever compromised, and the admin session lifetime can be tuned
/// independently of the customer one.
/// </summary>
public class AdminJwtOptions
{
    public const string SectionName = "AdminJwt";

    [Required, MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>
    /// Post-rebrand default (docs/OPEN-FIXES-FEATURES.csv "Token issuer and
    /// audience"). A token issued before this change still carries "Nestly" -
    /// <see cref="DependencyInjection.AddAdminJwtAuthentication"/> validates
    /// against both values during the rollout window so those tokens keep
    /// working until they expire naturally.
    /// </summary>
    [Required]
    public string Issuer { get; set; } = "Glavyx";

    [Required]
    public string Audience { get; set; } = "Glavyx.AdminUsers";

    /// <summary>Pre-rebrand values, still accepted on validation - see <see cref="Issuer"/>.</summary>
    public const string LegacyIssuer = "Nestly";

    public const string LegacyAudience = "Nestly.AdminUsers";

    /// <summary>
    /// Admin panel access-token lifetime (SRS 12.1.2, task 95e). Deliberately
    /// shorter than the customer access token (<see cref="JwtOptions.AccessTokenMinutes"/>,
    /// 15-minute default): an admin token carries back-office privileges, so
    /// a smaller window for a leaked token to be replayed in is worth the
    /// extra silent refreshes. Same rotate-on-refresh posture as the
    /// customer/provider identities (short-lived signed JWT + a longer-lived,
    /// single-use, revocable opaque refresh token backed by
    /// <see cref="Domain.AdminSession"/> — see <see cref="RefreshTokenHours"/>):
    /// this access token alone is no longer the session-timeout mechanism, a
    /// still-active session now smooths over its expiry via
    /// <c>AdminAuthController.Refresh</c>, and only a revoked or truly
    /// expired session forces re-authentication.
    /// </summary>
    public int AccessTokenMinutes { get; set; } = 10;

    /// <summary>
    /// Admin refresh-token lifetime (rotate-on-use, mirrors <see cref="JwtOptions.RefreshTokenDays"/>
    /// for the customer identity). Deliberately far shorter than the
    /// customer's 30-day window: an admin session carries back-office
    /// privileges, so a 12-hour rotating window — roughly one workday — is
    /// the right tradeoff between usability and blast radius if a refresh
    /// token is ever leaked.
    /// </summary>
    public int RefreshTokenHours { get; set; } = 12;
}
