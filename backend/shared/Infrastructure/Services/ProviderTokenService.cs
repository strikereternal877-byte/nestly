using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Nestly.Application.ProviderIdentity;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// JWT access tokens and opaque refresh tokens for providers. Same shape as
/// <see cref="TokenService"/> (customer tokens), but signed with
/// <see cref="ProviderJwtOptions"/> - see that type's doc comment for why this
/// is a separate signing key rather than a shared one.
/// </summary>
public class ProviderTokenService : IProviderTokenService
{
    private readonly ProviderJwtOptions _options;

    public ProviderTokenService(IOptions<ProviderJwtOptions> options)
    {
        _options = options.Value;
    }

    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(_options.RefreshTokenDays);

    public ProviderAccessToken GenerateAccessToken(Guid providerId, string mobile)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, providerId.ToString()),
            new Claim("mobile", mobile),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Convert.FromBase64String(_options.SigningKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: signingCredentials);

        return new ProviderAccessToken(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public string GenerateRefreshToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
}
