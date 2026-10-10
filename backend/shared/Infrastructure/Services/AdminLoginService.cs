using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.Identity;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Admin panel login, session issuance/refresh/logout, lockout and login
/// audit (SRS 12.1, 12.1.2, tasks 95a, 95c-95g). Mirrors
/// <see cref="CustomerLoginService"/>'s shape but is its own type rather than
/// a shared base class: the two identities have different lockout state (a
/// persisted counter here vs. a rolling <see cref="LoginAttempt"/> window for
/// customers — an admin account is provisioned rather than self-registered,
/// so an explicit administrative unlock path matters more than it does for a
/// customer), different token issuance (<see cref="IAdminTokenService"/>,
/// its own signing key/audience/lifetimes), and a hard requirement to audit
/// every attempt (task 95g) that the customer path does not have. The
/// refresh/logout session mechanics themselves (rotate-on-use, revoke on
/// logout) are the same design as <see cref="CustomerLoginService"/>'s,
/// backed by <see cref="AdminSession"/> instead of <c>CustomerSession</c>.
/// </summary>
public class AdminLoginService : IAdminLoginService
{
    private static readonly Error InvalidCredentials =
        Error.Unauthorized("AdminLogin.InvalidCredentials", "Invalid email or password.");

    private readonly IAdminUserRepository _adminUserRepository;
    private readonly IAdminSessionRepository _sessionRepository;
    private readonly IAdminTokenService _tokenService;
    private readonly IAdminMfaChallengeProvider _mfaChallengeProvider;
    private readonly IAdminRolePermissionQueryService _rolePermissionQueryService;
    private readonly IAuditLogWriter _auditLogWriter;
    private readonly NestlyDbContext _dbContext;
    private readonly AdminAccountOptions _options;
    private readonly PasswordHasher<AdminUser> _passwordHasher = new();

    public AdminLoginService(
        IAdminUserRepository adminUserRepository,
        IAdminSessionRepository sessionRepository,
        IAdminTokenService tokenService,
        IAdminMfaChallengeProvider mfaChallengeProvider,
        IAdminRolePermissionQueryService rolePermissionQueryService,
        IAuditLogWriter auditLogWriter,
        NestlyDbContext dbContext,
        IOptions<AdminAccountOptions> options)
    {
        _adminUserRepository = adminUserRepository;
        _sessionRepository = sessionRepository;
        _tokenService = tokenService;
        _mfaChallengeProvider = mfaChallengeProvider;
        _rolePermissionQueryService = rolePermissionQueryService;
        _auditLogWriter = auditLogWriter;
        _dbContext = dbContext;
        _options = options.Value;
    }

    public async Task<Result<AdminLoginResponse>> LoginAsync(AdminLoginRequest request)
    {
        DateTime now = DateTime.UtcNow;

        var adminUser = await _adminUserRepository.GetByEmailAsync(request.Email);
        if (adminUser is null)
        {
            // Same error as a wrong password below - an unknown email must
            // not be distinguishable (enumeration risk, SRS 28.3), same
            // reasoning as CustomerLoginService.
            await RecordAttemptAsync(request.Email, "AdminLoginFailed");
            return Result.Failure<AdminLoginResponse>(InvalidCredentials);
        }

        var verification = _passwordHasher.VerifyHashedPassword(adminUser, adminUser.PasswordHash, request.Password);
        if (verification == PasswordVerificationResult.Failed)
        {
            adminUser.RegisterFailedLoginAttempt(
                _options.MaxFailedLoginAttempts, TimeSpan.FromMinutes(_options.LockoutMinutes), now);
            await _adminUserRepository.UpdateAsync(adminUser);
            await RecordAttemptAsync(adminUser.Id.ToString(), "AdminLoginFailed");
            return Result.Failure<AdminLoginResponse>(InvalidCredentials);
        }

        // Lockout/inactive detail is only ever revealed once the password has
        // already been confirmed correct - checking these before password
        // verification would let an unauthenticated caller distinguish a
        // locked or deactivated account from an unknown email or a wrong
        // password with a single request (SRS 28.3 enumeration risk), the
        // same reasoning CustomerLoginService.IssueSessionAsync's Status
        // check follows.
        if (adminUser.IsLockedOut(now))
        {
            await RecordAttemptAsync(adminUser.Id.ToString(), "AdminLoginFailedAccountLocked");
            return Result.Failure<AdminLoginResponse>(Error.Forbidden(
                "AdminLogin.AccountLocked",
                $"This account is locked until {adminUser.LockedUntilUtc:u} due to repeated failed logins."));
        }

        if (adminUser.Status != AdminUserStatus.Active)
        {
            await RecordAttemptAsync(adminUser.Id.ToString(), "AdminLoginFailedAccountInactive");
            return Result.Failure<AdminLoginResponse>(
                Error.Forbidden("AdminLogin.AccountNotActive", "This account cannot log in."));
        }

        var mfaResult = await _mfaChallengeProvider.VerifyAsync(adminUser);
        if (mfaResult.IsFailure)
        {
            await RecordAttemptAsync(adminUser.Id.ToString(), "AdminLoginFailedMfaChallenge");
            return Result.Failure<AdminLoginResponse>(mfaResult.Error);
        }

        adminUser.RegisterSuccessfulLogin(now);
        await _adminUserRepository.UpdateAsync(adminUser);
        await RecordAttemptAsync(adminUser.Id.ToString(), "AdminLoginSucceeded");

        return await IssueSessionAsync(adminUser);
    }

