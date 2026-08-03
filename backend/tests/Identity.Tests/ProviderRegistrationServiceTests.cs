using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Nestly.Application.ProviderIdentity;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Identity.Tests;

/// <summary>
/// Provider registration (task 146a), structurally mirroring how
/// <c>CustomerRegistrationService</c> is exercised - OTP mocked so these
/// tests focus purely on registration's own rules (duplicate mobile,
/// consent, the resulting entity's starting state).
/// </summary>
public class ProviderRegistrationServiceTests : IDisposable
{
    private const string Mobile = "+919876543210";
    private const string OtpCode = "123456";

    private readonly TestDatabase _database = new();
    private readonly Mock<IProviderOtpService> _otpService = new();

    public ProviderRegistrationServiceTests()
    {
        _otpService
            .Setup(o => o.GenerateAsync(It.IsAny<string>(), OtpPurpose.Registration, It.IsAny<NotificationChannel>()))
            .ReturnsAsync(Result.Success());
        _otpService
            .Setup(o => o.ValidateAsync(Mobile, OtpCode, OtpPurpose.Registration))
            .ReturnsAsync(Result.Success());
    }

    private ProviderRegistrationService CreateService(NestlyDbContext context) =>
        new(new ProviderRepository(context), new ProviderAuthIdentityRepository(context), _otpService.Object);

    private static RegisterProviderRequest ValidRequest(string mobile = Mobile) => new(
        mobile, OtpCode, "Ravi Kumar", "Ravi's Repairs", "ravi@example.com", ConsentAccepted: true);

    [Fact]
    public async Task RegisterAsync_creates_a_provider_pending_verification_with_a_registered_onboarding_status()
    {
        await using var context = _database.CreateContext();
        var result = await CreateService(context).RegisterAsync(ValidRequest());

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(ProviderStatus.PendingVerification));
        result.Value.OnboardingStatus.Should().Be(nameof(ProviderOnboardingStatus.Registered));

        var stored = await context.Set<Provider>().SingleAsync();
        stored.ProviderType.Should().Be(ProviderType.Individual);
        stored.Phone.Should().Be(Mobile);
    }

    [Fact]
    public async Task RegisterAsync_creates_a_mobile_otp_auth_identity_for_the_new_provider()
    {
        await using var context = _database.CreateContext();
        var result = await CreateService(context).RegisterAsync(ValidRequest());

        var identity = await context.Set<ProviderAuthIdentity>().SingleAsync();
        identity.ProviderId.Should().Be(result.Value.Id);
        identity.Provider.Should().Be(AuthProviderType.MobileOtp);
        identity.Identifier.Should().Be(Mobile);
        identity.IsPrimary.Should().BeTrue();
    }

    [Fact]
    public async Task RegisterAsync_rejects_a_mobile_number_that_is_already_registered()
    {
        await using var context = _database.CreateContext();
        var service = CreateService(context);

        (await service.RegisterAsync(ValidRequest())).IsSuccess.Should().BeTrue();

        var second = await service.RegisterAsync(ValidRequest());
        second.IsFailure.Should().BeTrue();
        second.Error.Code.Should().Be("ProviderRegistration.MobileAlreadyRegistered");
    }

    [Fact]
    public async Task RegisterAsync_rejects_registration_without_consent()
    {
        await using var context = _database.CreateContext();
        var request = ValidRequest() with { ConsentAccepted = false };

        var result = await CreateService(context).RegisterAsync(request);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderRegistration.ConsentRequired");
        (await context.Set<Provider>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RegisterAsync_propagates_an_otp_validation_failure()
    {
        _otpService
            .Setup(o => o.ValidateAsync(Mobile, "000000", OtpPurpose.Registration))
            .ReturnsAsync(Result.Failure(Error.Validation("ProviderOtp.Incorrect", "The OTP code is incorrect.")));

        await using var context = _database.CreateContext();
        var result = await CreateService(context).RegisterAsync(ValidRequest() with { OtpCode = "000000" });

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderOtp.Incorrect");
        (await context.Set<Provider>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RequestOtpAsync_refuses_a_mobile_number_already_registered_as_a_provider()
    {
        await using var context = _database.CreateContext();
        var service = CreateService(context);
        await service.RegisterAsync(ValidRequest());

        var result = await service.RequestOtpAsync(new RequestProviderRegistrationOtpRequest(Mobile));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderRegistration.MobileAlreadyRegistered");
    }

    public void Dispose() => _database.Dispose();
}
