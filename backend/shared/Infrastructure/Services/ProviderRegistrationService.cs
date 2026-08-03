using Nestly.Application;
using Nestly.Application.ProviderIdentity;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Provider registration orchestration (task 146a), structurally mirroring
/// <see cref="CustomerRegistrationService"/>. No welcome-notification trigger
/// here (unlike the customer flow): <c>NotificationEvent.CustomerId</c> has a
/// real foreign key to the customer table, so it cannot record a provider
/// actor without a schema change - out of scope for this pass.
/// </summary>
public class ProviderRegistrationService : IProviderRegistrationService
{
    private readonly IProviderRepository _providerRepository;
    private readonly IProviderAuthIdentityRepository _authIdentityRepository;
    private readonly IProviderOtpService _otpService;

    public ProviderRegistrationService(
        IProviderRepository providerRepository,
        IProviderAuthIdentityRepository authIdentityRepository,
        IProviderOtpService otpService)
    {
        _providerRepository = providerRepository;
        _authIdentityRepository = authIdentityRepository;
        _otpService = otpService;
    }

    public async Task<Result> RequestOtpAsync(RequestProviderRegistrationOtpRequest request)
    {
        if (await _providerRepository.ExistsByPhoneAsync(request.Mobile))
        {
            return Result.Failure(Error.Conflict("ProviderRegistration.MobileAlreadyRegistered",
                "A provider with this mobile number already exists."));
        }

        return await _otpService.GenerateAsync(request.Mobile, OtpPurpose.Registration);
    }

    public async Task<Result<ProviderSummaryResponse>> RegisterAsync(RegisterProviderRequest request)
    {
        if (!request.ConsentAccepted)
        {
            return Result.Failure<ProviderSummaryResponse>(Error.Validation(
                "ProviderRegistration.ConsentRequired", "Consent to Terms & Privacy is required."));
        }

        var otpResult = await _otpService.ValidateAsync(request.Mobile, request.OtpCode, OtpPurpose.Registration);
        if (otpResult.IsFailure)
        {
            return Result.Failure<ProviderSummaryResponse>(otpResult.Error);
        }

        if (await _providerRepository.ExistsByPhoneAsync(request.Mobile))
        {
            return Result.Failure<ProviderSummaryResponse>(Error.Conflict(
                "ProviderRegistration.MobileAlreadyRegistered", "A provider with this mobile number already exists."));
        }

        // OTP proved mobile ownership only, not KYC - Provider's constructor
        // starts the account PendingVerification, not Active (OPEN DECISIONS
        // #2 in PROVIDER.md constrains this to Individual for v1).
        var provider = new Provider(
            Guid.NewGuid(), request.LegalName, request.DisplayName, ProviderType.Individual, request.Mobile, request.Email);
        await _providerRepository.AddAsync(provider);

        var mobileIdentity = new ProviderAuthIdentity(
            Guid.NewGuid(), provider.Id, AuthProviderType.MobileOtp, request.Mobile, isPrimary: true);
        await _authIdentityRepository.AddAsync(mobileIdentity);

        return Result.Success(new ProviderSummaryResponse(
            provider.Id, provider.LegalName, provider.DisplayName, provider.Phone, provider.Email,
            provider.Status.ToString(), provider.OnboardingStatus.ToString()));
    }
}
