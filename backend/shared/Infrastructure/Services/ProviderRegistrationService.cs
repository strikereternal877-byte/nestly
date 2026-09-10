using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.ProviderIdentity;
using Nestly.Application.ProviderReferral;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Provider registration orchestration (task 146a), structurally mirroring
/// <see cref="CustomerRegistrationService"/>. Task 372 added the same
/// optional email+password capture the customer flow already had. No
/// welcome-notification trigger here (unlike the customer flow):
/// <c>NotificationEvent.CustomerId</c> has a real foreign key to the
/// customer table, so it cannot record a provider actor without a schema
/// change - out of scope for this pass. The same constraint means
/// <see cref="TryCreateProviderReferralAsync"/> below (PROVIDER-REFERRAL.md)
/// never dispatches a "your invite was used" notification either, unlike
/// <c>CustomerRegistrationService.TryCreateReferralAsync</c> - the referrer
/// sees a new invite the same way they see everything else about their
/// referral program, via <c>GET /me/referral/history</c>.
/// </summary>
public class ProviderRegistrationService : IProviderRegistrationService
{
    private readonly IProviderRepository _providerRepository;
    private readonly IProviderAuthIdentityRepository _authIdentityRepository;
    private readonly IProviderOtpService _otpService;
    private readonly IProviderReferralRepository _referralRepository;
    private readonly IProviderReferralProgramConfigRepository _referralProgramConfigRepository;
    private readonly IProviderAvailabilityWindowRepository _availabilityWindowRepository;
    private readonly ILogger<ProviderRegistrationService> _logger;
    private readonly ProviderAccountOptions _options;
    private readonly PasswordHasher<Provider> _passwordHasher = new();

