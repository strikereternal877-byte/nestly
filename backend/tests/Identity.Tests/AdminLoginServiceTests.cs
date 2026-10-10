using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.Identity;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Auditing;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Identity.Tests;

/// <summary>
/// Admin panel login: throttling/lockout with an unlock path, the MFA hook,
/// and login audit (SRS 12.1, tasks 95a, 95c-95g), exercised end to end
/// through <see cref="AdminLoginService"/> and the real repositories over a
/// relational database — same rationale as <see cref="LoginThrottlingTests"/>
/// for the customer path: the counting and the audit rows both happen in
/// SQL, so stubbed repositories would prove nothing.
/// </summary>
public class AdminLoginServiceTests : IDisposable
{
    private const string Email = "admin@example.com";
    private const string CorrectPassword = "correct-horse-battery";

    private readonly TestDatabase _database = new();
    private readonly Mock<IAdminMfaChallengeProvider> _mfaChallengeProvider = new();
    private Guid _adminUserId;

    public AdminLoginServiceTests()
    {
        _mfaChallengeProvider
            .Setup(m => m.VerifyAsync(It.IsAny<AdminUser>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        SeedAdminUserWithPassword();
    }

    private void SeedAdminUserWithPassword()
    {
        using var context = _database.CreateContext();
        var adminUser = new AdminUser(Guid.NewGuid(), Email, "placeholder", "Test Admin");
        adminUser.SetPasswordHash(new PasswordHasher<AdminUser>().HashPassword(adminUser, CorrectPassword));
        _adminUserId = adminUser.Id;
        context.Add(adminUser);
        context.SaveChanges();
    }

    private AdminLoginService CreateService(NestlyDbContext context, AdminAccountOptions options) =>
        new(
            new AdminUserRepository(context),
            new AdminSessionRepository(context),
            new AdminTokenService(Options.Create(new AdminJwtOptions
            {
                SigningKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
                Issuer = "Nestly",
                Audience = "Nestly.AdminUsers",
                AccessTokenMinutes = 10,
                RefreshTokenHours = 12
            })),
            _mfaChallengeProvider.Object,
            new AdminRolePermissionQueryService(context),
            new AuditLogWriter(context, new StubAuditContextProvider()),
            context,
            Options.Create(options));

    private static AdminAccountOptions OptionsWith(int maxAttempts, int lockoutMinutes = 30) =>
        new() { MaxFailedLoginAttempts = maxAttempts, LockoutMinutes = lockoutMinutes };

    private async Task<Result<AdminLoginResponse>> AttemptLoginAsync(AdminAccountOptions options, string password)
    {
        await using var context = _database.CreateContext();
        return await CreateService(context, options).LoginAsync(new AdminLoginRequest(Email, password));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public async Task The_account_locks_once_the_configured_number_of_failures_is_reached(int maxAttempts)
    {
        var options = OptionsWith(maxAttempts);

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            var result = await AttemptLoginAsync(options, "wrong-password");
            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("AdminLogin.InvalidCredentials");
        }

        // Once locked, a wrong password still reports the same generic error
        // as any other failed credential check - the lockout check now runs
        // only after a successful password verification, so a wrong
        // password never reveals that the account is locked (SRS 28.3).
        var lockedWithWrongPassword = await AttemptLoginAsync(options, "wrong-password");
        lockedWithWrongPassword.IsFailure.Should().BeTrue();
        lockedWithWrongPassword.Error.Code.Should().Be("AdminLogin.InvalidCredentials");

        // Only the correct password, once verified, surfaces the lockout detail.
        var lockedWithCorrectPassword = await AttemptLoginAsync(options, CorrectPassword);
        lockedWithCorrectPassword.IsFailure.Should().BeTrue();
        lockedWithCorrectPassword.Error.Code.Should().Be("AdminLogin.AccountLocked");
    }

    [Fact]
    public async Task A_locked_account_refuses_even_the_correct_password()
    {
        var options = OptionsWith(maxAttempts: 3);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            await AttemptLoginAsync(options, "wrong-password");
        }

        var result = await AttemptLoginAsync(options, CorrectPassword);
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AdminLogin.AccountLocked");
    }

    [Fact]
    public async Task A_correct_password_still_works_below_the_threshold_and_resets_the_counter()
    {
        var options = OptionsWith(maxAttempts: 5);

        for (int attempt = 0; attempt < 4; attempt++)
        {
            await AttemptLoginAsync(options, "wrong-password");
        }

        var result = await AttemptLoginAsync(options, CorrectPassword);
        result.IsSuccess.Should().BeTrue();
        result.Value.AccessToken.Should().NotBeNullOrEmpty();

        await using var context = _database.CreateContext();
        var adminUser = await context.Set<AdminUser>().SingleAsync(a => a.Id == _adminUserId);
        adminUser.FailedLoginAttempts.Should().Be(0);
    }

    [Fact]
    public async Task An_unknown_email_is_rejected_with_the_same_error_as_a_wrong_password()
    {
        var options = OptionsWith(maxAttempts: 5);

        await using var context = _database.CreateContext();
        var unknown = await CreateService(context, options)
            .LoginAsync(new AdminLoginRequest("nobody@example.com", "whatever"));

        var wrongPassword = await AttemptLoginAsync(options, "wrong-password");

        // Distinguishable errors would turn the login form into an
        // account-enumeration oracle (SRS 28.3), same as the customer path.
        unknown.Error.Code.Should().Be(wrongPassword.Error.Code);
        unknown.Error.Message.Should().Be(wrongPassword.Error.Message);
    }

    [Fact]
    public async Task An_explicit_unlock_clears_the_lockout_immediately()
    {
        var options = OptionsWith(maxAttempts: 3);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            await AttemptLoginAsync(options, "wrong-password");
        }

        (await AttemptLoginAsync(options, CorrectPassword)).Error.Code.Should().Be("AdminLogin.AccountLocked");

        await using (var context = _database.CreateContext())
        {
            var unlockResult = await CreateService(context, options).UnlockAsync(_adminUserId);
            unlockResult.IsSuccess.Should().BeTrue();
        }

        // The unlock path (task 95d) works without waiting out the lockout window.
        (await AttemptLoginAsync(options, CorrectPassword)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task An_inactive_account_cannot_log_in_even_with_the_correct_password()
    {
        await using (var context = _database.CreateContext())
        {
            var adminUser = await context.Set<AdminUser>().SingleAsync(a => a.Id == _adminUserId);
            adminUser.Deactivate();
            await context.SaveChangesAsync();
        }

        var result = await AttemptLoginAsync(OptionsWith(maxAttempts: 5), CorrectPassword);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AdminLogin.AccountNotActive");
    }

    [Fact]
    public async Task A_failing_mfa_challenge_blocks_login_after_a_correct_password()
    {
        _mfaChallengeProvider
            .Setup(m => m.VerifyAsync(It.IsAny<AdminUser>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.Forbidden("Mfa.ChallengeFailed", "MFA challenge failed.")));

        var result = await AttemptLoginAsync(OptionsWith(maxAttempts: 5), CorrectPassword);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Mfa.ChallengeFailed");
    }

    [Fact]
    public async Task Every_login_attempt_is_written_to_the_audit_trail()
    {
        var options = OptionsWith(maxAttempts: 10);

        await AttemptLoginAsync(options, "wrong-password");
        await AttemptLoginAsync(options, CorrectPassword);

        await using var context = _database.CreateContext();
        var entries = await context.Set<AuditLog>()
            .Where(a => a.EntityName == "AdminUser" && a.EntityId == _adminUserId.ToString())
            .OrderBy(a => a.OccurredOnUtc)
            .ToListAsync();

        entries.Should().HaveCount(2);
        entries[0].Action.Should().Be("AdminLoginFailed");
        entries[1].Action.Should().Be("AdminLoginSucceeded");
    }

    [Fact]
    public async Task An_attempt_against_an_unknown_email_is_still_audited_by_the_attempted_email()
    {
        await using var context = _database.CreateContext();
        await CreateService(context, OptionsWith(maxAttempts: 5))
            .LoginAsync(new AdminLoginRequest("nobody@example.com", "whatever"));

        var entry = await context.Set<AuditLog>()
            .SingleAsync(a => a.EntityName == "AdminUser" && a.EntityId == "nobody@example.com");

        entry.Action.Should().Be("AdminLoginFailed");
    }

    [Fact]
    public async Task A_successful_login_issues_a_refresh_token_that_can_be_redeemed_for_a_new_session()
    {
        var loginResult = await AttemptLoginAsync(OptionsWith(maxAttempts: 5), CorrectPassword);
        loginResult.IsSuccess.Should().BeTrue();
        loginResult.Value.RefreshToken.Should().NotBeNullOrEmpty();

        await using var context = _database.CreateContext();
        var refreshResult = await CreateService(context, OptionsWith(maxAttempts: 5))
            .RefreshAsync(new RefreshTokenRequest(loginResult.Value.RefreshToken));

        refreshResult.IsSuccess.Should().BeTrue();
        refreshResult.Value.AccessToken.Should().NotBeNullOrEmpty();
        refreshResult.Value.RefreshToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Refreshing_rotates_the_session_so_the_old_refresh_token_can_no_longer_be_reused()
    {
        var loginResult = await AttemptLoginAsync(OptionsWith(maxAttempts: 5), CorrectPassword);
        string originalRefreshToken = loginResult.Value.RefreshToken;

        await using var context = _database.CreateContext();
        var service = CreateService(context, OptionsWith(maxAttempts: 5));

        var firstRefresh = await service.RefreshAsync(new RefreshTokenRequest(originalRefreshToken));
        firstRefresh.IsSuccess.Should().BeTrue();
        firstRefresh.Value.RefreshToken.Should().NotBe(originalRefreshToken);

        // Replaying the now-rotated-away token must fail - this is what makes
        // an intercepted refresh token a single-use liability rather than a
        // standing one (SRS 28.3).
        var replay = await service.RefreshAsync(new RefreshTokenRequest(originalRefreshToken));
        replay.IsFailure.Should().BeTrue();
        replay.Error.Code.Should().Be("AdminLogin.InvalidRefreshToken");

        // The newly issued token, however, must still work.
        var secondRefresh = await service.RefreshAsync(new RefreshTokenRequest(firstRefresh.Value.RefreshToken));
        secondRefresh.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task An_unknown_refresh_token_is_rejected()
    {
        await using var context = _database.CreateContext();
        var result = await CreateService(context, OptionsWith(maxAttempts: 5))
            .RefreshAsync(new RefreshTokenRequest("not-a-real-token"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AdminLogin.InvalidRefreshToken");
    }

    [Fact]
    public async Task Logout_revokes_the_session_so_its_refresh_token_can_no_longer_be_used()
    {
        var loginResult = await AttemptLoginAsync(OptionsWith(maxAttempts: 5), CorrectPassword);
        string refreshToken = loginResult.Value.RefreshToken;

        await using var context = _database.CreateContext();
        var service = CreateService(context, OptionsWith(maxAttempts: 5));

        var logoutResult = await service.LogoutAsync(new LogoutRequest(refreshToken));
        logoutResult.IsSuccess.Should().BeTrue();

        var refreshAfterLogout = await service.RefreshAsync(new RefreshTokenRequest(refreshToken));
        refreshAfterLogout.IsFailure.Should().BeTrue();
        refreshAfterLogout.Error.Code.Should().Be("AdminLogin.InvalidRefreshToken");
    }

    [Fact]
    public async Task Logging_out_an_already_invalid_token_is_treated_as_success()
    {
        // The end state (no active session for that token) already holds, so
        // this must not be reported as an error to the caller.
        await using var context = _database.CreateContext();
        var result = await CreateService(context, OptionsWith(maxAttempts: 5))
            .LogoutAsync(new LogoutRequest("not-a-real-token"));

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task An_expired_session_cannot_be_refreshed()
    {
        var loginResult = await AttemptLoginAsync(OptionsWith(maxAttempts: 5), CorrectPassword);
        string refreshToken = loginResult.Value.RefreshToken;

        // Age the session's expiry into the past instead of waiting out the
        // configured RefreshTokenHours window.
        await using (var context = _database.CreateContext())
        {
            var session = await context.Set<AdminSession>().SingleAsync();
            context.Entry(session).Property(nameof(AdminSession.ExpiresAt)).CurrentValue =
                DateTime.UtcNow.AddHours(-1);
            await context.SaveChangesAsync();
        }

        await using var refreshContext = _database.CreateContext();
        var result = await CreateService(refreshContext, OptionsWith(maxAttempts: 5))
            .RefreshAsync(new RefreshTokenRequest(refreshToken));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AdminLogin.InvalidRefreshToken");
    }

    [Fact]
    public async Task A_deactivated_admin_cannot_refresh_an_existing_session()
    {
        var loginResult = await AttemptLoginAsync(OptionsWith(maxAttempts: 5), CorrectPassword);
        string refreshToken = loginResult.Value.RefreshToken;

        await using (var context = _database.CreateContext())
        {
            var adminUser = await context.Set<AdminUser>().SingleAsync(a => a.Id == _adminUserId);
            adminUser.Deactivate();
            await context.SaveChangesAsync();
        }

        await using var refreshContext = _database.CreateContext();
        var result = await CreateService(refreshContext, OptionsWith(maxAttempts: 5))
            .RefreshAsync(new RefreshTokenRequest(refreshToken));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AdminLogin.AccountNotActive");
    }

    private sealed class StubAuditContextProvider : IAuditContextProvider
    {
        public AuditContext GetCurrent() =>
            new(AuditActorType.Anonymous, ActorId: null, IpAddress: "127.0.0.1", CorrelationId: "test-correlation-id");
    }

    public void Dispose() => _database.Dispose();
}
