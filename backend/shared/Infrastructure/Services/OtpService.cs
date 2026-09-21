using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// OTP generation/validation (SRS 11.2.1, 28.1): expiring, hashed, single-use
/// codes with an attempt limit and a cooldown against rapid re-requests.
/// </summary>
public class OtpService : IOTPService
{
    private static readonly TimeSpan Expiry = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(30);
    private const int MaxAttempts = 5;

    // Only ever checked when OtpOptions.AllowDevBypass is true - see that
    // property's doc comment for why this can't reach a deployed environment.
    private const string DevBypassCode = "000000";

    private readonly NestlyDbContext _context;
    private readonly INotificationProvider _notificationProvider;
    private readonly OtpOptions _options;

    public OtpService(NestlyDbContext context, INotificationProvider notificationProvider, IOptions<OtpOptions> options)
    {
        _context = context;
        _notificationProvider = notificationProvider;
        _options = options.Value;
    }

    public async Task<Result> GenerateAsync(string target, OtpPurpose purpose, NotificationChannel channel = NotificationChannel.Sms)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return Result.Failure(Error.Validation("Otp.InvalidTarget", "A delivery target is required."));
        }

        var now = DateTime.UtcNow;

        bool requestedRecently = await _context.Set<CustomerOtp>()
            .AnyAsync(o => o.Target == target && o.Purpose == purpose && o.CreatedAt > now.Subtract(ResendCooldown));
        if (requestedRecently)
        {
            return Result.Failure(Error.Business("Otp.TooManyRequests",
                "Please wait before requesting another OTP."));
        }

        string code = GenerateNumericCode(6);
        var otp = new CustomerOtp(Guid.NewGuid(), customerId: null, target, purpose,
            Hash(code), now.Add(Expiry));

        await _context.Set<CustomerOtp>().AddAsync(otp);
        await _context.SaveChangesAsync();

        // The plaintext code only ever exists in memory here and on the
        // recipient's device; it is never persisted or logged, matching the
        // no-PII/no-secrets logging rule (the sandbox provider follows suit).
        string message = $"Your Glavyx verification code is {code}";
        var sendResult = channel switch
        {
            NotificationChannel.Email => await _notificationProvider.SendEmailAsync(target, "Your Glavyx verification code", message),
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
            return Result.Failure(Error.Validation("Otp.InvalidInput", "A delivery target and OTP code are required."));
        }

        var otp = await _context.Set<CustomerOtp>()
            .Where(o => o.Target == target && o.Purpose == purpose && o.ConsumedAt == null)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync();

        if (otp is null)
        {
            return Result.Failure(Error.NotFound("Otp.NotFound", "No pending OTP for this request."));
        }

        if (otp.AttemptCount >= MaxAttempts)
        {
            return Result.Failure(Error.Business("Otp.RetryLimitExceeded", "Too many incorrect attempts."));
        }

        if (otp.IsExpired(DateTime.UtcNow))
        {
            return Result.Failure(Error.Business("Otp.Expired", "This OTP has expired."));
        }

        otp.RecordAttempt();

        bool isDevBypass = _options.AllowDevBypass && otpCode == DevBypassCode;
        if (!isDevBypass && otp.CodeHash != Hash(otpCode))
        {
            await _context.SaveChangesAsync();
            return Result.Failure(Error.Validation("Otp.Incorrect", "The OTP code is incorrect."));
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

    // HMAC-SHA256 keyed with a server-side pepper (NESTLY-005): unlike the
    // plain SHA256 this replaced, a stolen CodeHash row cannot be reversed
    // via a precomputed table over all 1,000,000 six-digit codes without
    // also having _options.Pepper.
    private string Hash(string code) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(_options.Pepper), Encoding.UTF8.GetBytes(code)));
}
