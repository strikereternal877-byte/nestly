using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Services;

namespace Nestly.Identity.Tests;

/// <summary>
/// Post-rebrand JWT issuer/audience (docs/OPEN-FIXES-FEATURES.csv "Token
/// issuer and audience"): newly issued tokens must carry the current brand,
/// and validation must still accept a token minted under the pre-rebrand
/// values so a session in flight during the rollout doesn't get logged out
/// the moment this change deploys - see the matching validation wiring in
/// DependencyInjection.AddJwtAuthentication/AddAdminJwtAuthentication/
/// AddProviderJwtAuthentication, which this test's <see cref="ValidationParameters"/>
/// mirrors exactly (ValidIssuers/ValidAudiences listing both the configured
/// and legacy value).
/// </summary>
public class JwtIssuerAudienceTests
{
    private const string SigningKeyBase64 = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

    [Fact]
    public void CustomerJwtOptions_DefaultsToTheCurrentBrand()
    {
        new JwtOptions().Issuer.Should().Be("Glavyx");
        new JwtOptions().Audience.Should().Be("Glavyx.Customers");
    }

    [Fact]
    public void AdminJwtOptions_DefaultsToTheCurrentBrand()
    {
        new AdminJwtOptions().Issuer.Should().Be("Glavyx");
        new AdminJwtOptions().Audience.Should().Be("Glavyx.AdminUsers");
    }

    [Fact]
    public void ProviderJwtOptions_DefaultsToTheCurrentBrand()
    {
        new ProviderJwtOptions().Issuer.Should().Be("Glavyx");
        new ProviderJwtOptions().Audience.Should().Be("Glavyx.Providers");
    }

    [Fact]
    public void TokenService_IssuesTokensWithTheCurrentIssuerAndAudience()
    {
        var options = new JwtOptions { SigningKey = SigningKeyBase64 };
        var sut = new TokenService(Options.Create(options));

        var token = sut.GenerateAccessToken(Guid.NewGuid(), "+919876543210");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token.Value);

        jwt.Issuer.Should().Be("Glavyx");
        jwt.Audiences.Should().ContainSingle().Which.Should().Be("Glavyx.Customers");
    }

    [Fact]
    public void Validation_AcceptsATokenIssuedUnderTheCurrentBrand()
    {
        var key = SigningKey();
        var token = MintToken(key, JwtOptions.LegacyIssuer, JwtOptions.LegacyAudience);

        var principal = new JwtSecurityTokenHandler().ValidateToken(
            token, ValidationParameters(key, "Glavyx", JwtOptions.LegacyIssuer, "Glavyx.Customers", JwtOptions.LegacyAudience), out _);

        principal.Should().NotBeNull();
    }

    [Fact]
    public void Validation_StillAcceptsATokenIssuedUnderThePreRebrandBrand()
    {
        // The scenario this whole fix exists for: a customer signed in
        // before the rebrand shipped still holds a token stamped "Nestly" /
        // "Nestly.Customers" - it must keep validating until it naturally
        // expires, not bounce them to /login the moment this deploys.
        var key = SigningKey();
        var token = MintToken(key, "Glavyx", "Glavyx.Customers");

        var principal = new JwtSecurityTokenHandler().ValidateToken(
            token, ValidationParameters(key, "Glavyx", JwtOptions.LegacyIssuer, "Glavyx.Customers", JwtOptions.LegacyAudience), out _);

        principal.Should().NotBeNull();
    }

    [Fact]
    public void Validation_RejectsATokenFromAnUnrelatedIssuer()
    {
        var key = SigningKey();
        var token = MintToken(key, "SomeOtherService", "Glavyx.Customers");

        var act = () => new JwtSecurityTokenHandler().ValidateToken(
            token, ValidationParameters(key, "Glavyx", JwtOptions.LegacyIssuer, "Glavyx.Customers", JwtOptions.LegacyAudience), out _);

        act.Should().Throw<SecurityTokenInvalidIssuerException>();
    }

    private static SymmetricSecurityKey SigningKey() =>
        new(Convert.FromBase64String(SigningKeyBase64));

    private static string MintToken(SymmetricSecurityKey key, string issuer, string audience)
    {
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: [],
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Mirrors the ValidIssuers/ValidAudiences construction in DependencyInjection's three Add*JwtAuthentication methods.</summary>
    private static TokenValidationParameters ValidationParameters(
        SymmetricSecurityKey key, string issuer, string legacyIssuer, string audience, string legacyAudience) =>
        new()
        {
            ValidateIssuer = true,
            ValidIssuers = [issuer, legacyIssuer],
            ValidateAudience = true,
            ValidAudiences = [audience, legacyAudience],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
}
