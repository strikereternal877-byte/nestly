using FluentValidation;

namespace Nestly.Application.ProviderIdentity;

public class RequestProviderLoginOtpRequestValidator : AbstractValidator<RequestProviderLoginOtpRequest>
{
    public RequestProviderLoginOtpRequestValidator()
    {
        RuleFor(x => x.Mobile)
            .NotEmpty().WithMessage("Mobile number is required")
            .Matches(@"^\+?[1-9]\d{7,14}$").WithMessage("Mobile number must be a valid phone number");
    }
}

public class LoginProviderWithOtpRequestValidator : AbstractValidator<LoginProviderWithOtpRequest>
{
    public LoginProviderWithOtpRequestValidator()
    {
        RuleFor(x => x.Mobile)
            .NotEmpty().WithMessage("Mobile number is required")
            .Matches(@"^\+?[1-9]\d{7,14}$").WithMessage("Mobile number must be a valid phone number");

        RuleFor(x => x.OtpCode)
            .NotEmpty().WithMessage("OTP code is required")
            .Matches(@"^\d{6}$").WithMessage("OTP code must be 6 digits");
    }
}

public class RefreshProviderTokenRequestValidator : AbstractValidator<RefreshProviderTokenRequest>
{
    public RefreshProviderTokenRequestValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty();
    }
}

public class LogoutProviderRequestValidator : AbstractValidator<LogoutProviderRequest>
{
    public LogoutProviderRequestValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty();
    }
}