    public async Task<Result> UnlockAsync(Guid adminUserId)
    {
        var adminUser = await _adminUserRepository.GetByIdAsync(adminUserId);
        if (adminUser is null)
        {
            return Result.Failure(Error.NotFound("AdminLogin.NotFound", "No admin account was found with that id."));
        }

        adminUser.Unlock(DateTime.UtcNow);
        await _adminUserRepository.UpdateAsync(adminUser);
        await RecordAttemptAsync(adminUser.Id.ToString(), "AdminAccountUnlocked");

        return Result.Success();
    }

    public async Task<Result<AdminLoginResponse>> RefreshAsync(RefreshTokenRequest request)
    {
        var session = await _sessionRepository.GetByRefreshTokenHashAsync(Hash(request.RefreshToken));
        if (session is null || !session.IsActive(DateTime.UtcNow))
        {
            return Result.Failure<AdminLoginResponse>(Error.Unauthorized("AdminLogin.InvalidRefreshToken", "The refresh token is invalid or has expired."));
        }

        var adminUser = await _adminUserRepository.GetByIdAsync(session.AdminUserId);
        if (adminUser is null)
        {
            return Result.Failure<AdminLoginResponse>(Error.Unauthorized("AdminLogin.InvalidRefreshToken", "The refresh token is invalid or has expired."));
        }

        // Rotate on every use: the old refresh token is revoked immediately so
        // it cannot be replayed if it was intercepted (SRS 28.3).
        session.Revoke();
        await _sessionRepository.UpdateAsync(session);

        return await IssueSessionAsync(adminUser, session.DeviceInfo, session.IpAddress);
    }

    public async Task<Result> LogoutAsync(LogoutRequest request)
    {
        var session = await _sessionRepository.GetByRefreshTokenHashAsync(Hash(request.RefreshToken));
        if (session is null)
        {
            // Logging out an already-invalid token is not an error from the
            // caller's point of view: the end state (no active session) holds.
            return Result.Success();
        }

        session.Revoke();
        await _sessionRepository.UpdateAsync(session);
        return Result.Success();
    }

    private async Task<Result<AdminLoginResponse>> IssueSessionAsync(AdminUser adminUser, string? deviceInfo = null, string? ipAddress = null)
    {
        // Mirrors CustomerLoginService.IssueSessionAsync's Status check: this
        // runs on every refresh too, not just the initial login, so an admin
        // deactivated mid-session cannot keep extending it by rotating an
        // already-issued refresh token.
        if (adminUser.Status != AdminUserStatus.Active)
        {
            return Result.Failure<AdminLoginResponse>(Error.Forbidden("AdminLogin.AccountNotActive", "This account cannot log in."));
        }

        AdminRolePermissions rolePermissions = await _rolePermissionQueryService.GetPermissionsAsync(adminUser.RoleId);
        var accessToken = _tokenService.GenerateAccessToken(
            adminUser.Id, adminUser.Email, rolePermissions.RoleName, rolePermissions.PermissionCodes);
        var refreshToken = _tokenService.GenerateRefreshToken();
        var now = DateTime.UtcNow;

        var session = new AdminSession(
            Guid.NewGuid(), adminUser.Id, Hash(refreshToken), now, now.Add(_tokenService.RefreshTokenLifetime),
            deviceInfo, ipAddress);
        await _sessionRepository.AddAsync(session);

        return Result.Success(new AdminLoginResponse(accessToken.Value, accessToken.ExpiresAtUtc, refreshToken));
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Login audit (task 95g). <see cref="IAuditContextProvider"/> resolves
    /// <see cref="AuditActorType.Anonymous"/> for every entry written here —
    /// the caller has no bearer token yet at login time, which is a correct
    /// read of who made the HTTP request, not a bug. The account actually
    /// being attempted is instead captured in <paramref name="entityId"/>
    /// (the admin user id once known, or the raw attempted email when it
    /// never resolves to one), which is what makes the row attributable to a
    /// specific account rather than just "someone, from somewhere".
    /// </summary>
    private async Task RecordAttemptAsync(string entityId, string action)
    {
        await _auditLogWriter.WriteAsync(new AuditEntry("AdminUser", entityId, action));
        await _dbContext.SaveChangesAsync();
    }
}
