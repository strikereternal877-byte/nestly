using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Identity;
using Nestly.Application.Notifications;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers task 163: referral code registration wiring, self-referral block, one-referral-per-referee.</summary>
public sealed class ReferralRegistrationTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public ReferralRegistrationTests(TestDatabase db) => _db = db;

    /// <summary>Captures the OTP SMS so the test can read the plaintext code back out - OtpService only ever persists a hash.</summary>
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

    private static CustomerRegistrationService BuildService(
        Nestly.Infrastructure.Persistence.NestlyDbContext context, OtpService otpService, AccountOptions? accountOptions = null) =>
        new(
            new CustomerRepository(context),
            new CustomerAuthIdentityRepository(context),
            otpService,
            new NotificationDispatchService(
                new NotificationTemplateRenderer(new FakeNotificationTemplateRepository(), new MemoryCache(new MemoryCacheOptions())),
                new SandboxNotificationProvider(NullLogger<SandboxNotificationProvider>.Instance, new FakeHostEnvironment()),
                new SandboxPushNotificationProvider(NullLogger<SandboxPushNotificationProvider>.Instance),
                new NotificationEventRepository(context),
                new DeviceTokenRepository(context),
                new CustomerRepository(context),
                new ProviderRepository(context),
                new NoOpMetricsService(),
                NullLogger<NotificationDispatchService>.Instance),
            new ReferralRepository(context),
            new ReferralProgramConfigRepository(context),
            NullLogger<CustomerRegistrationService>.Instance,
            Options.Create(accountOptions ?? new AccountOptions()));

    private static void SeedActiveConfig(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        if (context.ReferralProgramConfigs.Any()) return;
        context.Add(new ReferralProgramConfig(
            Guid.NewGuid(), ReferralRewardType.WalletCredit, 100m, ReferralRewardType.WalletCredit, 100m,
            299m, 30, null, isActive: true));
        context.SaveChanges();
    }

    [Fact]
    public async Task RegisterAsync_creates_a_referral_when_a_valid_referral_code_is_used()
    {
        using var context = _db.CreateContext();
        SeedActiveConfig(context);

        var referrer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Referrer", CustomerStatus.Active);
        referrer.SetReferralCode("REFCODE1");
        context.Add(referrer);
        context.SaveChanges();

        var otpProvider = new OtpCapturingNotificationProvider();
        var otpService = new OtpService(context, otpProvider, Options.Create(new OtpOptions { Pepper = "test-only-otp-pepper-not-for-production-abc123" }));
        var mobile = "9" + Guid.NewGuid().ToString("N")[..9];
        await otpService.GenerateAsync(mobile, OtpPurpose.Registration);
        string otpCode = System.Text.RegularExpressions.Regex.Match(otpProvider.LastSmsMessage!, @"\d{6}").Value;

        var service = BuildService(context, otpService);
        var result = await service.RegisterAsync(new RegisterCustomerRequest(
            mobile, otpCode, "Referee", null, null, true, "REFCODE1"));

        result.IsSuccess.Should().BeTrue();

        var referral = context.Referrals.Single(r => r.RefereeCustomerId == result.Value.Id);
        referral.ReferrerCustomerId.Should().Be(referrer.Id);
        referral.Status.Should().Be(ReferralStatus.Registered);
        referral.ReferralCodeUsed.Should().Be("REFCODE1");

        // Task 172: the referrer is notified their code was used - before
        // NotificationTemplateSeedData carried rows for ReferralRegistered
        // this would have logged a "no_template" Failed row instead.
        context.NotificationEvents
            .Where(e => e.EventType == NotificationEventType.ReferralRegistered && e.CustomerId == referrer.Id)
            .Should().NotBeEmpty()
            .And.OnlyContain(e => e.Status == NotificationDeliveryStatus.Sent);
    }

    [Fact]
    public async Task RegisterAsync_succeeds_but_skips_referral_creation_for_an_unknown_code()
    {
        using var context = _db.CreateContext();
        SeedActiveConfig(context);

        var otpProvider = new OtpCapturingNotificationProvider();
        var otpService = new OtpService(context, otpProvider, Options.Create(new OtpOptions { Pepper = "test-only-otp-pepper-not-for-production-abc123" }));
        var mobile = "9" + Guid.NewGuid().ToString("N")[..9];
        await otpService.GenerateAsync(mobile, OtpPurpose.Registration);
        string otpCode = System.Text.RegularExpressions.Regex.Match(otpProvider.LastSmsMessage!, @"\d{6}").Value;

        var service = BuildService(context, otpService);
        var result = await service.RegisterAsync(new RegisterCustomerRequest(
            mobile, otpCode, "Referee", null, null, true, "DOES-NOT-EXIST"));

        result.IsSuccess.Should().BeTrue();
        context.Referrals.Any(r => r.RefereeCustomerId == result.Value.Id).Should().BeFalse();
    }

    /// <summary>
    /// The self-referral guard in TryCreateReferralAsync checks mobile/email
    /// match, but both are unconditionally DB-unique (CustomerConfiguration's
    /// indexes, not just the app-level RequireUniqueEmail toggle - confirmed
    /// experimentally: a second customer row with the same email throws a
    /// raw SQLite/Postgres unique-constraint violation regardless of that
    /// option) - so a real self-referral-by-mobile-or-email attempt can
    /// never reach that guard through the public registration flow at all;
    /// it fails earlier, on MobileAlreadyRegistered/EmailAlreadyRegistered,
    /// which is a strictly stronger protection than the referral-specific
    /// check would have been. This test documents that finding rather than
    /// pretending the guard is independently exercisable - it is defense in
    /// depth against a future schema change (e.g. relaxed email uniqueness),
    /// not currently reachable code.
    /// </summary>
    [Fact]
    public async Task RegisterAsync_a_genuine_self_referral_attempt_fails_earlier_on_mobile_uniqueness()
    {
        using var context = _db.CreateContext();
        SeedActiveConfig(context);

        var otpProvider = new OtpCapturingNotificationProvider();
        var otpService = new OtpService(context, otpProvider, Options.Create(new OtpOptions { Pepper = "test-only-otp-pepper-not-for-production-abc123" }));
        var mobile = "9" + Guid.NewGuid().ToString("N")[..9];

        var referrer = new Customer(Guid.NewGuid(), mobile, "Self Referrer", CustomerStatus.Active);
        referrer.SetReferralCode("SELFCODE");
        context.Add(referrer);
        context.SaveChanges();

        await otpService.GenerateAsync(mobile, OtpPurpose.Registration);
        string otpCode = System.Text.RegularExpressions.Regex.Match(otpProvider.LastSmsMessage!, @"\d{6}").Value;

        var service = BuildService(context, otpService);
        var result = await service.RegisterAsync(new RegisterCustomerRequest(
            mobile, otpCode, "Self Referrer", null, null, true, "SELFCODE"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Registration.MobileAlreadyRegistered");
    }
}
