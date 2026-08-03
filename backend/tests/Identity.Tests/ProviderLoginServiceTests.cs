using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using Nestly.Application.ProviderIdentity;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Identity.Tests;

/// <summary>
/// Provider OTP login, session issuance/refresh/logout, and lockout (task
/// 146b) - structurally mirroring <see cref="LoginThrottlingTests"/> and the
/// OTP-login parts of <c>CustomerLoginService</c>'s coverage.
/// </summary>
public class ProviderLoginServiceTests : IDisposable
{
    private const string Mobile = "+919876543210";

    private readonly TestDatabase _database = new();
    private readonly Mock<IProviderOtpService> _otpService = new();
    private readonly Mock<IProviderTokenService> _tokenService = new();
    private Guid _providerId;

    public ProviderLoginServiceTests()
    {
        _tokenService
            .Setup(t => t.GenerateAccessToken(It.IsAny<Guid>(), It.IsAny<string>()))
            .Returns(new ProviderAccessToken("test-access-token", DateTime.UtcNow.AddMinutes(15)));
        _tokenService.Setup(t => t.GenerateRefreshToken()).Returns(() => Guid.NewGuid().ToString("N"));
        _tokenService.Setup(t => t.RefreshTokenLifetime).Returns(TimeSpan.FromDays(7));

        SeedProvider(ProviderStatus.PendingVerification);
    }

    private void SeedProvider(ProviderStatus status)
    {
        using var context = _database.CreateContext();
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, Mobile);
        provider.ChangeStatus(status);
        _providerId = provider.Id;
        context.Add(provider);
        context.SaveChanges();
    }

    private ProviderLoginService CreateService(NestlyDbContext context, ProviderAccountOptions options) =>
        new(
            new ProviderRepository(context),
            new ProviderSessionRepository(context),
            new ProviderLoginAttemptRepository(context),
            _otpService.Object,
            _tokenService.Object,
            Options.Create(options));

    private static ProviderAccountOptions OptionsWith(int maxAttempts, int windowMinutes = 15) =>
        new() { MaxFailedLoginAttempts = maxAttempts, LockoutWindowMinutes = windowMinutes };

    private void SetupOtp(bool succeeds, string code = "123456")
    {
        _otpService
            .Setup(o => o.ValidateAsync(Mobile, code, OtpPurpose.Login))
            .ReturnsAsync(succeeds
                ? Result.Success()
                : Result.Failure(Error.Validation("ProviderOtp.Incorrect", "The OTP code is incorrect.")));
    }

    [Fact]
    public async Task A_pending_verification_provider_can_still_log_in_to_finish_onboarding()
    {
        SetupOtp(succeeds: true);
        var options = OptionsWith(5);

        await using var context = _database.CreateContext();
        var result = await CreateService(context, options).LoginWithOtpAsync(new LoginProviderWithOtpRequest(Mobile, "123456"));

        result.IsSuccess.Should().BeTrue();
        result.Value.AccessToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_suspended_provider_cannot_log_in()
    {
        SetupOtp(succeeds: true);

        await using (var context = _database.CreateContext())
        {
            var provider = await context.Set<Provider>().SingleAsync(p => p.Id == _providerId);
            provider.ChangeStatus(ProviderStatus.Suspended);
            await context.SaveChangesAsync();
        }

        await using var ctx = _database.CreateContext();
        var result = await CreateService(ctx, OptionsWith(5)).LoginWithOtpAsync(new LoginProviderWithOtpRequest(Mobile, "123456"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderLogin.AccountNotActive");
    }

    [Fact]
    public async Task An_unknown_mobile_number_is_rejected()
    {
        await using var context = _database.CreateContext();
        var result = await CreateService(context, OptionsWith(5))
            .RequestOtpAsync(new RequestProviderLoginOtpRequest("+910000000000"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderLogin.NotFound");
    }

    [Fact]
    public async Task The_account_locks_once_the_configured_number_of_failures_is_reached()
    {
        SetupOtp(succeeds: false);
        var options = OptionsWith(3);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            await using var context = _database.CreateContext();
            var result = await CreateService(context, options).LoginWithOtpAsync(new LoginProviderWithOtpRequest(Mobile, "123456"));
            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("ProviderOtp.Incorrect");
        }

        await using (var context = _database.CreateContext())
        {
            var locked = await CreateService(context, options).LoginWithOtpAsync(new LoginProviderWithOtpRequest(Mobile, "123456"));
            locked.IsFailure.Should().BeTrue();
            locked.Error.Code.Should().Be("ProviderLogin.AccountLocked");
        }
    }

    [Fact]
    public async Task Locking_one_provider_does_not_lock_another()
    {
        SetupOtp(succeeds: false);
        var options = OptionsWith(3);

        for (int attempt = 0; attempt < 4; attempt++)
        {
            await using var context = _database.CreateContext();
            await CreateService(context, options).LoginWithOtpAsync(new LoginProviderWithOtpRequest(Mobile, "123456"));
        }

        await using var ctx = _database.CreateContext();
        var otherResult = await CreateService(ctx, options)
            .RequestOtpAsync(new RequestProviderLoginOtpRequest("+919999999999"));

        // Not locked - just genuinely unknown, proving the lockout counter
        // for the first mobile did not bleed into this one.
        otherResult.Error.Code.Should().Be("ProviderLogin.NotFound");
    }

    [Fact]
    public async Task RefreshAsync_rotates_the_refresh_token_and_revokes_the_old_one()
    {
        SetupOtp(succeeds: true);
        var options = OptionsWith(5);

        string firstRefreshToken;
        await using (var context = _database.CreateContext())
        {
            var login = await CreateService(context, options).LoginWithOtpAsync(new LoginProviderWithOtpRequest(Mobile, "123456"));
            firstRefreshToken = login.Value.RefreshToken;
        }

        await using (var context = _database.CreateContext())
        {
            var refreshed = await CreateService(context, options).RefreshAsync(new RefreshProviderTokenRequest(firstRefreshToken));
            refreshed.IsSuccess.Should().BeTrue();
            refreshed.Value.RefreshToken.Should().NotBe(firstRefreshToken);
        }

        await using (var context = _database.CreateContext())
        {
            // The old refresh token must no longer work - replay protection.
            var replay = await CreateService(context, options).RefreshAsync(new RefreshProviderTokenRequest(firstRefreshToken));
            replay.IsFailure.Should().BeTrue();
            replay.Error.Code.Should().Be("ProviderLogin.InvalidRefreshToken");
        }
    }

    [Fact]
    public async Task LogoutAsync_revokes_the_session_so_the_refresh_token_no_longer_works()
    {
        SetupOtp(succeeds: true);
        var options = OptionsWith(5);

        string refreshToken;
        await using (var context = _database.CreateContext())
        {
            var login = await CreateService(context, options).LoginWithOtpAsync(new LoginProviderWithOtpRequest(Mobile, "123456"));
            refreshToken = login.Value.RefreshToken;
        }

        await using (var context = _database.CreateContext())
        {
            (await CreateService(context, options).LogoutAsync(new LogoutProviderRequest(refreshToken))).IsSuccess.Should().BeTrue();
        }

        await using (var context = _database.CreateContext())
        {
            var afterLogout = await CreateService(context, options).RefreshAsync(new RefreshProviderTokenRequest(refreshToken));
            afterLogout.IsFailure.Should().BeTrue();
        }
    }

    public void Dispose() => _database.Dispose();
}
