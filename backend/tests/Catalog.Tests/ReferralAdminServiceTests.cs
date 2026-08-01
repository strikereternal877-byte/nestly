using FluentAssertions;
using Nestly.Application;
using Nestly.Application.Referral;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers task 170's admin list/detail and task 171's funnel/cost reports.</summary>
public sealed class ReferralAdminServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public ReferralAdminServiceTests(TestDatabase db) => _db = db;

    private static ReferralAdminService BuildService(Nestly.Infrastructure.Persistence.NestlyDbContext context) => new(
        new ReferralRepository(context),
        new ReferralMilestoneRepository(context),
        new ReferralMilestoneAwardRepository(context),
        new CustomerRepository(context));

    private static Customer SeedCustomer(Nestly.Infrastructure.Persistence.NestlyDbContext context, string name, string? mobile = null)
    {
        var customer = new Customer(Guid.NewGuid(), mobile ?? "9" + Guid.NewGuid().ToString("N")[..9], name, CustomerStatus.Active);
        context.Add(customer);
        context.SaveChanges();
        return customer;
    }

    private static ReferralProgramConfig BuildConfig(
        ReferralRewardType referrerType = ReferralRewardType.WalletCredit, ReferralRewardType refereeType = ReferralRewardType.WalletCredit) =>
        new(Guid.NewGuid(), referrerType, 100m, refereeType, 50m, 299m, 30, maxReferralsPerCustomer: null, isActive: true);

    [Fact]
    public async Task SearchAsync_filters_by_status()
    {
        using var context = _db.CreateContext();
        var config = BuildConfig();
        var referrer = SeedCustomer(context, "Referrer1");

        var registeredReferee = SeedCustomer(context, "RegisteredReferee");
        context.Add(new Domain.Referral(Guid.NewGuid(), referrer.Id, registeredReferee.Id, "C1", config));

        var qualifiedReferee = SeedCustomer(context, "QualifiedReferee");
        var qualified = new Domain.Referral(Guid.NewGuid(), referrer.Id, qualifiedReferee.Id, "C2", config);
        qualified.MarkQualified(Guid.NewGuid());
        context.Add(qualified);
        context.SaveChanges();

        var result = await BuildService(context).SearchAsync(new ReferralAdminSearchRequest(ReferralStatus.Qualified, null, null));

        result.Items.Should().ContainSingle();
        result.Items.Single().RefereeName.Should().Be("QualifiedReferee");
        result.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task SearchAsync_filters_by_customer_search_term_across_referrer_and_referee()
    {
        using var context = _db.CreateContext();
        var config = BuildConfig();
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var referrer = SeedCustomer(context, $"UniqueReferrer{uniqueSuffix}");
        var referee = SeedCustomer(context, "SomeReferee");
        context.Add(new Domain.Referral(Guid.NewGuid(), referrer.Id, referee.Id, "C1", config));

        var otherReferrer = SeedCustomer(context, "OtherReferrer");
        var otherReferee = SeedCustomer(context, "OtherReferee");
        context.Add(new Domain.Referral(Guid.NewGuid(), otherReferrer.Id, otherReferee.Id, "C2", config));
        context.SaveChanges();

        var result = await BuildService(context).SearchAsync(new ReferralAdminSearchRequest(null, null, $"UniqueReferrer{uniqueSuffix}"));

        result.Items.Should().ContainSingle();
        result.Items.Single().ReferrerName.Should().Be($"UniqueReferrer{uniqueSuffix}");
    }

    [Fact]
    public async Task SearchAsync_with_a_non_matching_customer_search_returns_no_results()
    {
        using var context = _db.CreateContext();
        var result = await BuildService(context).SearchAsync(
            new ReferralAdminSearchRequest(null, null, "no-such-customer-" + Guid.NewGuid()));

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task GetByIdAsync_returns_full_detail_and_NotFound_for_unknown_id()
    {
        using var context = _db.CreateContext();
        var config = BuildConfig();
        var referrer = SeedCustomer(context, "DetailReferrer");
        var referee = SeedCustomer(context, "DetailReferee");
        var referral = new Domain.Referral(Guid.NewGuid(), referrer.Id, referee.Id, "DETAILCODE", config);
        context.Add(referral);
        context.SaveChanges();

        var found = await BuildService(context).GetByIdAsync(referral.Id);
        found.IsSuccess.Should().BeTrue();
        found.Value.ReferrerName.Should().Be("DetailReferrer");
        found.Value.RefereeName.Should().Be("DetailReferee");
        found.Value.ReferralCodeUsed.Should().Be("DETAILCODE");

        var missing = await BuildService(context).GetByIdAsync(Guid.NewGuid());
        missing.IsSuccess.Should().BeFalse();
        missing.Error.Code.Should().Be("Referral.NotFound");
    }

    [Fact]
    public async Task GetFunnelReportAsync_counts_the_cohort_that_registered_in_range()
    {
        using var context = _db.CreateContext();
        var config = BuildConfig();
        var referrer = SeedCustomer(context, "FunnelReferrer");

        var fromUtc = DateTime.UtcNow.AddDays(-1);
        var toUtc = DateTime.UtcNow.AddDays(1);

        var registeredReferee = SeedCustomer(context, "FunnelRegistered");
        context.Add(new Domain.Referral(Guid.NewGuid(), referrer.Id, registeredReferee.Id, "F1", config));

        var rewardedReferee = SeedCustomer(context, "FunnelRewarded");
        var rewarded = new Domain.Referral(Guid.NewGuid(), referrer.Id, rewardedReferee.Id, "F2", config);
        rewarded.MarkQualified(Guid.NewGuid());
        rewarded.MarkRewarded(Guid.NewGuid(), null, Guid.NewGuid(), null);
        context.Add(rewarded);
        context.SaveChanges();

        var result = await BuildService(context).GetFunnelReportAsync(fromUtc, toUtc);

        result.IsSuccess.Should().BeTrue();
        result.Value.RegisteredCount.Should().BeGreaterThanOrEqualTo(2);
        result.Value.InvitedCount.Should().Be(result.Value.RegisteredCount);
        result.Value.RewardedCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task GetFunnelReportAsync_rejects_an_inverted_date_range()
    {
        using var context = _db.CreateContext();
        var result = await BuildService(context).GetFunnelReportAsync(DateTime.UtcNow, DateTime.UtcNow.AddDays(-5));

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("ReferralReport.InvalidRange");
    }

    [Fact]
    public async Task GetCostReportAsync_sums_only_actually_disbursed_rewards_split_by_wallet_and_coupon()
    {
        using var context = _db.CreateContext();
        var referrer = SeedCustomer(context, "CostReferrer");
        var fromUtc = DateTime.UtcNow.AddDays(-1);
        var toUtc = DateTime.UtcNow.AddDays(1);

        // Wallet-credit reward, fully disbursed both sides.
        var walletConfig = BuildConfig();
        var walletReferee = SeedCustomer(context, "WalletReferee");
        var walletReferral = new Domain.Referral(Guid.NewGuid(), referrer.Id, walletReferee.Id, "W1", walletConfig);
        walletReferral.MarkQualified(Guid.NewGuid());
        walletReferral.MarkRewarded(Guid.NewGuid(), null, Guid.NewGuid(), null);
        context.Add(walletReferral);

        // Coupon reward.
        var couponConfig = BuildConfig(ReferralRewardType.Coupon, ReferralRewardType.Coupon);
        var couponReferee = SeedCustomer(context, "CouponReferee");
        var couponReferral = new Domain.Referral(Guid.NewGuid(), referrer.Id, couponReferee.Id, "C1", couponConfig);
        couponReferral.MarkQualified(Guid.NewGuid());
        couponReferral.MarkRewarded(null, Guid.NewGuid(), null, Guid.NewGuid());
        context.Add(couponReferral);

        // Capped referrer side: referrer reward skipped (both ids null), referee still gets wallet credit.
        var cappedConfig = BuildConfig();
        var cappedReferee = SeedCustomer(context, "CappedReferee");
        var cappedReferral = new Domain.Referral(Guid.NewGuid(), referrer.Id, cappedReferee.Id, "CAP1", cappedConfig);
        cappedReferral.MarkQualified(Guid.NewGuid());
        cappedReferral.MarkRewarded(null, null, Guid.NewGuid(), null);
        context.Add(cappedReferral);

        context.SaveChanges();

        var result = await BuildService(context).GetCostReportAsync(fromUtc, toUtc);

        result.IsSuccess.Should().BeTrue();
        // Wallet: referral1 both sides (100+50) + capped referral's referee-only (50) = 200.
        result.Value.TotalWalletCreditCost.Should().Be(200m);
        // Coupon: referral2 both sides (100+50) = 150.
        result.Value.TotalCouponCost.Should().Be(150m);
        result.Value.TotalCost.Should().Be(350m);
        result.Value.RewardedReferralCount.Should().Be(3);
    }
}
