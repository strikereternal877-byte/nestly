using FluentAssertions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Referral;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers task 168: the customer-facing Refer &amp; Earn summary and history.</summary>
public sealed class ReferralCustomerServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public ReferralCustomerServiceTests(TestDatabase db) => _db = db;

    private static ReferralCustomerService BuildService(Nestly.Infrastructure.Persistence.NestlyDbContext context) => new(
        new ReferralRepository(context),
        new ReferralCodeService(new CustomerRepository(context), Options.Create(new ReferralOptions())),
        new CustomerRepository(context));

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
            299m, 30, maxReferralsPerCustomer: null, isActive: true);
        context.Add(config);
        context.SaveChanges();
        return config;
    }

    [Fact]
    public async Task GetSummaryAsync_generates_a_code_and_reports_zero_stats_for_a_new_referrer()
    {
        using var context = _db.CreateContext();
        var referrer = SeedCustomer(context, "NewReferrer");

        var summary = await BuildService(context).GetSummaryAsync(referrer.Id);

        summary.ReferralCode.Should().HaveLength(8);
        summary.ShareLink.Should().Contain(summary.ReferralCode);
        summary.InvitedCount.Should().Be(0);
        summary.QualifiedCount.Should().Be(0);
        summary.RewardedCount.Should().Be(0);
        summary.TotalEarned.Should().Be(0m);
    }

    [Fact]
    public async Task GetSummaryAsync_counts_referrals_by_stage()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context);
        var referrer = SeedCustomer(context, "Referrer");

        var registeredOnly = SeedCustomer(context, "RegisteredOnly");
        context.Add(new Domain.Referral(Guid.NewGuid(), referrer.Id, registeredOnly.Id, "CODE1", config));

        var qualified = SeedCustomer(context, "Qualified");
        var qualifiedReferral = new Domain.Referral(Guid.NewGuid(), referrer.Id, qualified.Id, "CODE2", config);
        qualifiedReferral.MarkQualified(Guid.NewGuid());
        context.Add(qualifiedReferral);

        var rewarded = SeedCustomer(context, "Rewarded");
        var rewardedReferral = new Domain.Referral(Guid.NewGuid(), referrer.Id, rewarded.Id, "CODE3", config);
        rewardedReferral.MarkQualified(Guid.NewGuid());
        rewardedReferral.MarkRewarded(Guid.NewGuid(), null, Guid.NewGuid(), null);
        context.Add(rewardedReferral);

        context.SaveChanges();

        var summary = await BuildService(context).GetSummaryAsync(referrer.Id);

        summary.InvitedCount.Should().Be(3);
        summary.QualifiedCount.Should().Be(2, "Qualified and Rewarded both count as having qualified");
        summary.RewardedCount.Should().Be(1);
        summary.TotalEarned.Should().Be(100m, "only the Rewarded referral actually paid out the referrer's 100 reward");
    }

    [Fact]
    public async Task GetHistoryAsync_lists_referrals_newest_first_with_referee_names()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context);
        var referrer = SeedCustomer(context, "Referrer");
        var referee = SeedCustomer(context, "Alice");
        context.Add(new Domain.Referral(Guid.NewGuid(), referrer.Id, referee.Id, "CODE1", config));
        context.SaveChanges();

        var history = await BuildService(context).GetHistoryAsync(referrer.Id);

        history.Should().ContainSingle();
        history.Single().RefereeName.Should().Be("Alice");
        history.Single().Status.Should().Be(nameof(ReferralStatus.Registered));
        history.Single().RewardEarned.Should().BeNull("not yet Rewarded");
    }
}
