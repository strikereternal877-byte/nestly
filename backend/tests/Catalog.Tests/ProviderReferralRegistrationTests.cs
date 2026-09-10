using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application.ProviderIdentity;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers PROVIDER-REFERRAL.md's registration wiring: referral code capture, self-referral block, one-referral-per-referee. Mirrors ReferralRegistrationTests.</summary>
public sealed class ProviderReferralRegistrationTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public ProviderReferralRegistrationTests(TestDatabase db) => _db = db;

    private sealed class OtpCapturingNotificationProvider : INotificationProvider
    {
        public string? LastSmsMessage { get; private set; }

        public Task<Result> SendSmsAsync(string toMobile, string message, CancellationToken cancellationToken = default)
        {
            LastSmsMessage = message;
            return Task.FromResult(Result.Success());
        }

        public Task<Result> SendEmailAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }

    private static ProviderRegistrationService BuildService(
        Nestly.Infrastructure.Persistence.NestlyDbContext context, ProviderOtpService otpService, ProviderAccountOptions? accountOptions = null) =>
        new(
            new ProviderRepository(context),
            new ProviderAuthIdentityRepository(context),
            otpService,
            new ProviderReferralRepository(context),
            new ProviderReferralProgramConfigRepository(context),
            new ProviderAvailabilityWindowRepository(context),
            NullLogger<ProviderRegistrationService>.Instance,
            Options.Create(accountOptions ?? new ProviderAccountOptions()));

    /// <summary>
    /// Always clears existing rows first (rather than "if Any() return"):
    /// this is a single-row table in production, and TestDatabase shares one
    /// database across every test method in this class, so a prior test that
    /// seeded an inactive config would otherwise leak into this one.
    /// </summary>
    private static void SeedActiveConfig(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        context.RemoveRange(context.ProviderReferralProgramConfigs);
        context.SaveChanges();
        context.Add(new ProviderReferralProgramConfig(
            Guid.NewGuid(), 500m, 500m, 3, 45, null, isActive: true));
        context.SaveChanges();
    }

    [Fact]
    public async Task RegisterAsync_creates_a_provider_referral_when_a_valid_referral_code_is_used()
    {
        using var context = _db.CreateContext();
        SeedActiveConfig(context);

        var referrer = new Provider(Guid.NewGuid(), "Referrer", "Referrer", ProviderType.Individual, "+9198" + Guid.NewGuid().ToString("N")[..8]);
        referrer.SetReferralCode("PREFCODE1");
        context.Add(referrer);
        context.SaveChanges();

        var otpProvider = new OtpCapturingNotificationProvider();
        var otpService = new ProviderOtpService(context, otpProvider, Options.Create(new OtpOptions { Pepper = "test-only-otp-pepper-not-for-production-abc123" }));
        var mobile = "+9198" + Guid.NewGuid().ToString("N")[..8];
        await otpService.GenerateAsync(mobile, OtpPurpose.Registration);
        string otpCode = System.Text.RegularExpressions.Regex.Match(otpProvider.LastSmsMessage!, @"\d{6}").Value;

        var service = BuildService(context, otpService);
        var result = await service.RegisterAsync(new RegisterProviderRequest(
            mobile, otpCode, "Referee", "Referee's Repairs", null, true, null, "PREFCODE1"));

        result.IsSuccess.Should().BeTrue();

        var referral = context.ProviderReferrals.Single(r => r.RefereeProviderId == result.Value.Id);
        referral.ReferrerProviderId.Should().Be(referrer.Id);
        referral.Status.Should().Be(ProviderReferralStatus.Registered);
        referral.ReferralCodeUsed.Should().Be("PREFCODE1");
    }

    [Fact]
    public async Task RegisterAsync_succeeds_but_skips_referral_creation_for_an_unknown_code()
    {
        using var context = _db.CreateContext();
        SeedActiveConfig(context);

        var otpProvider = new OtpCapturingNotificationProvider();
        var otpService = new ProviderOtpService(context, otpProvider, Options.Create(new OtpOptions { Pepper = "test-only-otp-pepper-not-for-production-abc123" }));
        var mobile = "+9198" + Guid.NewGuid().ToString("N")[..8];
        await otpService.GenerateAsync(mobile, OtpPurpose.Registration);
        string otpCode = System.Text.RegularExpressions.Regex.Match(otpProvider.LastSmsMessage!, @"\d{6}").Value;

        var service = BuildService(context, otpService);
        var result = await service.RegisterAsync(new RegisterProviderRequest(
            mobile, otpCode, "Referee", "Referee's Repairs", null, true, null, "DOES-NOT-EXIST"));

        result.IsSuccess.Should().BeTrue();
        context.ProviderReferrals.Any(r => r.RefereeProviderId == result.Value.Id).Should().BeFalse();
    }

    /// <summary>Mirrors ReferralRegistrationTests' equivalent test: phone is unconditionally DB-unique, so a genuine self-referral fails earlier on MobileAlreadyRegistered - this documents that finding rather than pretending the in-service guard is independently exercisable.</summary>
    [Fact]
    public async Task RegisterAsync_a_genuine_self_referral_attempt_fails_earlier_on_mobile_uniqueness()
    {
        using var context = _db.CreateContext();
        SeedActiveConfig(context);

        var otpProvider = new OtpCapturingNotificationProvider();
        var otpService = new ProviderOtpService(context, otpProvider, Options.Create(new OtpOptions { Pepper = "test-only-otp-pepper-not-for-production-abc123" }));
        var mobile = "+9198" + Guid.NewGuid().ToString("N")[..8];

        var referrer = new Provider(Guid.NewGuid(), "Self Referrer", "Self Referrer", ProviderType.Individual, mobile);
        referrer.SetReferralCode("SELFPCODE");
        context.Add(referrer);
        context.SaveChanges();

        await otpService.GenerateAsync(mobile, OtpPurpose.Registration);
        string otpCode = System.Text.RegularExpressions.Regex.Match(otpProvider.LastSmsMessage!, @"\d{6}").Value;

        var service = BuildService(context, otpService);
        var result = await service.RegisterAsync(new RegisterProviderRequest(
            mobile, otpCode, "Self Referrer", "Self Referrer", null, true, null, "SELFPCODE"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderRegistration.MobileAlreadyRegistered");
    }

    [Fact]
    public async Task RegisterAsync_ignores_a_referral_code_when_the_program_is_inactive()
    {
        using var context = _db.CreateContext();
        context.RemoveRange(context.ProviderReferralProgramConfigs);
        context.Add(new ProviderReferralProgramConfig(Guid.NewGuid(), 500m, 500m, 3, 45, null, isActive: false));
        context.SaveChanges();

        var referrer = new Provider(Guid.NewGuid(), "Referrer", "Referrer", ProviderType.Individual, "+9198" + Guid.NewGuid().ToString("N")[..8]);
        referrer.SetReferralCode("INACTIVECODE");
        context.Add(referrer);
        context.SaveChanges();

        var otpProvider = new OtpCapturingNotificationProvider();
        var otpService = new ProviderOtpService(context, otpProvider, Options.Create(new OtpOptions { Pepper = "test-only-otp-pepper-not-for-production-abc123" }));
        var mobile = "+9198" + Guid.NewGuid().ToString("N")[..8];
        await otpService.GenerateAsync(mobile, OtpPurpose.Registration);
        string otpCode = System.Text.RegularExpressions.Regex.Match(otpProvider.LastSmsMessage!, @"\d{6}").Value;

        var service = BuildService(context, otpService);
        var result = await service.RegisterAsync(new RegisterProviderRequest(
            mobile, otpCode, "Referee", "Referee's Repairs", null, true, null, "INACTIVECODE"));

        result.IsSuccess.Should().BeTrue();
        context.ProviderReferrals.Any(r => r.RefereeProviderId == result.Value.Id).Should().BeFalse();
    }
}
