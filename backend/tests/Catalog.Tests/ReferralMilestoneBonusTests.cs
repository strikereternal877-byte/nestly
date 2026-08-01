using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Nestly.Application;
using Nestly.Application.Notifications;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Persistence.Interceptors;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Covers task 174: a referrer's Rewarded-referral count crossing an active
/// milestone threshold pays a bonus through the same wallet-credit/coupon
/// mechanics as a normal per-referral reward
/// (<see cref="ReferralRewardService.DisburseAsync"/>'s "IssueRewardAsync"
/// core), exactly once per (milestone, referrer) pair.
/// </summary>
public sealed class ReferralMilestoneBonusTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public ReferralMilestoneBonusTests(TestDatabase db) => _db = db;

    private static ReferralRewardService BuildRewardService(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(
            new ReferralRepository(context),
            new ReferralProgramConfigRepository(context),
            new CustomerRepository(context),
            new WalletService(new WalletLedgerRepository(context)),
            new CouponRepository(context),
            new ReferralMilestoneRepository(context),
            new ReferralMilestoneAwardRepository(context),
            new NotificationDispatchService(
                new NotificationTemplateRenderer(new FakeNotificationTemplateRepository(), new MemoryCache(new MemoryCacheOptions())),
                new SandboxNotificationProvider(NullLogger<SandboxNotificationProvider>.Instance),
                new SandboxPushNotificationProvider(NullLogger<SandboxPushNotificationProvider>.Instance),
                new NotificationEventRepository(context),
                new NoOpMetricsService(),
                NullLogger<NotificationDispatchService>.Instance),
            NullLogger<ReferralRewardService>.Instance);

    private static ReferralQualifyingBookingHandler BuildHandler(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(
            new BookingRepository(context),
            new ReferralRepository(context),
            BuildRewardService(context),
            NullLogger<ReferralQualifyingBookingHandler>.Instance);

    private static Customer SeedCustomer(Nestly.Infrastructure.Persistence.NestlyDbContext context, string name)
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], name, CustomerStatus.Active,
            $"{name.ToLowerInvariant()}-{Guid.NewGuid():N}@example.com");
        context.Add(customer);
        context.SaveChanges();
        return customer;
    }

    private static ReferralProgramConfig SeedConfig(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        // Same single-row-table cleanup reasoning as ReferralQualificationAndRewardTests.SeedConfig.
        context.RemoveRange(context.ReferralProgramConfigs);
        context.SaveChanges();

        var config = new ReferralProgramConfig(
            Guid.NewGuid(), ReferralRewardType.WalletCredit, 100m, ReferralRewardType.WalletCredit, 50m,
            299m, 30, maxReferralsPerCustomer: null, isActive: true);
        context.Add(config);
        context.SaveChanges();
        return config;
    }

    /// <summary>
    /// IClassFixture&lt;TestDatabase&gt; shares one physical database across every
    /// test in this class (same reasoning as SeedConfig's cleanup above), and
    /// <c>referral_milestone.threshold_count</c> carries a unique index - two
    /// tests seeding the same threshold value would otherwise collide. Clearing
    /// both milestone tables before each test's own seed keeps every test's
    /// milestone state isolated regardless of what threshold values it picks.
    /// </summary>
    private static void ClearMilestones(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        context.RemoveRange(context.Set<ReferralMilestoneAward>());
        context.RemoveRange(context.Set<ReferralMilestone>());
        context.SaveChanges();
    }

    private static ReferralMilestone SeedMilestone(
        Nestly.Infrastructure.Persistence.NestlyDbContext context, int thresholdCount, decimal bonusValue, ReferralRewardType bonusType = ReferralRewardType.WalletCredit, bool isActive = true)
    {
        var milestone = new ReferralMilestone(Guid.NewGuid(), thresholdCount, bonusType, bonusValue, isActive);
        context.Add(milestone);
        context.SaveChanges();
        return milestone;
    }

    private static Domain.Referral SeedRegisteredReferral(
        Nestly.Infrastructure.Persistence.NestlyDbContext context, Customer referrer, Customer referee, ReferralProgramConfig config)
    {
        var referral = new Domain.Referral(Guid.NewGuid(), referrer.Id, referee.Id, "TESTCODE-" + Guid.NewGuid().ToString("N")[..6], config);
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

    private static DomainEventNotification<BookingStatusChangedEvent> CompletionNotification(Guid bookingId) =>
        new(new BookingStatusChangedEvent(bookingId, BookingStatus.InProgress, BookingStatus.Completed));

    /// <summary>Drives one referee through to a Rewarded referral for the given referrer, so a test can build up the referrer's rewarded count one referral at a time.</summary>
    private static async Task RewardOneReferralAsync(
        Nestly.Infrastructure.Persistence.NestlyDbContext context, Customer referrer, ReferralProgramConfig config)
    {
        var referee = SeedCustomer(context, "Referee-" + Guid.NewGuid().ToString("N")[..6]);
        SeedRegisteredReferral(context, referrer, referee, config);
        var booking = SeedCompletedBooking(context, referee.Id, totalPayable: 500m);
        await BuildHandler(context).Handle(CompletionNotification(booking.Id), CancellationToken.None);
    }

    [Fact]
    public async Task Crossing_an_active_milestone_threshold_pays_the_bonus_once()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context);
        var referrer = SeedCustomer(context, "Referrer");
        ClearMilestones(context);
        SeedMilestone(context, thresholdCount: 2, bonusValue: 250m);

        await RewardOneReferralAsync(context, referrer, config);
        await RewardOneReferralAsync(context, referrer, config);

        var award = context.Set<ReferralMilestoneAward>().Single(a => a.ReferrerCustomerId == referrer.Id);
        award.WalletEntryId.Should().NotBeNull();

        var bonusEntries = context.WalletLedgerEntries
            .Where(e => e.CustomerId == referrer.Id && e.SourceType == WalletSourceType.ReferralMilestoneBonus)
            .ToList();
        bonusEntries.Should().ContainSingle();
        bonusEntries.Single().Amount.Should().Be(250m);
    }

    [Fact]
    public async Task Does_not_pay_the_same_milestone_twice_for_the_same_referrer()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context);
        var referrer = SeedCustomer(context, "Referrer");
        ClearMilestones(context);
        var milestone = SeedMilestone(context, thresholdCount: 1, bonusValue: 100m);

        await RewardOneReferralAsync(context, referrer, config);
        await RewardOneReferralAsync(context, referrer, config);
        await RewardOneReferralAsync(context, referrer, config);

        var awards = context.Set<ReferralMilestoneAward>()
            .Where(a => a.ReferrerCustomerId == referrer.Id && a.ReferralMilestoneId == milestone.Id)
            .ToList();
        awards.Should().ContainSingle("the threshold of 1 is only ever crossed once, and the award row guards against paying it again");
    }

    [Fact]
    public async Task Inactive_milestones_never_pay()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context);
        var referrer = SeedCustomer(context, "Referrer");
        ClearMilestones(context);
        SeedMilestone(context, thresholdCount: 1, bonusValue: 100m, isActive: false);

        await RewardOneReferralAsync(context, referrer, config);

        context.Set<ReferralMilestoneAward>().Any(a => a.ReferrerCustomerId == referrer.Id).Should().BeFalse();
    }

    [Fact]
    public async Task Milestone_bonus_still_pays_when_the_triggering_referral_was_coupon_type()
    {
        using var context = _db.CreateContext();
        context.RemoveRange(context.ReferralProgramConfigs);
        context.SaveChanges();
        var config = new ReferralProgramConfig(
            Guid.NewGuid(), ReferralRewardType.Coupon, 100m, ReferralRewardType.Coupon, 50m,
            299m, 30, maxReferralsPerCustomer: null, isActive: true);
        context.Add(config);
        context.SaveChanges();

        var referrer = SeedCustomer(context, "Referrer");
        ClearMilestones(context);
        SeedMilestone(context, thresholdCount: 1, bonusValue: 75m, bonusType: ReferralRewardType.WalletCredit);

        await RewardOneReferralAsync(context, referrer, config);

        var bonusEntries = context.WalletLedgerEntries
            .Where(e => e.CustomerId == referrer.Id && e.SourceType == WalletSourceType.ReferralMilestoneBonus)
            .ToList();
        bonusEntries.Should().ContainSingle("the milestone bonus type is independent of the per-referral reward type");
        bonusEntries.Single().Amount.Should().Be(75m);
    }
}