    public ProviderRegistrationService(
        IProviderRepository providerRepository,
        IProviderAuthIdentityRepository authIdentityRepository,
        IProviderOtpService otpService,
        IProviderReferralRepository referralRepository,
        IProviderReferralProgramConfigRepository referralProgramConfigRepository,
        IProviderAvailabilityWindowRepository availabilityWindowRepository,
        ILogger<ProviderRegistrationService> logger,
        IOptions<ProviderAccountOptions> options)
    {
        _providerRepository = providerRepository;
        _authIdentityRepository = authIdentityRepository;
        _otpService = otpService;
        _referralRepository = referralRepository;
        _referralProgramConfigRepository = referralProgramConfigRepository;
        _availabilityWindowRepository = availabilityWindowRepository;
        _logger = logger;
        _options = options.Value;
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

        if (!string.IsNullOrEmpty(request.Password))
        {
            if (!_options.PasswordAuthEnabled)
            {
                return Result.Failure<ProviderSummaryResponse>(Error.Validation(
                    "ProviderRegistration.PasswordAuthDisabled", "Password-based authentication is not enabled."));
            }

            if (string.IsNullOrWhiteSpace(request.Email))
            {
                return Result.Failure<ProviderSummaryResponse>(Error.Validation(
                    "ProviderRegistration.EmailRequiredForPassword", "Email is required when setting a password."));
            }
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

        if (!string.IsNullOrWhiteSpace(request.Email) && _options.RequireUniqueEmail &&
            await _providerRepository.ExistsByEmailAsync(request.Email))
        {
            return Result.Failure<ProviderSummaryResponse>(Error.Conflict(
                "ProviderRegistration.EmailAlreadyRegistered", "A provider with this email already exists."));
        }

        // OTP proved mobile ownership only, not KYC - Provider's constructor
        // starts the account PendingVerification, not Active (OPEN DECISIONS
        // #2 in PROVIDER.md constrains this to Individual for v1).
        var provider = new Provider(
            Guid.NewGuid(), request.LegalName, request.DisplayName, ProviderType.Individual, request.Mobile, request.Email);
        await _providerRepository.AddAsync(provider);

        // Row 31, docs/OPEN-FIXES-FEATURES.csv: without this the provider is
        // invisible to matching until they set availability by hand.
        await _availabilityWindowRepository.ReplaceForProviderAsync(
            provider.Id, ProviderAvailabilityWindow.DefaultWeeklySchedule(provider.Id));

        var mobileIdentity = new ProviderAuthIdentity(
            Guid.NewGuid(), provider.Id, AuthProviderType.MobileOtp, request.Mobile, isPrimary: true);
        await _authIdentityRepository.AddAsync(mobileIdentity);

        if (!string.IsNullOrEmpty(request.Password) && !string.IsNullOrWhiteSpace(request.Email))
        {
            var emailIdentity = new ProviderAuthIdentity(
                Guid.NewGuid(), provider.Id, AuthProviderType.EmailPassword, request.Email, isPrimary: false);
            emailIdentity.SetPasswordHash(_passwordHasher.HashPassword(provider, request.Password));
            await _authIdentityRepository.AddAsync(emailIdentity);
        }

        if (!string.IsNullOrWhiteSpace(request.ReferralCode))
        {
            try
            {
                await TryCreateProviderReferralAsync(provider, request.ReferralCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process provider referral code {ReferralCode} for provider {ProviderId}.", request.ReferralCode, provider.Id);
            }
        }

        return Result.Success(new ProviderSummaryResponse(
            provider.Id, provider.LegalName, provider.DisplayName, provider.Phone, provider.Email,
            provider.Status.ToString(), provider.OnboardingStatus.ToString()));
    }

    public async Task<Result> RequestEmailOtpAsync(RequestProviderRegistrationEmailOtpRequest request)
    {
        if (!_options.PasswordAuthEnabled)
        {
            return Result.Failure(Error.Validation(
                "ProviderRegistration.PasswordAuthDisabled", "Password-based authentication is not enabled."));
        }

        if (_options.RequireUniqueEmail && await _providerRepository.ExistsByEmailAsync(request.Email))
        {
            return Result.Failure(Error.Conflict("ProviderRegistration.EmailAlreadyRegistered",
                "A provider with this email already exists."));
        }

        return await _otpService.GenerateAsync(request.Email, OtpPurpose.Registration, NotificationChannel.Email);
    }

    public async Task<Result<ProviderSummaryResponse>> RegisterWithEmailAsync(RegisterProviderWithEmailRequest request)
    {
        if (!request.ConsentAccepted)
        {
            return Result.Failure<ProviderSummaryResponse>(Error.Validation(
                "ProviderRegistration.ConsentRequired", "Consent to Terms & Privacy is required."));
        }

        if (!_options.PasswordAuthEnabled)
        {
            return Result.Failure<ProviderSummaryResponse>(Error.Validation(
                "ProviderRegistration.PasswordAuthDisabled", "Password-based authentication is not enabled."));
        }

        var otpResult = await _otpService.ValidateAsync(request.Email, request.OtpCode, OtpPurpose.Registration);
        if (otpResult.IsFailure)
        {
            return Result.Failure<ProviderSummaryResponse>(otpResult.Error);
        }

        if (await _providerRepository.ExistsByPhoneAsync(request.Mobile))
        {
            return Result.Failure<ProviderSummaryResponse>(Error.Conflict(
                "ProviderRegistration.MobileAlreadyRegistered", "A provider with this mobile number already exists."));
        }

        if (_options.RequireUniqueEmail && await _providerRepository.ExistsByEmailAsync(request.Email))
        {
            return Result.Failure<ProviderSummaryResponse>(Error.Conflict(
                "ProviderRegistration.EmailAlreadyRegistered", "A provider with this email already exists."));
        }

        // OTP proved email ownership only, not KYC - same PendingVerification
        // start state as the mobile-OTP path.
        var provider = new Provider(
            Guid.NewGuid(), request.LegalName, request.DisplayName, ProviderType.Individual, request.Mobile, request.Email);
        await _providerRepository.AddAsync(provider);

        // Row 31, docs/OPEN-FIXES-FEATURES.csv: without this the provider is
        // invisible to matching until they set availability by hand.
        await _availabilityWindowRepository.ReplaceForProviderAsync(
            provider.Id, ProviderAvailabilityWindow.DefaultWeeklySchedule(provider.Id));

        // Email+password is the verified identity here - mobile was never
        // proven via OTP on this path, so no MobileOtp identity is created.
        var emailIdentity = new ProviderAuthIdentity(
            Guid.NewGuid(), provider.Id, AuthProviderType.EmailPassword, request.Email, isPrimary: true);
        emailIdentity.SetPasswordHash(_passwordHasher.HashPassword(provider, request.Password));
        await _authIdentityRepository.AddAsync(emailIdentity);

        if (!string.IsNullOrWhiteSpace(request.ReferralCode))
        {
            try
            {
                await TryCreateProviderReferralAsync(provider, request.ReferralCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process provider referral code {ReferralCode} for provider {ProviderId}.", request.ReferralCode, provider.Id);
            }
        }

        return Result.Success(new ProviderSummaryResponse(
            provider.Id, provider.LegalName, provider.DisplayName, provider.Phone, provider.Email,
            provider.Status.ToString(), provider.OnboardingStatus.ToString()));
    }

    /// <summary>
    /// Best-effort - an invalid or self-referential code never fails
    /// registration itself, mirrors
    /// <c>CustomerRegistrationService.TryCreateReferralAsync</c>.
    /// </summary>
    private async Task TryCreateProviderReferralAsync(Provider referee, string referralCode)
    {
        Provider? referrer = await _providerRepository.GetByReferralCodeAsync(referralCode);
        if (referrer is null)
        {
            _logger.LogInformation("Provider registration referral code {ReferralCode} did not match any provider.", referralCode);
            return;
        }

        // Self-referral block by phone/email match - the referee is a
        // brand-new account, so id equality can never trigger; the real risk
        // is the same person's second account using their own code.
        bool samePhone = referrer.Phone == referee.Phone;
        bool sameEmail = referrer.Email is not null && referee.Email is not null && referrer.Email == referee.Email;
        if (samePhone || sameEmail)
        {
            _logger.LogWarning(
                "Provider registration blocked a self-referral attempt: provider {ProviderId} tried to use their own referral code.",
                referrer.Id);
            return;
        }

        // One referral per referee, ever - the unique index on
        // referee_provider_id is the real backstop; this check exists so a
        // stale/duplicate submission gets a clean no-op.
        if (await _referralRepository.GetByRefereeProviderIdAsync(referee.Id) is not null)
        {
            return;
        }

        ProviderReferralProgramConfig? config = await _referralProgramConfigRepository.GetAsync();
        if (config is null || !config.IsActive)
        {
            _logger.LogInformation("Provider registration referral code {ReferralCode} ignored: provider referral program is not active.", referralCode);
            return;
        }

        var referral = new Domain.ProviderReferral(Guid.NewGuid(), referrer.Id, referee.Id, referralCode, config);
        await _referralRepository.AddAsync(referral);
    }
}
