using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nestly.Application;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Persistence.Interceptors;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers PROVIDER-REFERRAL.md's qualifying-job handler and reward disbursement, mirrors ReferralQualificationAndRewardTests.</summary>
public sealed class ProviderReferralQualificationAndRewardTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public ProviderReferralQualificationAndRewardTests(TestDatabase db) => _db = db;

    private static ProviderReferralRewardService BuildRewardService(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(
            new ProviderReferralRepository(context),
            new ProviderReferralProgramConfigRepository(context),
            new ProviderRepository(context),
            new ProviderEarningLedgerService(
                new ProviderRepository(context),
                new ProviderEarningLedgerRepository(context),
                new BookingRepository(context),
                new PaymentTransactionRepository(context),
                new ProviderPayoutRepository(context)),
            NullLogger<ProviderReferralRewardService>.Instance);

    private static ProviderReferralQualifyingJobHandler BuildHandler(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(
            new BookingRepository(context),
            new ProviderReferralRepository(context),
            BuildRewardService(context),
            NullLogger<ProviderReferralQualifyingJobHandler>.Instance);

    private static Provider SeedProvider(Nestly.Infrastructure.Persistence.NestlyDbContext context, string name)
    {
        var provider = new Provider(Guid.NewGuid(), name, name, ProviderType.Individual, "+9198" + Guid.NewGuid().ToString("N")[..8]);
        context.Add(provider);
        context.SaveChanges();
        return provider;
    }

    private static ProviderReferralProgramConfig SeedConfig(
        Nestly.Infrastructure.Persistence.NestlyDbContext context,
        int qualifyingCompletedJobsCount = 1,
        int? maxReferralsPerProvider = null)
    {
        // Single-row table in production (see IProviderReferralProgramConfigRepository's
        // doc comment) - TestDatabase shares one database across every test
        // in this class, so clear existing rows first, same convention as
        // ReferralQualificationAndRewardTests.SeedConfig.
        context.RemoveRange(context.ProviderReferralProgramConfigs);
        context.SaveChanges();

        var config = new ProviderReferralProgramConfig(
            Guid.NewGuid(), 500m, 500m, qualifyingCompletedJobsCount, 45, maxReferralsPerProvider, isActive: true);
        context.Add(config);
        context.SaveChanges();
        return config;
    }

    private static ProviderReferral SeedRegisteredReferral(
        Nestly.Infrastructure.Persistence.NestlyDbContext context, Provider referrer, Provider referee, ProviderReferralProgramConfig config)
    {
        var referral = new ProviderReferral(Guid.NewGuid(), referrer.Id, referee.Id, "TESTCODE", config);
        context.Add(referral);
        context.SaveChanges();
        return referral;
    }

    private static Customer SeedCustomer(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Test Customer", CustomerStatus.Active);
        context.Add(customer);
        context.SaveChanges();
        return customer;
    }

    /// <summary>Builds a Completed booking assigned to the given provider, mirrors ReferralQualificationAndRewardTests.SeedCompletedBooking.</summary>
    private static Booking SeedCompletedBookingForProvider(Nestly.Infrastructure.Persistence.NestlyDbContext context, Guid providerId)
    {
        var booking = new Booking(
            Guid.NewGuid(), SeedCustomer(context).Id,
            new CustomerSnapshot("Test Customer", "9" + Guid.NewGuid().ToString("N")[..9]),
            null,
            new AddressSnapshot("Home", "123 St", null, null, "560001", "Bengaluru", "Karnataka", 12.9m, 77.5m, "Test", "9000000000"),
            new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
            new PriceSnapshot(500m, 1, 500m, 0, 0, 500m, 0, 0, 0, 500m));

        booking.AssignProvider(providerId);
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
    public async Task Handle_qualifies_and_credits_the_earning_ledger_on_both_sides_once_the_job_count_is_reached()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context, qualifyingCompletedJobsCount: 1);
        var referrer = SeedProvider(context, "Referrer");
        var referee = SeedProvider(context, "Referee");
        var referral = SeedRegisteredReferral(context, referrer, referee, config);
        var booking = SeedCompletedBookingForProvider(context, referee.Id);

        await BuildHandler(context).Handle(CompletionNotification(booking.Id), CancellationToken.None);

        var updated = context.ProviderReferrals.Single(r => r.Id == referral.Id);
        updated.Status.Should().Be(ProviderReferralStatus.Rewarded);
        updated.QualifyingBookingId.Should().Be(booking.Id);
        updated.ReferrerEarningEntryId.Should().NotBeNull();
        updated.RefereeEarningEntryId.Should().NotBeNull();

        context.ProviderEarningLedgerEntries.Single(e => e.ProviderId == referrer.Id).Amount.Should().Be(500m);
        context.ProviderEarningLedgerEntries.Single(e => e.ProviderId == referee.Id).Amount.Should().Be(500m);
    }

    [Fact]
    public async Task Handle_does_not_qualify_before_the_configured_completed_job_count_is_reached()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context, qualifyingCompletedJobsCount: 3);
        var referrer = SeedProvider(context, "Referrer");
        var referee = SeedProvider(context, "Referee");
        var referral = SeedRegisteredReferral(context, referrer, referee, config);

        var firstBooking = SeedCompletedBookingForProvider(context, referee.Id);
        await BuildHandler(context).Handle(CompletionNotification(firstBooking.Id), CancellationToken.None);
        context.ProviderReferrals.Single(r => r.Id == referral.Id).Status.Should().Be(ProviderReferralStatus.Registered);

        var secondBooking = SeedCompletedBookingForProvider(context, referee.Id);
        await BuildHandler(context).Handle(CompletionNotification(secondBooking.Id), CancellationToken.None);
        context.ProviderReferrals.Single(r => r.Id == referral.Id).Status.Should().Be(ProviderReferralStatus.Registered);

        var thirdBooking = SeedCompletedBookingForProvider(context, referee.Id);
        await BuildHandler(context).Handle(CompletionNotification(thirdBooking.Id), CancellationToken.None);
        context.ProviderReferrals.Single(r => r.Id == referral.Id).Status.Should().Be(ProviderReferralStatus.Rewarded);
    }

    [Fact]
    public async Task Handle_skips_the_referrer_reward_once_the_per_provider_cap_is_reached_but_still_rewards_the_referee()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context, maxReferralsPerProvider: 1);
        var referrer = SeedProvider(context, "Referrer");

        var firstReferee = SeedProvider(context, "FirstReferee");
        var firstReferral = SeedRegisteredReferral(context, referrer, firstReferee, config);
        var firstBooking = SeedCompletedBookingForProvider(context, firstReferee.Id);
        await BuildHandler(context).Handle(CompletionNotification(firstBooking.Id), CancellationToken.None);
        context.ProviderReferrals.Single(r => r.Id == firstReferral.Id).Status.Should().Be(ProviderReferralStatus.Rewarded);

        var secondReferee = SeedProvider(context, "SecondReferee");
        var secondReferral = SeedRegisteredReferral(context, referrer, secondReferee, config);
        var secondBooking = SeedCompletedBookingForProvider(context, secondReferee.Id);
        await BuildHandler(context).Handle(CompletionNotification(secondBooking.Id), CancellationToken.None);

        var updated = context.ProviderReferrals.Single(r => r.Id == secondReferral.Id);
        updated.Status.Should().Be(ProviderReferralStatus.Rewarded);
        updated.ReferrerEarningEntryId.Should().BeNull("the referrer already hit the reward cap");
        updated.RefereeEarningEntryId.Should().NotBeNull("the referee's own reward is independent of the referrer's cap");
    }

    [Fact]
    public async Task Handle_is_a_no_op_when_the_completed_bookings_provider_has_no_pending_referral()
    {
        using var context = _db.CreateContext();
        var provider = SeedProvider(context, "NoReferral");
        var booking = SeedCompletedBookingForProvider(context, provider.Id);

        var act = async () => await BuildHandler(context).Handle(CompletionNotification(booking.Id), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Handle_is_a_no_op_when_the_completed_booking_has_no_assigned_provider()
    {
        using var context = _db.CreateContext();
        var booking = new Booking(
            Guid.NewGuid(), SeedCustomer(context).Id,
            new CustomerSnapshot("Test Customer", "9" + Guid.NewGuid().ToString("N")[..9]),
            null,
            new AddressSnapshot("Home", "123 St", null, null, "560001", "Bengaluru", "Karnataka", 12.9m, 77.5m, "Test", "9000000000"),
            new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
            new PriceSnapshot(500m, 1, 500m, 0, 0, 500m, 0, 0, 0, 500m));
        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.TransitionTo(BookingStatus.Confirmed);
        booking.TransitionTo(BookingStatus.AwaitingFulfilment);
        // No AssignProvider call - AssignedProviderId stays null.
        context.Add(booking);
        context.SaveChanges();

        var act = async () => await BuildHandler(context).Handle(new DomainEventNotification<BookingStatusChangedEvent>(
            new BookingStatusChangedEvent(booking.Id, BookingStatus.AwaitingFulfilment, BookingStatus.Completed)), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
