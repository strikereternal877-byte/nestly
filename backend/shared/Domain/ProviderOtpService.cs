using Nestly.BuildingBlocks.Results;

namespace Nestly.Domain;

/// <summary>
/// Provider OTP generation/validation. Structural mirror of
/// <see cref="IOTPService"/> (same signatures, same reasoning for returning
/// <see cref="Result"/>), but implemented against <see cref="ProviderOtp"/>
/// instead of <see cref="CustomerOtp"/> - see <see cref="ProviderOtp"/>'s doc
/// comment for why this module keeps its own OTP table/service rather than
/// sharing the customer one.
/// </summary>
public interface IProviderOtpService
{
    Task<Result> GenerateAsync(string target, OtpPurpose purpose, NotificationChannel channel = NotificationChannel.Sms);
    Task<Result> ValidateAsync(string target, string otpCode, OtpPurpose purpose);
}
