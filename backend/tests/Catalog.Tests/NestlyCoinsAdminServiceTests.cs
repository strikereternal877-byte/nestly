using FluentAssertions;
using Nestly.Application;
using Nestly.Application.NestlyCoins;
using Nestly.Domain;
using Nestly.Domain.NestlyCoins;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers task 202: Nestly Coins admin config get/upsert and the issued/clawed-back report.</summary>
public sealed class NestlyCoinsAdminServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public NestlyCoinsAdminServiceTests(TestDatabase db) => _db = db;

    private static NestlyCoinsAdminService BuildService(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(
            new NestlyCoinsProgramConfigRepository(context),
            new WalletLedgerRepository(context),
            new ProviderEarningLedgerRepository(context));

    private static void ClearConfig(Nestly.Infrastructure.Persistence.NestlyDbContext context, NestlyCoinsAudience audience)
    {
        context.RemoveRange(context.Set<NestlyCoinsProgramConfig>().Where(c => c.Audience == audience));
        context.SaveChanges();
    }

    [Fact]
    public async Task GetAsync_returns_not_found_when_the_audience_has_never_been_configured()
    {
        using var context = _db.CreateContext();
        ClearConfig(context, NestlyCoinsAudience.Customer);

        var result = await BuildService(context).GetAsync(NestlyCoinsAudience.Customer);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("NestlyCoinsProgramConfig.NotFound");
    }

    [Fact]
    public async Task UpsertAsync_creates_the_row_when_the_audience_has_never_been_configured()
    {
        using var context = _db.CreateContext();
        ClearConfig(context, NestlyCoinsAudience.Provider);

        var request = new NestlyCoinsProgramConfigUpsertRequest(
            EarnRatePer100: 5m, MinimumOrderAmount: 300m, RequireReorder: true,
            MaxCoinsPerMonth: 200m, ExpiryDays: 60, ClawbackWindowDays: 5, IsActive: true);

        var result = await BuildService(context).UpsertAsync(NestlyCoinsAudience.Provider, request, Guid.NewGuid());

        result.IsSuccess.Should().BeTrue();
        result.Value.Audience.Should().Be(NestlyCoinsAudience.Provider);
        result.Value.EarnRatePer100.Should().Be(5m);

        context.Set<NestlyCoinsProgramConfig>().Count(c => c.Audience == NestlyCoinsAudience.Provider).Should().Be(1);
    }

    [Fact]
    public async Task UpsertAsync_updates_the_existing_row_rather_than_creating_a_second_one()
    {
        using var context = _db.CreateContext();
        ClearConfig(context, NestlyCoinsAudience.Customer);

        var adminUserId = Guid.NewGuid();
        var createRequest = new NestlyCoinsProgramConfigUpsertRequest(10m, 200m, true, null, 30, 3, true);
        await BuildService(context).UpsertAsync(NestlyCoinsAudience.Customer, createRequest, adminUserId);

        var updateRequest = new NestlyCoinsProgramConfigUpsertRequest(15m, 250m, false, 100m, 45, 7, true);
        var result = await BuildService(context).UpsertAsync(NestlyCoinsAudience.Customer, updateRequest, adminUserId);

        result.IsSuccess.Should().BeTrue();
        result.Value.EarnRatePer100.Should().Be(15m);
        result.Value.RequireReorder.Should().BeFalse();
        result.Value.UpdatedByAdminUserId.Should().Be(adminUserId);

        context.Set<NestlyCoinsProgramConfig>().Count(c => c.Audience == NestlyCoinsAudience.Customer).Should().Be(1);
    }

    [Fact]
    public async Task UpsertAsync_rejects_an_invalid_request()
    {
        using var context = _db.CreateContext();
        ClearConfig(context, NestlyCoinsAudience.Customer);

        var invalidRequest = new NestlyCoinsProgramConfigUpsertRequest(
            EarnRatePer100: -1m, MinimumOrderAmount: 200m, RequireReorder: true,
            MaxCoinsPerMonth: null, ExpiryDays: 30, ClawbackWindowDays: 3, IsActive: true);

        var result = await BuildService(context).UpsertAsync(NestlyCoinsAudience.Customer, invalidRequest, Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("NestlyCoinsProgramConfig.Invalid");
    }

    [Fact]
    public async Task GetReportAsync_sums_issued_and_clawed_back_wallet_credits_for_the_customer_audience()
    {
        using var context = _db.CreateContext();
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Report Customer", CustomerStatus.Active);
        context.Add(customer);
        context.SaveChanges();

        var wallet = new WalletService(new WalletLedgerRepository(context));
        var fromUtc = DateTime.UtcNow.AddDays(-1);
        var toUtc = DateTime.UtcNow.AddDays(1);

        // The report sums program-wide (every customer), and this class's
        // TestDatabase fixture is shared across test methods - baseline
        // before/after this test's own entries rather than asserting an
        // absolute total, so this can't be polluted by another test's
        // entries landing in the same wide date window.
        var before = await BuildService(context).GetReportAsync(NestlyCoinsAudience.Customer, fromUtc, toUtc);

        await wallet.CreditAsync(customer.Id, 50m, WalletSourceType.NestlyCoinsReward, Guid.NewGuid(), "issued");
        await wallet.CreditAsync(customer.Id, 30m, WalletSourceType.NestlyCoinsReward, Guid.NewGuid(), "issued");
        await wallet.DebitAsync(customer.Id, 20m, WalletSourceType.NestlyCoinsClawback, Guid.NewGuid(), "clawed back");

        var after = await BuildService(context).GetReportAsync(NestlyCoinsAudience.Customer, fromUtc, toUtc);

        (after.TotalIssued - before.TotalIssued).Should().Be(80m);
        (after.TotalClawedBack - before.TotalClawedBack).Should().Be(20m);
        (after.NetOutstanding - before.NetOutstanding).Should().Be(60m);
    }

    [Fact]
    public async Task GetReportAsync_excludes_entries_outside_the_requested_range()
    {
        using var context = _db.CreateContext();
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Report Customer Out Of Range", CustomerStatus.Active);
        context.Add(customer);
        context.SaveChanges();

        var wallet = new WalletService(new WalletLedgerRepository(context));
        await wallet.CreditAsync(customer.Id, 50m, WalletSourceType.NestlyCoinsReward, Guid.NewGuid(), "issued");

        // A range entirely in the future should see none of it.
        var report = await BuildService(context).GetReportAsync(
            NestlyCoinsAudience.Customer, DateTime.UtcNow.AddDays(10), DateTime.UtcNow.AddDays(20));

        report.TotalIssued.Should().Be(0m);
    }
}
