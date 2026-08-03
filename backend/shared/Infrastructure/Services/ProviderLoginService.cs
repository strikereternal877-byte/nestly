using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.ProviderIdentity;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Provider OTP login, session issuance, refresh, and logout (task 146b),
/// structurally mirroring <see cref="CustomerLoginService"/>. No password
/// login path - PROVIDER.md's API surface lists only OTP for providers.
///
/// Unlike <see cref="CustomerLoginService.IssueSessionAsync"/> (which only
/// allows <c>CustomerStatus.Active</c>), a session is issued for both
/// <see cref="ProviderStatus.PendingVerification"/> and
/// <see cref="ProviderStatus.Active"/>: a newly registered provider has not
/// been through admin KYC approval yet, but still needs to log back in to
/// finish onboarding (upload KYC documents, complete profile). Only
/// <see cref="ProviderStatus.Suspended"/>/<see cref="ProviderStatus.Deactivated"/>
/// are blocked.
/// </summary>
public class ProviderLoginService : IProviderLoginService
{
    private readonly IProviderRepository _providerRepository;
    private readonly IProviderSessionRepository _sessionRepository;
    private readonly IProviderLoginAttemptRepository _loginAttemptRepository;
    private readonly IProviderOtpService _otpService;
    private readonly IProviderTokenService _tokenService;
    private readonly ProviderAccountOptions _accountOptions;

    public ProviderLoginService(
        IProviderRepository providerRepository,
        IProviderSessionRepository sessionRepository,
        IProviderLoginAttemptRepository loginAttemptRepository,
        IProviderOtpService otpService,
        IProviderTokenService tokenService,
        IOptions<ProviderAccountOptions> accountOptions)
    {
        _providerRepository = providerRepository;
        _sessionRepository = sessionRepository;
        _loginAttemptRepository = loginAttemptRepository;
        _otpService = otpService;
        _tokenService = tokenService;
        _accountOptions = accountOptions.Value;
    }

    public async Task<Result> RequestOtpAsync(RequestProviderLoginOtpRequest request)
    {
        var provider = await _providerRepository.GetByPhoneAsync(request.Mobile);
        if (provider is null)
        {
            // Same message either way avoids confirming/denying whether a
            // mobile number is registered (mirrors CustomerLoginService).
            return Result.Failure(Error.NotFound("ProviderLogin.NotFound", "No account found for this mobile number."));
        }

        return await _otpService.GenerateAsync(request.Mobile, OtpPurpose.Login);
    }

    public async Task<Result<ProviderLoginResponse>> LoginWithOtpAsync(LoginProviderWithOtpRequest request)
    {
        var lockout = await CheckLockoutAsync(request.Mobile);
        if (lockout is not null)
        {
            return Result.Failure<ProviderLoginResponse>(lockout);
        }

        var otpResult = await _otpService.ValidateAsync(request.Mobile, request.OtpCode, OtpPurpose.Login);
        if (otpResult.IsFailure)
        {
            await RecordAttemptAsync(request.Mobile, succeeded: false);
            return Result.Failure<ProviderLoginResponse>(otpResult.Error);
        }

        var provider = await _providerRepository.GetByPhoneAsync(request.Mobile);
        if (provider is null)
        {
            await RecordAttemptAsync(request.Mobile, succeeded: false);
            return Result.Failure<ProviderLoginResponse>(Error.NotFound("ProviderLogin.NotFound", "No account found for this mobile number."));
        }

        await RecordAttemptAsync(request.Mobile, succeeded: true);
        return await IssueSessionAsync(provider);
    }

    public async Task<Result<ProviderLoginResponse>> RefreshAsync(RefreshProviderTokenRequest request)
    {
        var session = await _sessionRepository.GetByRefreshTokenHashAsync(Hash(request.RefreshToken));
        if (session is null || !session.IsActive(DateTime.UtcNow))
        {
            return Result.Failure<ProviderLoginResponse>(Error.Unauthorized("ProviderLogin.InvalidRefreshToken", "The refresh token is invalid or has expired."));
        }

        var provider = await _providerRepository.GetByIdAsync(session.ProviderId);
        if (provider is null)
        {
            return Result.Failure<ProviderLoginResponse>(Error.Unauthorized("ProviderLogin.InvalidRefreshToken", "The refresh token is invalid or has expired."));
        }

        // Rotate on every use: the old refresh token is revoked immediately
        // so it cannot be replayed if intercepted (mirrors CustomerLoginService).
        session.Revoke();
        await _sessionRepository.UpdateAsync(session);

        return await IssueSessionAsync(provider, session.DeviceInfo, session.IpAddress);
    }

    public async Task<Result> LogoutAsync(LogoutProviderRequest request)
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

    private async Task<Result<ProviderLoginResponse>> IssueSessionAsync(Provider provider, string? deviceInfo = null, string? ipAddress = null)
    {
        if (provider.Status is ProviderStatus.Suspended or ProviderStatus.Deactivated)
        {
            return Result.Failure<ProviderLoginResponse>(Error.Forbidden("ProviderLogin.AccountNotActive", "This account cannot log in."));
        }

        var accessToken = _tokenService.GenerateAccessToken(provider.Id, provider.Phone);
        var refreshToken = _tokenService.GenerateRefreshToken();
        var now = DateTime.UtcNow;

        var session = new ProviderSession(
            Guid.NewGuid(), provider.Id, Hash(refreshToken), now, now.Add(_tokenService.RefreshTokenLifetime),
            deviceInfo, ipAddress);
        await _sessionRepository.AddAsync(session);

        return Result.Success(new ProviderLoginResponse(accessToken.Value, accessToken.ExpiresAtUtc, refreshToken));
    }

    private async Task<Error?> CheckLockoutAsync(string identifier)
    {
        var since = DateTime.UtcNow.AddMinutes(-_accountOptions.LockoutWindowMinutes);
        int failures = await _loginAttemptRepository.CountFailuresSinceAsync(identifier, since);
        if (failures >= _accountOptions.MaxFailedLoginAttempts)
        {
            return Error.Forbidden("ProviderLogin.AccountLocked",
                $"Too many failed attempts. Try again in {_accountOptions.LockoutWindowMinutes} minutes.");
        }

        return null;
    }

    private Task RecordAttemptAsync(string identifier, bool succeeded) =>
        _loginAttemptRepository.AddAsync(new ProviderLoginAttempt(Guid.NewGuid(), identifier, succeeded, DateTime.UtcNow));

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
