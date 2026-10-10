using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Nestly.Application;
using Nestly.Application.Notifications;
using Nestly.Application.Referral;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Persistence.Interceptors;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers tasks 170 (admin list/detail) and 171 (funnel + program cost reports).</summary>
public sealed class ReferralAdminServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public ReferralAdminServiceTests(TestDatabase db) => _db = db;

    private static ReferralAdminService BuildAdminService(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(
            new ReferralRepository(context),
            new ReferralMilestoneRepository(context),
            new ReferralMilestoneAwardRepository(context),
            new CustomerRepository(context));

    private static ReferralRewardService BuildRewardService(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(
            new ReferralRepository(context),
            new ReferralProgramConfigRepository(context),
            new CustomerRepository(context),
            new WalletService(new WalletLedgerRepository(context), context),
            new CouponRepository(context),
            new ReferralMilestoneRepository(context),
            new ReferralMilestoneAwardRepository(context),
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
            NullLogger<ReferralRewardService>.Instance);

    private static ReferralQualifyingBookingHandler BuildHandler(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(new BookingRepository(context), new ReferralRepository(context), BuildRewardService(context), NullLogger<ReferralQualifyingBookingHandler>.Instance);

    private static Customer SeedCustomer(Nestly.Infrastructure.Persistence.NestlyDbContext context, string name)
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], name, CustomerStatus.Active,
            $"{name.ToLowerInvariant()}-{Guid.NewGuid():N}@example.com");
        context.Add(customer);
        context.SaveChanges();
        return customer;
    }

    private static ReferralProgramConfig SeedConfig(
        Nestly.Infrastructure.Persistence.NestlyDbContext context,
        ReferralRewardType referrerType = ReferralRewardType.WalletCredit,
        ReferralRewardType refereeType = ReferralRewardType.WalletCredit)
    {
        context.RemoveRange(context.ReferralProgramConfigs);
        context.SaveChanges();

        var config = new ReferralProgramConfig(Guid.NewGuid(), referrerType, 100m, refereeType, 50m, 299m, 30, null, isActive: true);
        context.Add(config);
        context.SaveChanges();
        return config;
    }

    private static Domain.Referral SeedRegisteredReferral(
        Nestly.Infrastructure.Persistence.NestlyDbContext context, Customer referrer, Customer referee, ReferralProgramConfig config)
    {
        var referral = new Domain.Referral(Guid.NewGuid(), referrer.Id, referee.Id, "CODE" + Guid.NewGuid().ToString("N")[..6], config);
        context.Add(referral);
        context.SaveChanges();
        return referral;
    }

    private static Booking SeedCompletedBooking(Nestly.Infrastructure.Persistence.NestlyDbContext context, Guid customerId, decimal totalPayable)
    {
        var booking = new Booking(
            Guid.NewGuid(), customerId,
            new CustomerSnapshot("Test Customer", "9" + Guid.NewGuid().ToString("N")[..9]),
            null,
            new AddressSnapshot("Home", "123 St", null, null, "560001", "Bengaluru", "Karnataka", 12.9m, 77.5m, "Test", "9000000000"),
            new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
            new PriceSnapshot(totalPayable, 1, totalPayable, 0, 0, totalPayable, 0, 0, 0, totalPayable));

        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.TransitionTo(BookingStatus.Confirmed);
        booking.TransitionTo(BookingStatus.AwaitingFulfilment);
        booking.TransitionTo(BookingStatus.Assigned);
        booking.TransitionTo(BookingStatus.InProgress);
        booking.TransitionTo(BookingStatus.Completed);

        context.Add(booking);
        context.SaveChanges();
        return booking;
    }

    private static async Task QualifyAndRewardAsync(Nestly.Infrastructure.Persistence.NestlyDbContext context, Customer referee, decimal amount)
    {
        var booking = SeedCompletedBooking(context, referee.Id, amount);
        await BuildHandler(context).Handle(new DomainEventNotification<BookingStatusChangedEvent>(
            new BookingStatusChangedEvent(booking.Id, BookingStatus.InProgress, BookingStatus.Completed)), CancellationToken.None);
    }

    [Fact]
    public async Task GetFunnelReportAsync_counts_each_stage_of_the_cohort_registered_in_range()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context);
        // Captured before this test creates anything, with NO backward
        // safety margin - the TestDatabase fixture is shared across every
        // test in this class and tests run sub-second, so even a 1-second
        // margin would reach back into a preceding test's data. Sequential
        // (non-parallel) execution within a class means nothing after this
        // exact point belongs to any OTHER test, so an open upper bound is
        // safe and no margin is needed on the lower bound either.
        var fromUtc = DateTime.UtcNow;

        var referrer = SeedCustomer(context, "Referrer");

        // Rewarded (qualifies and is rewarded).
        var rewardedReferee = SeedCustomer(context, "RewardedReferee");
        SeedRegisteredReferral(context, referrer, rewardedReferee, config);
        await QualifyAndRewardAsync(context, rewardedReferee, 500m);

        // Registered only (never qualifies - below minimum amount).
        var registeredOnlyReferee = SeedCustomer(context, "RegisteredOnlyReferee");
        SeedRegisteredReferral(context, referrer, registeredOnlyReferee, config);
        await QualifyAndRewardAsync(context, registeredOnlyReferee, 100m);

        var result = await BuildAdminService(context).GetFunnelReportAsync(fromUtc, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.RegisteredCount.Should().Be(2);
        result.Value.InvitedCount.Should().Be(2, "InvitedCount deliberately equals RegisteredCount - see the response's doc comment");
        result.Value.QualifiedCount.Should().Be(1);
        result.Value.RewardedCount.Should().Be(1);
    }

    [Fact]
    public async Task GetFunnelReportAsync_rejects_an_inverted_date_range()
    {
        using var context = _db.CreateContext();
        SeedConfig(context);

        var result = await BuildAdminService(context).GetFunnelReportAsync(DateTime.UtcNow, DateTime.UtcNow.AddDays(-1));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ReferralReport.InvalidRange");
    }

    [Fact]
    public async Task GetCostReportAsync_totals_wallet_and_coupon_rewards_plus_milestone_bonuses()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context, referrerType: ReferralRewardType.WalletCredit, refereeType: ReferralRewardType.Coupon);
        var milestone = new ReferralMilestone(Guid.NewGuid(), thresholdCount: 1, ReferralRewardType.WalletCredit, 200m, isActive: true);
        context.Add(milestone);
        context.SaveChanges();

        // Captured before this test creates anything - see the doc comment
        // on the equivalent line in the funnel report test above for why.
        var fromUtc = DateTime.UtcNow;

        var referrer = SeedCustomer(context, "Referrer");
        var referee = SeedCustomer(context, "Referee");
        SeedRegisteredReferral(context, referrer, referee, config);
        await QualifyAndRewardAsync(context, referee, 500m);

        var result = await BuildAdminService(context).GetCostReportAsync(fromUtc, null);

        result.IsSuccess.Should().BeTrue();
        // Referrer: 100 wallet credit + 200 milestone bonus wallet credit = 300.
        result.Value.TotalWalletCreditCost.Should().Be(300m);
        // Referee: 50 coupon.
        result.Value.TotalCouponCost.Should().Be(50m);
        result.Value.TotalCost.Should().Be(350m);
        result.Value.RewardedReferralCount.Should().Be(1);
        result.Value.MilestoneBonusCount.Should().Be(1);
    }

    [Fact]
    public async Task SearchAsync_filters_by_status_and_fraud_flag()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context);
        var referrer = SeedCustomer(context, "Referrer");
        var referee = SeedCustomer(context, "Referee");
        var referral = SeedRegisteredReferral(context, referrer, referee, config);

        var unfiltered = await BuildAdminService(context).SearchAsync(new ReferralAdminSearchRequest(null, null, null, 1, 20));
        unfiltered.Items.Should().Contain(i => i.Id == referral.Id);

        var flaggedOnly = await BuildAdminService(context).SearchAsync(new ReferralAdminSearchRequest(null, true, null, 1, 20));
        flaggedOnly.Items.Should().NotContain(i => i.Id == referral.Id, "this referral is not flagged");

        var rewardedOnly = await BuildAdminService(context).SearchAsync(new ReferralAdminSearchRequest(ReferralStatus.Rewarded, null, null, 1, 20));
        rewardedOnly.Items.Should().NotContain(i => i.Id == referral.Id, "this referral is still Registered, not Rewarded");
    }

    /// <summary>
    /// Task 253: SearchAsync used to resolve the referrer and referee with a
    /// GetByIdAsync each, per row - 2N round trips for an N-row page, each
    /// loading a whole Customer aggregate to read one column. It now batches
    /// every name on the page into a single lookup, so the command count must
    /// stay flat as the page grows rather than scaling with it.
    /// </summary>
    [Fact]
    public async Task SearchAsync_does_not_scale_its_query_count_with_the_page_size()
    {
        var counter = new CountingCommandInterceptor();
        using var context = _db.CreateContext(counter);
        var config = SeedConfig(context);

        // Distinct customers on both sides of every referral: the pre-fix
        // per-row lookups had no cache, so this is the worst case for it.
        for (int i = 0; i < 10; i++)
        {
            SeedRegisteredReferral(context, SeedCustomer(context, $"Referrer{i}"), SeedCustomer(context, $"Referee{i}"), config);
        }

        counter.Reset();
        var result = await BuildAdminService(context).SearchAsync(new ReferralAdminSearchRequest(null, null, null, 1, 20));

        result.Items.Should().HaveCountGreaterThanOrEqualTo(10);
        result.Items.Should().NotContain(i => i.ReferrerName == "Unknown", "every referrer on the page exists");
        result.Items.Should().NotContain(i => i.RefereeName == "Unknown", "every referee on the page exists");

        // Count + page + one batched name lookup. The pre-fix code issued
        // 2 more commands for every additional row.
        counter.CommandCount.Should().BeLessThanOrEqualTo(4,
            "names must be batched, not resolved per row");
    }

    [Fact]
    public async Task GetByIdAsync_returns_not_found_for_an_unknown_id()
    {
        using var context = _db.CreateContext();
        var result = await BuildAdminService(context).GetByIdAsync(Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Referral.NotFound");
    }

    [Fact]
    public async Task GetByIdAsync_returns_full_detail_for_an_existing_referral()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context);
        var referrer = SeedCustomer(context, "Referrer");
        var referee = SeedCustomer(context, "Referee");
        var referral = SeedRegisteredReferral(context, referrer, referee, config);

        var result = await BuildAdminService(context).GetByIdAsync(referral.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(referral.Id);
        result.Value.ReferrerName.Should().Be("Referrer");
        result.Value.RefereeName.Should().Be("Referee");
        result.Value.Status.Should().Be(ReferralStatus.Registered);
    }
}
