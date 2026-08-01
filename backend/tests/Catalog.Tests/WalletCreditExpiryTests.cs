using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nestly.Application;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Covers task 175's FIFO consumption-tracking design: <see cref="WalletService.DebitAsync"/>
/// draws down a customer's soonest-to-expire outstanding credits first
/// (<see cref="WalletLedgerEntry.RemainingAmount"/>), and <see cref="WalletCreditExpirySweepJob"/>
/// writes off whatever is left of a credit once its expiry passes unspent.
/// </summary>
public sealed class WalletCreditExpiryTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public WalletCreditExpiryTests(TestDatabase db) => _db = db;

    private static WalletService BuildWalletService(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(new WalletLedgerRepository(context));

    private static WalletCreditExpirySweepJob BuildSweepJob(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(new WalletLedgerRepository(context), BuildWalletService(context), NullLogger<WalletCreditExpirySweepJob>.Instance);

    private static Guid SeedCustomer(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Wallet Customer", CustomerStatus.Active);
        context.Add(customer);
        context.SaveChanges();
        return customer.Id;
    }

    [Fact]
    public async Task DebitAsync_consumes_the_soonest_to_expire_credit_first()
    {
        using var context = _db.CreateContext();
        var customerId = SeedCustomer(context);
        var service = BuildWalletService(context);

        // Two expiring credits, oldest-expiry first, plus a non-expiring one.
        var soonExpiry = await service.CreditAsync(
            customerId, 50m, WalletSourceType.ReferralReward, null, "Soon-expiring credit", DateTime.UtcNow.AddDays(5));
        var laterExpiry = await service.CreditAsync(
            customerId, 100m, WalletSourceType.ReferralReward, null, "Later-expiring credit", DateTime.UtcNow.AddDays(30));

        // Debit less than the soon-expiring credit alone - should draw only from it.
        await service.DebitAsync(customerId, 20m, WalletSourceType.ManualAdjustment, null, "Partial spend");

        using var context2 = _db.CreateContext();
        var repo = new WalletLedgerRepository(context2);
        var refreshedSoon = (await repo.ListByCustomerAsync(customerId)).Single(e => e.Id == soonExpiry.Id);
        var refreshedLater = (await repo.ListByCustomerAsync(customerId)).Single(e => e.Id == laterExpiry.Id);

        refreshedSoon.RemainingAmount.Should().Be(30m, "the soonest-to-expire credit is drawn down first");
        refreshedLater.RemainingAmount.Should().Be(100m, "the later-expiring credit is untouched while the soonest one still has balance");
    }

    [Fact]
    public async Task DebitAsync_spills_over_into_the_next_expiring_credit_once_the_first_is_exhausted()
    {
        using var context = _db.CreateContext();
        var customerId = SeedCustomer(context);
        var service = BuildWalletService(context);

        var soonExpiry = await service.CreditAsync(
            customerId, 50m, WalletSourceType.ReferralReward, null, "Soon-expiring credit", DateTime.UtcNow.AddDays(5));
        var laterExpiry = await service.CreditAsync(
            customerId, 100m, WalletSourceType.ReferralReward, null, "Later-expiring credit", DateTime.UtcNow.AddDays(30));

        // Debit more than the first credit alone - should exhaust it then spill into the second.
        await service.DebitAsync(customerId, 70m, WalletSourceType.ManualAdjustment, null, "Spend across two credits");

        using var context2 = _db.CreateContext();
        var repo = new WalletLedgerRepository(context2);
        var refreshedSoon = (await repo.ListByCustomerAsync(customerId)).Single(e => e.Id == soonExpiry.Id);
        var refreshedLater = (await repo.ListByCustomerAsync(customerId)).Single(e => e.Id == laterExpiry.Id);

        refreshedSoon.RemainingAmount.Should().Be(0m);
        refreshedLater.RemainingAmount.Should().Be(80m);
    }

    [Fact]
    public async Task SweepAsync_writes_off_the_unconsumed_portion_of_an_expired_credit()
    {
        using var context = _db.CreateContext();
        var customerId = SeedCustomer(context);
        var service = BuildWalletService(context);

        var expiredCredit = await service.CreditAsync(
            customerId, 100m, WalletSourceType.ReferralReward, null, "Already-expired credit", DateTime.UtcNow.AddDays(-1));

        await BuildSweepJob(context).SweepAsync();

        using var context2 = _db.CreateContext();
        var repo = new WalletLedgerRepository(context2);
        var refreshed = (await repo.ListByCustomerAsync(customerId)).Single(e => e.Id == expiredCredit.Id);
        refreshed.RemainingAmount.Should().Be(0m);

        var writeOffEntry = (await repo.ListByCustomerAsync(customerId))
            .Single(e => e.SourceType == WalletSourceType.ReferralCreditExpiry);
        writeOffEntry.EntryType.Should().Be(WalletEntryType.Debit);
        writeOffEntry.Amount.Should().Be(100m);

        var balance = await BuildWalletService(context2).GetBalanceAsync(customerId);
        balance.Value.Balance.Should().Be(0m);
    }

    [Fact]
    public async Task SweepAsync_only_writes_off_the_remaining_unspent_portion_not_the_full_original_credit()
    {
        using var context = _db.CreateContext();
        var customerId = SeedCustomer(context);
        var service = BuildWalletService(context);

        var expiredCredit = await service.CreditAsync(
            customerId, 100m, WalletSourceType.ReferralReward, null, "Partially spent then expired", DateTime.UtcNow.AddDays(-1));

        // Spend half of it while it's still (barely) valid from the consumption tracker's point of view.
        await service.DebitAsync(customerId, 40m, WalletSourceType.ManualAdjustment, null, "Partial spend before sweep");

        await BuildSweepJob(context).SweepAsync();

        using var context2 = _db.CreateContext();
        var repo = new WalletLedgerRepository(context2);
        var writeOffEntry = (await repo.ListByCustomerAsync(customerId))
            .Single(e => e.SourceType == WalletSourceType.ReferralCreditExpiry);
        writeOffEntry.Amount.Should().Be(60m, "only the unspent 60 of the original 100 should be written off");

        var balance = await BuildWalletService(context2).GetBalanceAsync(customerId);
        balance.Value.Balance.Should().Be(0m, "the spent 40 plus the written-off 60 accounts for the full original 100");
    }

    [Fact]
    public async Task SweepAsync_does_not_touch_a_credit_that_has_not_expired_yet()
    {
        using var context = _db.CreateContext();
        var customerId = SeedCustomer(context);
        var service = BuildWalletService(context);

        var futureCredit = await service.CreditAsync(
            customerId, 100m, WalletSourceType.ReferralReward, null, "Not yet expired", DateTime.UtcNow.AddDays(30));

        await BuildSweepJob(context).SweepAsync();

        using var context2 = _db.CreateContext();
        var repo = new WalletLedgerRepository(context2);
        var refreshed = (await repo.ListByCustomerAsync(customerId)).Single(e => e.Id == futureCredit.Id);
        refreshed.RemainingAmount.Should().Be(100m);

        var balance = await BuildWalletService(context2).GetBalanceAsync(customerId);
        balance.Value.Balance.Should().Be(100m);
    }
}
