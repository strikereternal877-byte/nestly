using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Provider OTP generation/validation. Structural copy of <see cref="OtpService"/>
/// (same expiry/cooldown/retry-limit rules) operating on <see cref="ProviderOtp"/>
/// instead of <see cref="CustomerOtp"/> - see <see cref="ProviderOtp"/>'s doc
/// comment for why this is a separate table/service rather than a shared one.
/// </summary>
public class ProviderOtpService : IProviderOtpService
{
    private static readonly TimeSpan Expiry = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(30);
    private const int MaxAttempts = 5;

    private readonly NestlyDbContext _context;
    private readonly INotificationProvider _notificationProvider;

    public ProviderOtpService(NestlyDbContext context, INotificationProvider notificationProvider)
    {
        _context = context;
        _notificationProvider = notificationProvider;
    }

    public async Task<Result> GenerateAsync(string target, OtpPurpose purpose, NotificationChannel channel = NotificationChannel.Sms)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return Result.Failure(Error.Validation("ProviderOtp.InvalidTarget", "A delivery target is required."));
        }

        var now = DateTime.UtcNow;

        bool requestedRecently = await _context.Set<ProviderOtp>()
            .AnyAsync(o => o.Target == target && o.Purpose == purpose && o.CreatedAt > now.Subtract(ResendCooldown));
        if (requestedRecently)
        {
            return Result.Failure(Error.Business("ProviderOtp.TooManyRequests",
                "Please wait before requesting another OTP."));
        }

        string code = GenerateNumericCode(6);
        var otp = new ProviderOtp(Guid.NewGuid(), providerId: null, target, purpose,
            Hash(code), now.Add(Expiry));

        await _context.Set<ProviderOtp>().AddAsync(otp);
        await _context.SaveChangesAsync();

        // The plaintext code only ever exists in memory here and on the
        // recipient's device; it is never persisted or logged (same
        // no-secrets-in-logs rule OtpService follows).
        string message = $"Your Nestly provider verification code is {code}";
        var sendResult = channel switch
        {
            NotificationChannel.Email => await _notificationProvider.SendEmailAsync(target, "Your Nestly provider verification code", message),
            _ => await _notificationProvider.SendSmsAsync(target, message)
        };

        if (sendResult.IsFailure)
        {
            return sendResult;
        }

        return Result.Success();
    }

    public async Task<Result> ValidateAsync(string target, string otpCode, OtpPurpose purpose)
    {
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(otpCode))
        {
            return Result.Failure(Error.Validation("ProviderOtp.InvalidInput", "A delivery target and OTP code are required."));
        }

        var otp = await _context.Set<ProviderOtp>()
            .Where(o => o.Target == target && o.Purpose == purpose && o.ConsumedAt == null)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync();

        if (otp is null)
        {
            return Result.Failure(Error.NotFound("ProviderOtp.NotFound", "No pending OTP for this request."));
        }

        if (otp.AttemptCount >= MaxAttempts)
        {
            return Result.Failure(Error.Business("ProviderOtp.RetryLimitExceeded", "Too many incorrect attempts."));
        }

        if (otp.IsExpired(DateTime.UtcNow))
        {
            return Result.Failure(Error.Business("ProviderOtp.Expired", "This OTP has expired."));
        }

        otp.RecordAttempt();

        if (otp.CodeHash != Hash(otpCode))
        {
            await _context.SaveChangesAsync();
            return Result.Failure(Error.Validation("ProviderOtp.Incorrect", "The OTP code is incorrect."));
        }

        otp.MarkConsumed();
        await _context.SaveChangesAsync();
        return Result.Success();
    }

    private static string GenerateNumericCode(int digits)
    {
        var builder = new StringBuilder(digits);
        for (int i = 0; i < digits; i++)
        {
            builder.Append(RandomNumberGenerator.GetInt32(0, 10));
        }
        return builder.ToString();
    }

    private static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}
