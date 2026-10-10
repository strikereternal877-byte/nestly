using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nestly.Application;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Persistence.Interceptors;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers task 166: fraud flag/approve/reject and the post-reward-cancellation auto-flag signal.</summary>
public sealed class ReferralFraudReviewTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public ReferralFraudReviewTests(TestDatabase db) => _db = db;

    private static ReferralFraudReviewService BuildFraudService(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(new ReferralRepository(context));

    private static Customer SeedCustomer(Nestly.Infrastructure.Persistence.NestlyDbContext context, string name)
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], name, CustomerStatus.Active);
        context.Add(customer);
        context.SaveChanges();
        return customer;
    }

    private static ReferralProgramConfig SeedConfig(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        context.RemoveRange(context.ReferralProgramConfigs);
        context.SaveChanges();
        var config = new ReferralProgramConfig(
            Guid.NewGuid(), ReferralRewardType.WalletCredit, 100m, ReferralRewardType.WalletCredit, 50m,
            299m, 30, null, isActive: true);
        context.Add(config);
        context.SaveChanges();
        return config;
    }

    private static Domain.Referral SeedRewardedReferral(
        Nestly.Infrastructure.Persistence.NestlyDbContext context, Customer referrer, Customer referee, ReferralProgramConfig config, Guid qualifyingBookingId)
    {
        var referral = new Domain.Referral(Guid.NewGuid(), referrer.Id, referee.Id, "TESTCODE", config);
        referral.MarkQualified(qualifyingBookingId);
        referral.MarkRewarded(Guid.NewGuid(), null, Guid.NewGuid(), null);
        context.Add(referral);
        context.SaveChanges();
        return referral;
    }

    [Fact]
    public async Task FlagAsync_flags_a_referral_without_changing_its_status()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context);
        var referrer = SeedCustomer(context, "Referrer");
        var referee = SeedCustomer(context, "Referee");
        var referral = SeedRewardedReferral(context, referrer, referee, config, Guid.NewGuid());
        var adminId = Guid.NewGuid();

        var result = await BuildFraudService(context).FlagAsync(referral.Id, adminId, "Looks suspicious");

        result.IsSuccess.Should().BeTrue();
        var updated = context.Referrals.Single(r => r.Id == referral.Id);
        updated.IsFraudFlagged.Should().BeTrue();
        updated.Status.Should().Be(ReferralStatus.Rewarded, "flagging is independent of the reward status");
        updated.FraudReviewedByAdminUserId.Should().Be(adminId);
    }

    [Fact]
    public async Task ApproveAsync_clears_the_flag_and_leaves_the_reward_untouched()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context);
        var referrer = SeedCustomer(context, "Referrer");
        var referee = SeedCustomer(context, "Referee");
        var referral = SeedRewardedReferral(context, referrer, referee, config, Guid.NewGuid());
        var fraudService = BuildFraudService(context);
        await fraudService.FlagAsync(referral.Id, Guid.NewGuid(), "flagged");

        var result = await fraudService.ApproveAsync(referral.Id, Guid.NewGuid(), "Confirmed abuse pattern");

        result.IsSuccess.Should().BeTrue();
        var updated = context.Referrals.Single(r => r.Id == referral.Id);
        updated.IsFraudFlagged.Should().BeFalse();
        updated.Status.Should().Be(ReferralStatus.Rewarded, "approving a flag records the finding, it never auto-reverses the wallet credit");
        updated.ReferrerWalletEntryId.Should().NotBeNull();
    }

    [Fact]
    public async Task RejectAsync_clears_the_flag_as_a_false_positive()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context);
        var referrer = SeedCustomer(context, "Referrer");
        var referee = SeedCustomer(context, "Referee");
        var referral = SeedRewardedReferral(context, referrer, referee, config, Guid.NewGuid());
        var fraudService = BuildFraudService(context);
        await fraudService.FlagAsync(referral.Id, Guid.NewGuid(), "flagged");

        var result = await fraudService.RejectAsync(referral.Id, Guid.NewGuid(), "Household sharing a device, legitimate");

        result.IsSuccess.Should().BeTrue();
        context.Referrals.Single(r => r.Id == referral.Id).IsFraudFlagged.Should().BeFalse();
    }

    [Fact]
    public async Task ApproveAsync_fails_when_the_referral_is_not_currently_flagged()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context);
        var referrer = SeedCustomer(context, "Referrer");
        var referee = SeedCustomer(context, "Referee");
        var referral = SeedRewardedReferral(context, referrer, referee, config, Guid.NewGuid());

        var result = await BuildFraudService(context).ApproveAsync(referral.Id, Guid.NewGuid(), null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Referral.NotFlagged");
    }

    [Fact]
    public async Task CancellationSignalHandler_auto_flags_a_referral_cancelled_shortly_after_reward()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context);
        var referrer = SeedCustomer(context, "Referrer");
        var referee = SeedCustomer(context, "Referee");
        var bookingId = Guid.NewGuid();
        var referral = SeedRewardedReferral(context, referrer, referee, config, bookingId);

        var handler = new ReferralCancellationFraudSignalHandler(
            new ReferralRepository(context), BuildFraudService(context), TimeProvider.System,
            NullLogger<ReferralCancellationFraudSignalHandler>.Instance);

        await handler.Handle(
            new DomainEventNotification<BookingStatusChangedEvent>(
                new BookingStatusChangedEvent(bookingId, BookingStatus.Completed, BookingStatus.CancelledByCustomer)),
            CancellationToken.None);

        var updated = context.Referrals.Single(r => r.Id == referral.Id);
        updated.IsFraudFlagged.Should().BeTrue();
        updated.FraudReviewedByAdminUserId.Should().BeNull("this is a system-detected signal, not an admin action");
    }

    /// <summary>
    /// Regression coverage for the referral clawback gap: the handler used
    /// to only ever listen for CancelledByCustomer/CancelledByAdmin, but
    /// BookingLifecycle allows a Completed booking (which is the only status
    /// ReferralQualifyingBookingHandler ever disburses a reward against)
    /// exactly one way out - Completed -> RefundPending -> Refunded, never
    /// back to either Cancelled status. That made the one signal this
    /// handler exists to raise structurally unreachable for a dispute or
    /// admin refund reversing an already-rewarded referral's qualifying
    /// order - exactly the case REFERRAL.md's fraud section describes.
    /// </summary>
    [Fact]
    public async Task CancellationSignalHandler_auto_flags_a_referral_whose_qualifying_booking_is_refunded_after_completion()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context);
        var referrer = SeedCustomer(context, "Referrer");
        var referee = SeedCustomer(context, "Referee");
        var bookingId = Guid.NewGuid();
        var referral = SeedRewardedReferral(context, referrer, referee, config, bookingId);

        var handler = new ReferralCancellationFraudSignalHandler(
            new ReferralRepository(context), BuildFraudService(context), TimeProvider.System,
            NullLogger<ReferralCancellationFraudSignalHandler>.Instance);

        // The real transition pair RefundService.InitiateAsync produces when
        // a full refund drains a Completed booking's remaining balance (see
        // CommissionAndEscrowTests' own coverage of that transition) -
        // Completed -> RefundPending happens first, then this one.
        await handler.Handle(
            new DomainEventNotification<BookingStatusChangedEvent>(
                new BookingStatusChangedEvent(bookingId, BookingStatus.RefundPending, BookingStatus.Refunded)),
            CancellationToken.None);

        var updated = context.Referrals.Single(r => r.Id == referral.Id);
        updated.IsFraudFlagged.Should().BeTrue();
        updated.FraudReviewedByAdminUserId.Should().BeNull("this is a system-detected signal, not an admin action");
    }

    [Fact]
    public async Task CancellationSignalHandler_ignores_a_booking_that_is_not_a_referrals_qualifying_booking()
    {
        using var context = _db.CreateContext();
        var handler = new ReferralCancellationFraudSignalHandler(
            new ReferralRepository(context), BuildFraudService(context), TimeProvider.System,
            NullLogger<ReferralCancellationFraudSignalHandler>.Instance);

        var act = async () => await handler.Handle(
            new DomainEventNotification<BookingStatusChangedEvent>(
                new BookingStatusChangedEvent(Guid.NewGuid(), BookingStatus.Completed, BookingStatus.CancelledByCustomer)),
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
