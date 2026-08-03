using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nestly.Application;
using Nestly.Domain;
using Nestly.Domain.NestlyCoins;
using Nestly.Infrastructure.Persistence.Interceptors;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers task 201: Nestly Coins earn/credit/clawback against the booking-completion and cancellation paths.</summary>
public sealed class NestlyCoinsServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public NestlyCoinsServiceTests(TestDatabase db) => _db = db;

    private static NestlyCoinsService BuildService(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(
            new NestlyCoinsProgramConfigRepository(context),
            new BookingRepository(context),
            new WalletService(new WalletLedgerRepository(context)),
            new WalletLedgerRepository(context),
            new ProviderEarningLedgerService(new ProviderRepository(context), new ProviderEarningLedgerRepository(context)),
            new ProviderEarningLedgerRepository(context),
            NullLogger<NestlyCoinsService>.Instance);

    private static NestlyCoinsQualifyingOrderHandler BuildQualifyingHandler(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(new BookingRepository(context), BuildService(context));

    private static NestlyCoinsClawbackHandler BuildClawbackHandler(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(BuildService(context));

    private static Customer SeedCustomer(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Coins Customer", CustomerStatus.Active,
            $"coins-{Guid.NewGuid():N}@example.com");
        context.Add(customer);
        context.SaveChanges();
        return customer;
    }

    private static Provider SeedProvider(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        var provider = new Provider(Guid.NewGuid(), "Coins Provider Pvt Ltd", "Coins Provider", ProviderType.Individual, "9" + Guid.NewGuid().ToString("N")[..9]);
        context.Add(provider);
        context.SaveChanges();
        return provider;
    }

    private static NestlyCoinsProgramConfig SeedConfig(
        Nestly.Infrastructure.Persistence.NestlyDbContext context,
        NestlyCoinsAudience audience,
        decimal earnRatePer100 = 10m,
        decimal minimumOrderAmount = 200m,
        bool requireReorder = true,
        decimal? maxCoinsPerMonth = null,
        int expiryDays = 30,
        int clawbackWindowDays = 3,
        bool isActive = true)
    {
        // One row per audience is a real invariant (unique index) - clear
        // any existing row for this audience first so re-running a test
        // against the shared TestDatabase fixture never collides.
        var existing = context.Set<NestlyCoinsProgramConfig>().Where(c => c.Audience == audience);
        context.RemoveRange(existing);
        context.SaveChanges();

        var config = new NestlyCoinsProgramConfig(
            Guid.NewGuid(), audience, earnRatePer100, minimumOrderAmount, requireReorder,
            maxCoinsPerMonth, expiryDays, clawbackWindowDays, isActive);
        context.Add(config);
        context.SaveChanges();
        return config;
    }

    /// <summary>Builds a Completed booking directly via the domain constructor + TransitionTo chain, same as ReferralQualificationAndRewardTests - the full BookingService orchestration is out of scope for what this test needs.</summary>
    private static Booking SeedCompletedBooking(Nestly.Infrastructure.Persistence.NestlyDbContext context, Guid customerId, decimal totalPayable, Guid? assignedProviderId = null)
    {
        var booking = new Booking(
            Guid.NewGuid(), customerId,
            new CustomerSnapshot("Coins Customer", "9" + Guid.NewGuid().ToString("N")[..9]),
            null,
            new AddressSnapshot("Home", "123 St", null, null, "560001", "Bengaluru", "Karnataka", 12.9m, 77.5m, "Test", "9000000000"),
            new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
            new PriceSnapshot(totalPayable, 1, totalPayable, 0, 0, totalPayable, 0, 0, 0, totalPayable));

        if (assignedProviderId is Guid providerId)
        {
            booking.AssignProvider(providerId);
        }

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

    private static DomainEventNotification<Nestly.Domain.Events.BookingStatusChangedEvent> Notification(Guid bookingId, BookingStatus from, BookingStatus to) =>
        new(new Nestly.Domain.Events.BookingStatusChangedEvent(bookingId, from, to));

    [Fact]
    public void EvaluateQualifyingOrder_fails_when_config_is_inactive()
    {
        var config = new NestlyCoinsProgramConfig(Guid.NewGuid(), NestlyCoinsAudience.Customer, 10m, 200m, requireReorder: false, null, 30, 3, isActive: false);
        var service = new NestlyCoinsService(null!, null!, null!, null!, null!, null!, NullLogger<NestlyCoinsService>.Instance);

        service.EvaluateQualifyingOrder(config, orderAmount: 500m, priorCompletedCount: 5, creditedThisMonth: 0m).Should().BeFalse();
    }

    [Fact]
    public void EvaluateQualifyingOrder_fails_below_the_minimum_order_amount()
    {
        var config = new NestlyCoinsProgramConfig(Guid.NewGuid(), NestlyCoinsAudience.Customer, 10m, 200m, requireReorder: false, null, 30, 3, isActive: true);
        var service = new NestlyCoinsService(null!, null!, null!, null!, null!, null!, NullLogger<NestlyCoinsService>.Instance);

        service.EvaluateQualifyingOrder(config, orderAmount: 199m, priorCompletedCount: 5, creditedThisMonth: 0m).Should().BeFalse();
    }

    [Fact]
    public void EvaluateQualifyingOrder_fails_a_first_order_when_reorder_is_required()
    {
        var config = new NestlyCoinsProgramConfig(Guid.NewGuid(), NestlyCoinsAudience.Customer, 10m, 200m, requireReorder: true, null, 30, 3, isActive: true);
        var service = new NestlyCoinsService(null!, null!, null!, null!, null!, null!, NullLogger<NestlyCoinsService>.Instance);

        service.EvaluateQualifyingOrder(config, orderAmount: 500m, priorCompletedCount: 0, creditedThisMonth: 0m).Should().BeFalse();
        service.EvaluateQualifyingOrder(config, orderAmount: 500m, priorCompletedCount: 1, creditedThisMonth: 0m).Should().BeTrue();
    }

    [Fact]
    public void EvaluateQualifyingOrder_fails_once_the_monthly_cap_would_be_exceeded()
    {
        var config = new NestlyCoinsProgramConfig(Guid.NewGuid(), NestlyCoinsAudience.Customer, 10m, 0m, requireReorder: false, maxCoinsPerMonth: 40m, 30, 3, isActive: true);
        var service = new NestlyCoinsService(null!, null!, null!, null!, null!, null!, NullLogger<NestlyCoinsService>.Instance);

        // 500 * 10/100 = 50 earned, already credited 0 this month -> 50 > 40 cap, fails.
        service.EvaluateQualifyingOrder(config, orderAmount: 500m, priorCompletedCount: 0, creditedThisMonth: 0m).Should().BeFalse();
        // Already credited 35 this month, earning 5 more (50 order) keeps it at/under the 40 cap.
        service.EvaluateQualifyingOrder(config, orderAmount: 50m, priorCompletedCount: 0, creditedThisMonth: 35m).Should().BeTrue();
    }

    [Fact]
    public async Task CreditCustomerCoinsAsync_credits_the_wallet_with_an_expiring_credit_on_a_qualifying_reorder()
    {
        using var context = _db.CreateContext();
        SeedConfig(context, NestlyCoinsAudience.Customer, earnRatePer100: 10m, minimumOrderAmount: 200m, requireReorder: true, expiryDays: 30);
        var customer = SeedCustomer(context);

        SeedCompletedBooking(context, customer.Id, totalPayable: 500m); // first order - establishes reorder history
        var secondBooking = SeedCompletedBooking(context, customer.Id, totalPayable: 1000m);

        await BuildQualifyingHandler(context).Handle(Notification(secondBooking.Id, BookingStatus.InProgress, BookingStatus.Completed), CancellationToken.None);

        var entry = context.WalletLedgerEntries.Single(e => e.SourceType == WalletSourceType.NestlyCoinsReward && e.SourceReferenceId == secondBooking.Id);
        entry.Amount.Should().Be(100m); // 1000 * 10/100
        entry.CustomerId.Should().Be(customer.Id);
        entry.ExpiresAtUtc.Should().NotBeNull();
        entry.RemainingAmount.Should().Be(100m);
    }

    [Fact]
    public async Task CreditCustomerCoinsAsync_does_not_credit_a_customers_first_order_when_reorder_is_required()
    {
        using var context = _db.CreateContext();
        SeedConfig(context, NestlyCoinsAudience.Customer, requireReorder: true);
        var customer = SeedCustomer(context);
        var booking = SeedCompletedBooking(context, customer.Id, totalPayable: 500m);

        await BuildQualifyingHandler(context).Handle(Notification(booking.Id, BookingStatus.InProgress, BookingStatus.Completed), CancellationToken.None);

        context.WalletLedgerEntries.Any(e => e.SourceReferenceId == booking.Id && e.SourceType == WalletSourceType.NestlyCoinsReward).Should().BeFalse();
    }

    [Fact]
    public async Task CreditCustomerCoinsAsync_is_a_no_op_when_the_customer_audience_has_no_config()
    {
        using var context = _db.CreateContext();
        // No SeedConfig call at all - GetByAudienceAsync returns null.
        var configs = context.Set<NestlyCoinsProgramConfig>().Where(c => c.Audience == NestlyCoinsAudience.Customer);
        context.RemoveRange(configs);
        context.SaveChanges();

        var customer = SeedCustomer(context);
        var booking = SeedCompletedBooking(context, customer.Id, totalPayable: 500m);

        var act = async () => await BuildQualifyingHandler(context).Handle(Notification(booking.Id, BookingStatus.InProgress, BookingStatus.Completed), CancellationToken.None);

        await act.Should().NotThrowAsync();
        context.WalletLedgerEntries.Any(e => e.SourceReferenceId == booking.Id && e.SourceType == WalletSourceType.NestlyCoinsReward).Should().BeFalse();
    }

    [Fact]
    public async Task CreditProviderCoinsAsync_credits_the_earning_ledger_for_the_assigned_provider()
    {
        using var context = _db.CreateContext();
        SeedConfig(context, NestlyCoinsAudience.Provider, earnRatePer100: 5m, minimumOrderAmount: 200m, requireReorder: true);
        var customer = SeedCustomer(context);
        var provider = SeedProvider(context);

        SeedCompletedBooking(context, customer.Id, totalPayable: 500m, assignedProviderId: provider.Id);
        var secondBooking = SeedCompletedBooking(context, customer.Id, totalPayable: 1000m, assignedProviderId: provider.Id);

        await BuildQualifyingHandler(context).Handle(Notification(secondBooking.Id, BookingStatus.InProgress, BookingStatus.Completed), CancellationToken.None);

        var entry = context.Set<ProviderEarningLedgerEntry>().Single(e => e.SourceType == ProviderEarningSourceType.NestlyCoinsReward && e.SourceReferenceId == secondBooking.Id);
        entry.Amount.Should().Be(50m); // 1000 * 5/100
        entry.ProviderId.Should().Be(provider.Id);
    }

    [Fact]
    public async Task CreditProviderCoinsAsync_is_a_no_op_when_no_provider_is_assigned()
    {
        using var context = _db.CreateContext();
        SeedConfig(context, NestlyCoinsAudience.Provider, requireReorder: false);
        var customer = SeedCustomer(context);
        var booking = SeedCompletedBooking(context, customer.Id, totalPayable: 500m, assignedProviderId: null);

        var act = async () => await BuildQualifyingHandler(context).Handle(Notification(booking.Id, BookingStatus.InProgress, BookingStatus.Completed), CancellationToken.None);

        await act.Should().NotThrowAsync();
        context.Set<ProviderEarningLedgerEntry>().Any(e => e.SourceReferenceId == booking.Id && e.SourceType == ProviderEarningSourceType.NestlyCoinsReward).Should().BeFalse();
    }

    [Fact]
    public async Task ClawbackOnCancellationAsync_reverses_the_customer_credit_within_the_window()
    {
        Guid bookingId, customerId;

        using (var seedContext = _db.CreateContext())
        {
            SeedConfig(seedContext, NestlyCoinsAudience.Customer, requireReorder: false, clawbackWindowDays: 3);
            var customer = SeedCustomer(seedContext);
            var booking = SeedCompletedBooking(seedContext, customer.Id, totalPayable: 500m);
            customerId = customer.Id;
            bookingId = booking.Id;

            await BuildQualifyingHandler(seedContext).Handle(Notification(booking.Id, BookingStatus.InProgress, BookingStatus.Completed), CancellationToken.None);
            seedContext.WalletLedgerEntries.Single(e => e.SourceReferenceId == bookingId && e.SourceType == WalletSourceType.NestlyCoinsReward).Amount.Should().Be(50m);
        }

        // Fresh context per phase (mirrors RefundServiceTests' seed/order/cancel-context
        // split) - a booking loaded, transitioned and saved within its own context scope,
        // rather than one long-lived context juggling several unrelated SaveChanges rounds
        // against the same tracked owned-collection graph.
        using (var refundContext = _db.CreateContext())
        {
            // BookingLifecycle only allows Completed -> RefundPending -> Refunded
            // (never a direct Completed -> CancelledBy*), so a real post-completion
            // reversal is always a refund, not a cancellation.
            var booking = refundContext.Bookings.Include(b => b.StatusHistory).Single(b => b.Id == bookingId);
            booking.TransitionTo(BookingStatus.RefundPending);
            booking.TransitionTo(BookingStatus.Refunded);
            await refundContext.SaveChangesAsync();
        }

        using (var clawbackContext = _db.CreateContext())
        {
            await BuildClawbackHandler(clawbackContext).Handle(Notification(bookingId, BookingStatus.RefundPending, BookingStatus.Refunded), CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        var clawback = readContext.WalletLedgerEntries.Single(e => e.SourceType == WalletSourceType.NestlyCoinsClawback && e.SourceReferenceId == bookingId);
        clawback.Amount.Should().Be(50m);

        var latest = readContext.WalletLedgerEntries.Where(e => e.CustomerId == customerId).OrderByDescending(e => e.CreatedAtUtc).First();
        latest.BalanceAfter.Should().Be(0m);
    }

    [Fact]
    public async Task ClawbackOnCancellationAsync_is_a_no_op_for_a_booking_that_was_never_credited()
    {
        Guid bookingId;

        using (var seedContext = _db.CreateContext())
        {
            SeedConfig(seedContext, NestlyCoinsAudience.Customer, requireReorder: true);
            var customer = SeedCustomer(seedContext);
            // First order - never credited (RequireReorder).
            var booking = SeedCompletedBooking(seedContext, customer.Id, totalPayable: 500m);
            bookingId = booking.Id;
        }

        using (var refundContext = _db.CreateContext())
        {
            var booking = refundContext.Bookings.Include(b => b.StatusHistory).Single(b => b.Id == bookingId);
            booking.TransitionTo(BookingStatus.RefundPending);
            booking.TransitionTo(BookingStatus.Refunded);
            await refundContext.SaveChangesAsync();
        }

        using var clawbackContext = _db.CreateContext();
        var act = async () => await BuildClawbackHandler(clawbackContext).Handle(Notification(bookingId, BookingStatus.RefundPending, BookingStatus.Refunded), CancellationToken.None);

        await act.Should().NotThrowAsync();
        clawbackContext.WalletLedgerEntries.Any(e => e.SourceReferenceId == bookingId && e.SourceType == WalletSourceType.NestlyCoinsClawback).Should().BeFalse();
    }
}
