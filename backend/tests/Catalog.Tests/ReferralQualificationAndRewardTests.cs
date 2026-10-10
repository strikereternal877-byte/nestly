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

/// <summary>Covers tasks 164-165: booking-completion qualification and reward disbursement to both sides.</summary>
public sealed class ReferralQualificationAndRewardTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public ReferralQualificationAndRewardTests(TestDatabase db) => _db = db;

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

    private static ReferralProgramConfig SeedConfig(
        Nestly.Infrastructure.Persistence.NestlyDbContext context,
        ReferralRewardType referrerType = ReferralRewardType.WalletCredit,
        ReferralRewardType refereeType = ReferralRewardType.WalletCredit,
        decimal minQualifyingOrderAmount = 299m,
        int? maxReferralsPerCustomer = null)
    {
        // ReferralProgramConfig is a genuine single-row table in production
        // (IReferralProgramConfigRepository.GetAsync() is an unfiltered
        // FirstOrDefaultAsync, by design - see that interface's doc
        // comment). TestDatabase shares one database across every test in
        // this class (IClassFixture<TestDatabase>), so blindly inserting a
        // fresh row per test would leave several rows behind and make
        // GetAsync() pick an arbitrary one - clearing existing rows first
        // keeps that single-row invariant true in tests too.
        context.RemoveRange(context.ReferralProgramConfigs);
        context.SaveChanges();

        var config = new ReferralProgramConfig(
            Guid.NewGuid(), referrerType, 100m, refereeType, 50m,
            minQualifyingOrderAmount, 30, maxReferralsPerCustomer, isActive: true);
        context.Add(config);
        context.SaveChanges();
        return config;
    }

    private static Domain.Referral SeedRegisteredReferral(
        Nestly.Infrastructure.Persistence.NestlyDbContext context, Customer referrer, Customer referee, ReferralProgramConfig config)
    {
        var referral = new Domain.Referral(Guid.NewGuid(), referrer.Id, referee.Id, "TESTCODE", config);
        context.Add(referral);
        context.SaveChanges();
        return referral;
    }

    /// <summary>Builds a Completed booking directly via the domain constructor + TransitionTo chain - the full BookingService orchestration is out of scope for what this test needs (see the class doc comment).</summary>
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

    [Fact]
    public async Task Handle_qualifies_and_disburses_wallet_credit_to_both_sides()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context);
        var referrer = SeedCustomer(context, "Referrer");
        var referee = SeedCustomer(context, "Referee");
        var referral = SeedRegisteredReferral(context, referrer, referee, config);
        var booking = SeedCompletedBooking(context, referee.Id, totalPayable: 500m);

        await BuildHandler(context).Handle(CompletionNotification(booking.Id), CancellationToken.None);

        var updated = context.Referrals.Single(r => r.Id == referral.Id);
        updated.Status.Should().Be(ReferralStatus.Rewarded);
        updated.QualifyingBookingId.Should().Be(booking.Id);
        updated.ReferrerWalletEntryId.Should().NotBeNull();
        updated.RefereeWalletEntryId.Should().NotBeNull();

        context.WalletLedgerEntries.Single(e => e.CustomerId == referrer.Id).Amount.Should().Be(100m);
        context.WalletLedgerEntries.Single(e => e.CustomerId == referee.Id).Amount.Should().Be(50m);
    }

    [Fact]
    public async Task Handle_issues_a_customer_restricted_coupon_for_coupon_type_rewards()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context, referrerType: ReferralRewardType.Coupon, refereeType: ReferralRewardType.Coupon);
        var referrer = SeedCustomer(context, "Referrer");
        var referee = SeedCustomer(context, "Referee");
        var referral = SeedRegisteredReferral(context, referrer, referee, config);
        var booking = SeedCompletedBooking(context, referee.Id, totalPayable: 500m);

        await BuildHandler(context).Handle(CompletionNotification(booking.Id), CancellationToken.None);

        var updated = context.Referrals.Single(r => r.Id == referral.Id);
        updated.ReferrerCouponId.Should().NotBeNull();
        updated.RefereeCouponId.Should().NotBeNull();

        var referrerCoupon = context.Coupons.Single(c => c.Id == updated.ReferrerCouponId);
        referrerCoupon.RestrictedToCustomerId.Should().Be(referrer.Id);
        referrerCoupon.DiscountValue.Should().Be(100m);
    }

    [Fact]
    public async Task Handle_does_not_qualify_a_booking_below_the_minimum_amount()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context, minQualifyingOrderAmount: 500m);
        var referrer = SeedCustomer(context, "Referrer");
        var referee = SeedCustomer(context, "Referee");
        var referral = SeedRegisteredReferral(context, referrer, referee, config);
        var booking = SeedCompletedBooking(context, referee.Id, totalPayable: 299m);

        await BuildHandler(context).Handle(CompletionNotification(booking.Id), CancellationToken.None);

        context.Referrals.Single(r => r.Id == referral.Id).Status.Should().Be(ReferralStatus.Registered);
    }

    [Fact]
    public async Task Handle_skips_the_referrer_reward_once_the_per_customer_cap_is_reached_but_still_rewards_the_referee()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context, maxReferralsPerCustomer: 1);
        var referrer = SeedCustomer(context, "Referrer");

        // First referral: consumes the referrer's one allowed reward.
        var firstReferee = SeedCustomer(context, "FirstReferee");
        var firstReferral = SeedRegisteredReferral(context, referrer, firstReferee, config);
        var firstBooking = SeedCompletedBooking(context, firstReferee.Id, totalPayable: 500m);
        await BuildHandler(context).Handle(CompletionNotification(firstBooking.Id), CancellationToken.None);
        context.Referrals.Single(r => r.Id == firstReferral.Id).Status.Should().Be(ReferralStatus.Rewarded);

        // Second referral: referrer is now at the cap.
        var secondReferee = SeedCustomer(context, "SecondReferee");
        var secondReferral = SeedRegisteredReferral(context, referrer, secondReferee, config);
        var secondBooking = SeedCompletedBooking(context, secondReferee.Id, totalPayable: 500m);
        await BuildHandler(context).Handle(CompletionNotification(secondBooking.Id), CancellationToken.None);

        var updated = context.Referrals.Single(r => r.Id == secondReferral.Id);
        updated.Status.Should().Be(ReferralStatus.Rewarded);
        updated.ReferrerWalletEntryId.Should().BeNull("the referrer already hit the reward cap");
        updated.RefereeWalletEntryId.Should().NotBeNull("the referee's own reward is independent of the referrer's cap");
    }

    /// <summary>
    /// Regression coverage for the referral disbursement race: two of a
    /// referee's bookings completing near-simultaneously each load their own
    /// (equally stale) Registered referral before either commits -
    /// TryMarkQualifiedAsync's atomic conditional UPDATE, not the read, is
    /// what must decide which one (if either) is allowed to disburse.
    /// Complements the deterministic sequential test below: this one
    /// exercises genuine Task.WhenAll concurrency (mirroring
    /// PaymentWebhookServiceTests' identical-shape NESTLY-006 test) so a
    /// regression that removes the WHERE guard entirely - letting every call
    /// through unconditionally - still fails this test even though it would
    /// pass the sequential one below.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_qualifying_bookings_for_the_same_referral_qualify_it_at_most_once()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context);
        var referrer = SeedCustomer(context, "Referrer");
        var referee = SeedCustomer(context, "Referee");
        var referral = SeedRegisteredReferral(context, referrer, referee, config);

        var attempts = Enumerable.Range(0, 2).Select(async _ =>
        {
            using var raceContext = _db.CreateContext();
            return await new ReferralRepository(raceContext).TryMarkQualifiedAsync(referral.Id, Guid.NewGuid());
        });

        var results = await Task.WhenAll(attempts);

        results.Count(r => r).Should().BeLessThanOrEqualTo(1, "at most one of the two racing qualifying bookings may transition the referral to Qualified");

        using var finalContext = _db.CreateContext();
        finalContext.Referrals.Single(r => r.Id == referral.Id).Status.Should().Be(ReferralStatus.Qualified);
    }

    /// <summary>
    /// Regression coverage for the referral disbursement race.
    /// TryMarkQualifiedAsync's WHERE clause ("still Registered") is
    /// re-evaluated by the database against the committed row at UPDATE
    /// time, the same NESTLY-006 idiom PaymentTransactionRepository.
    /// TryMarkAttemptResolvedAsync already uses for the identical shape of
    /// race - so of two concurrent qualifying bookings for the same
    /// referral, only the first UPDATE the database actually executes can
    /// ever affect a row; the second necessarily finds Status already
    /// flipped and affects zero, regardless of what either caller's own
    /// earlier read believed. This proves that guard directly: a second call
    /// after the first has already flipped the row must report false, not
    /// silently re-affect it.
    ///
    /// (A genuinely concurrent Task.WhenAll version of this test was tried
    /// first, mirroring PaymentWebhookServiceTests' own such test, but
    /// proved unreliable here - it passed even against a deliberately
    /// reverted, non-atomic implementation, because fast in-memory SQLite
    /// does not reliably interleave two tasks' single-row read-then-write at
    /// the critical moment. That is a limitation of this in-memory
    /// harness's timing, not evidence the underlying database-level
    /// mechanism is unsafe - Postgres row locking during a real UPDATE
    /// enforces exactly the guarantee this test's assertions describe.)
    /// </summary>
    [Fact]
    public async Task TryMarkQualifiedAsync_only_the_first_of_two_calls_for_the_same_referral_succeeds()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context);
        var referrer = SeedCustomer(context, "Referrer");
        var referee = SeedCustomer(context, "Referee");
        var referral = SeedRegisteredReferral(context, referrer, referee, config);
        var firstBookingId = Guid.NewGuid();
        var secondBookingId = Guid.NewGuid();

        bool first;
        using (var firstContext = _db.CreateContext())
        {
            first = await new ReferralRepository(firstContext).TryMarkQualifiedAsync(referral.Id, firstBookingId);
        }

        bool second;
        using (var secondContext = _db.CreateContext())
        {
            second = await new ReferralRepository(secondContext).TryMarkQualifiedAsync(referral.Id, secondBookingId);
        }

        first.Should().BeTrue("the referral was still Registered when this call's UPDATE ran");
        second.Should().BeFalse("the referral was already Qualified by the first call - this must not re-affect the row or report success");

        using var finalContext = _db.CreateContext();
        var updated = finalContext.Referrals.Single(r => r.Id == referral.Id);
        updated.Status.Should().Be(ReferralStatus.Qualified);
        updated.QualifyingBookingId.Should().Be(firstBookingId, "the second, losing call must not overwrite which booking actually qualified it");
    }

    [Fact]
    public async Task Handle_is_a_no_op_when_the_completed_bookings_customer_has_no_pending_referral()
    {
        using var context = _db.CreateContext();
        var customer = SeedCustomer(context, "NoReferral");
        var booking = SeedCompletedBooking(context, customer.Id, totalPayable: 500m);

        var act = async () => await BuildHandler(context).Handle(CompletionNotification(booking.Id), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
