using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Nestly.Application.Identity;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// JWT access tokens for the admin panel (SRS 12.1, tasks 95a/95e). Separate
/// from <see cref="TokenService"/> (customer tokens) rather than a shared
/// parameterised method: admin tokens use their own signing key, issuer,
/// audience and lifetime (<see cref="AdminJwtOptions"/>), so a compromised
/// customer key cannot be replayed against the admin API and vice versa, and
/// the admin session lifetime can be tuned independently.
///
/// Carries RBAC claims (task 96c): a generic <see cref="ClaimTypes.Role"/> of
/// "Admin" (the same marker <c>HttpAuditContextProvider</c> checks to tell an
/// admin actor from a customer one in the audit trail), <see cref="ClaimTypes.NameIdentifier"/>
/// (so that same audit attribution can resolve an actor id), and one
/// <see cref="AdminClaimTypes.Permission"/> claim per permission code the
/// caller resolved from the account's role — this type itself makes no DB
/// call, it only encodes what it is given.
/// </summary>
public class AdminTokenService : IAdminTokenService
{
    private readonly AdminJwtOptions _options;

    public AdminTokenService(IOptions<AdminJwtOptions> options)
    {
        _options = options.Value;
    }

    public TimeSpan RefreshTokenLifetime => TimeSpan.FromHours(_options.RefreshTokenHours);

    public string GenerateRefreshToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    public AdminAccessToken GenerateAccessToken(Guid adminUserId, string email, string? roleName, IReadOnlyList<string> permissionCodes)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, adminUserId.ToString()),
            new(ClaimTypes.NameIdentifier, adminUserId.ToString()),
            new(JwtRegisteredClaimNames.Email, email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "Admin")
        };

        if (!string.IsNullOrWhiteSpace(roleName))
        {
            claims.Add(new Claim(AdminClaimTypes.AdminRoleName, roleName));
        }

        foreach (string permissionCode in permissionCodes)
        {
            claims.Add(new Claim(AdminClaimTypes.Permission, permissionCode));
        }

        var signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Convert.FromBase64String(_options.SigningKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: signingCredentials);

        return new AdminAccessToken(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
