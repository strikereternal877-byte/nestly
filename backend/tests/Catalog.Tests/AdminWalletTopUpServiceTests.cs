using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Application.Payments;
using Nestly.Application.Wallet;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// The admin's view of wallet top-ups: the list says which ones need a person (stuck, or a gateway callback that
/// disagreed with the amount), "Reconcile now" asks the gateway and applies a definite answer exactly once, and the
/// customer page can show the ledger behind a balance. Each test gets its own database so the summary counts are
/// exact rather than relative.
/// </summary>
public sealed class AdminWalletTopUpServiceTests : IDisposable
{
    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    // ---- helpers ----------------------------------------------------------------------------------------------

    /// <summary>A clock a fixed distance ahead of real time, so a freshly created top-up can be made to look old.</summary>
    private sealed class OffsetTimeProvider(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + offset;
    }

    private static readonly Guid AdminId = Guid.NewGuid();

    private AdminWalletTopUpService BuildAdminService(
        NestlyDbContext context, WalletTopUpServiceTests.ScriptedGateway? gateway = null, TimeSpan? clockOffset = null) =>
        new(
            context,
            WalletTopUpServiceTests.BuildService(context, gateway ?? new WalletTopUpServiceTests.ScriptedGateway(), WalletTopUpServiceTests.Enabled()),
            TestServices.AuditLogWriter(context),
            clockOffset is { } offset ? new OffsetTimeProvider(offset) : TimeProvider.System);

    private Customer SeedCustomer(string name = "Asha Rao", string? mobile = null)
    {
        using var context = _db.CreateContext();
        var customer = new Customer(Guid.NewGuid(), mobile ?? "9" + Guid.NewGuid().ToString("N")[..9], name, CustomerStatus.Active);
        context.Add(customer);
        context.SaveChanges();
        return customer;
    }

    /// <summary>Saves a top-up in the given state; <paramref name="ageMinutes"/> backdates its creation.</summary>
    private WalletTopUp SeedTopUp(
        Guid customerId, decimal amount = 500m, WalletTopUpStatus status = WalletTopUpStatus.Pending, int ageMinutes = 0,
        string? reviewReason = null)
    {
        using var context = _db.CreateContext();
        var topUp = new WalletTopUp(Guid.NewGuid(), customerId, amount, "INR", "order_" + Guid.NewGuid().ToString("N"));
        context.Add(topUp);
        context.Entry(topUp).Property(t => t.CreatedAtUtc).CurrentValue = DateTime.UtcNow.AddMinutes(-ageMinutes);
        context.SaveChanges();

        if (status == WalletTopUpStatus.Success)
        {
            var entry = new WalletService(new WalletLedgerRepository(context), context)
                .CreditAsync(customerId, amount, WalletSourceType.TopUp, topUp.Id, "Wallet top-up").GetAwaiter().GetResult();
            topUp.MarkSucceeded("ref_" + topUp.Id.ToString("N")[..8], entry.Id);
        }
        else if (status == WalletTopUpStatus.Failed)
        {
            topUp.MarkFailed("Declined");
        }

        if (reviewReason is not null)
        {
            topUp.FlagForReview(reviewReason);
        }

        context.SaveChanges();
        return topUp;
    }

    private async Task<decimal> BalanceAsync(Guid customerId)
    {
        using var context = _db.CreateContext();
        return (await new WalletService(new WalletLedgerRepository(context), context).GetBalanceAsync(customerId)).Value.Balance;
    }

    // ---- list, filters, summary ------------------------------------------------------------------------------

    [Fact]
    public async Task The_list_is_newest_first_with_the_customer_and_gateway_details()
    {
        var asha = SeedCustomer("Asha Rao", "9000000001");
        var older = SeedTopUp(asha.Id, 500m, ageMinutes: 5);
        var newer = SeedTopUp(asha.Id, 1000m, WalletTopUpStatus.Success);

        using var context = _db.CreateContext();
        var result = await BuildAdminService(context).SearchAsync(new AdminWalletTopUpFilterRequest());

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Select(i => i.Id).Should().Equal(newer.Id, older.Id);
        var first = result.Value.Items[0];
        first.CustomerName.Should().Be("Asha Rao");
        first.CustomerMobile.Should().Be("9000000001");
        first.Status.Should().Be(WalletTopUpStatus.Success);
        first.WalletLedgerEntryId.Should().NotBeNull();
        first.AgeMinutes.Should().BeNull("an age only means something while the top-up is still pending");
        result.Value.Items[1].AgeMinutes.Should().BeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public async Task The_summary_counts_describe_the_whole_table_whatever_the_filter()
    {
        var customer = SeedCustomer();
        SeedTopUp(customer.Id, status: WalletTopUpStatus.Pending, ageMinutes: 2);
        SeedTopUp(customer.Id, status: WalletTopUpStatus.Pending, ageMinutes: 45);
        SeedTopUp(customer.Id, status: WalletTopUpStatus.Pending, ageMinutes: 3, reviewReason: "Amount differs");
        SeedTopUp(customer.Id, 700m, WalletTopUpStatus.Success);
        SeedTopUp(customer.Id, 300m, WalletTopUpStatus.Success);
        SeedTopUp(customer.Id, status: WalletTopUpStatus.Failed);

        using var context = _db.CreateContext();
        var result = await BuildAdminService(context).SearchAsync(new AdminWalletTopUpFilterRequest(Status: WalletTopUpStatus.Failed));

        result.Value.TotalCount.Should().Be(1, "the filter narrows the rows");
        result.Value.PendingCount.Should().Be(3);
        result.Value.StuckCount.Should().Be(1, "only the 45-minute-old pending top-up is past the stuck threshold");
        result.Value.NeedsReviewCount.Should().Be(1);
        result.Value.CreditedLast24HoursCount.Should().Be(2);
        result.Value.CreditedLast24HoursAmount.Should().Be(1000m);
    }

    [Fact]
    public async Task Needs_attention_keeps_stuck_and_flagged_top_ups_and_drops_fresh_and_finished_ones()
    {
        var customer = SeedCustomer();
        var fresh = SeedTopUp(customer.Id, ageMinutes: 2);
        var stuck = SeedTopUp(customer.Id, ageMinutes: 31);
        var flagged = SeedTopUp(customer.Id, ageMinutes: 1, reviewReason: "The gateway reported 400 paid but 500 was requested.");
        var done = SeedTopUp(customer.Id, status: WalletTopUpStatus.Success, ageMinutes: 120);

        using var context = _db.CreateContext();
        var result = await BuildAdminService(context).SearchAsync(new AdminWalletTopUpFilterRequest(NeedsAttention: true));

        var ids = result.Value.Items.Select(i => i.Id).ToList();
        ids.Should().BeEquivalentTo([stuck.Id, flagged.Id]);
        ids.Should().NotContain([fresh.Id, done.Id]);

        result.Value.Items.Single(i => i.Id == stuck.Id).Attention.Should().Be(AdminWalletTopUpAttention.Stuck);
        result.Value.Items.Single(i => i.Id == stuck.Id).AttentionReason.Should().Contain("31 minutes");
        var review = result.Value.Items.Single(i => i.Id == flagged.Id);
        review.Attention.Should().Be(AdminWalletTopUpAttention.NeedsReview);
        review.AttentionReason.Should().Contain("500 was requested");
    }

    [Fact]
    public async Task A_flagged_top_up_that_is_also_old_is_reported_as_needing_review_not_merely_stuck()
    {
        var customer = SeedCustomer();
        var topUp = SeedTopUp(customer.Id, ageMinutes: 90, reviewReason: "Amount differs");

        using var context = _db.CreateContext();
        var result = await BuildAdminService(context).GetAsync(topUp.Id);

        result.Value.Attention.Should().Be(AdminWalletTopUpAttention.NeedsReview);
    }

    [Fact]
    public async Task A_clock_running_ahead_makes_a_recent_pending_top_up_stuck()
    {
        var customer = SeedCustomer();
        SeedTopUp(customer.Id, ageMinutes: 1);

        using var context = _db.CreateContext();
        var result = await BuildAdminService(context, clockOffset: TimeSpan.FromMinutes(40))
            .SearchAsync(new AdminWalletTopUpFilterRequest(NeedsAttention: true));

        result.Value.StuckCount.Should().Be(1);
        result.Value.Items.Should().ContainSingle().Which.Attention.Should().Be(AdminWalletTopUpAttention.Stuck);
    }

    [Theory]
    [InlineData("asha")]
    [InlineData("ASHA RAO")]
    [InlineData("9123456789")]
    [InlineData("12345")]
    public async Task Search_matches_the_customer_name_or_mobile_case_insensitively(string needle)
    {
        var asha = SeedCustomer("Asha Rao", "9123456789");
        var other = SeedCustomer("Vikram Singh", "9888877777");
        var ashaTopUp = SeedTopUp(asha.Id);
        SeedTopUp(other.Id);

        using var context = _db.CreateContext();
        var result = await BuildAdminService(context).SearchAsync(new AdminWalletTopUpFilterRequest(Search: needle));

        result.Value.Items.Should().ContainSingle().Which.Id.Should().Be(ashaTopUp.Id);
    }

    [Fact]
    public async Task Search_finds_a_top_up_by_its_gateway_order_id()
    {
        var customer = SeedCustomer();
        var target = SeedTopUp(customer.Id);
        SeedTopUp(customer.Id);

        using var context = _db.CreateContext();
        var result = await BuildAdminService(context).SearchAsync(new AdminWalletTopUpFilterRequest(Search: target.GatewayOrderId));

        result.Value.Items.Should().ContainSingle().Which.Id.Should().Be(target.Id);
    }

    [Fact]
    public async Task Paging_returns_the_requested_slice_and_the_total()
    {
        var customer = SeedCustomer();
        for (int i = 0; i < 5; i++)
        {
            SeedTopUp(customer.Id, ageMinutes: i);
        }

        using var context = _db.CreateContext();
        var result = await BuildAdminService(context).SearchAsync(new AdminWalletTopUpFilterRequest(Page: 2, PageSize: 2));

        result.Value.TotalCount.Should().Be(5);
        result.Value.Items.Should().HaveCount(2);
        result.Value.Page.Should().Be(2);
        result.Value.PageSize.Should().Be(2);
    }

    [Fact]
    public async Task An_unknown_top_up_is_not_found()
    {
        using var context = _db.CreateContext();
        var service = BuildAdminService(context);

        (await service.GetAsync(Guid.NewGuid())).Error.Code.Should().Be("WalletTopUp.NotFound");
        (await service.ReconcileAsync(Guid.NewGuid(), AdminId)).Error.Code.Should().Be("WalletTopUp.NotFound");
    }

    // ---- the mismatch flag ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_callback_whose_amount_differs_is_flagged_for_review_and_then_shows_in_the_admin_list()
    {
        var customer = SeedCustomer();
        var topUp = SeedTopUp(customer.Id, 500m);
        var gateway = new WalletTopUpServiceTests.ScriptedGateway();

        using (var context = _db.CreateContext())
        {
            string payload = PaymentWebhookPayload.Build(topUp.GatewayOrderId, "ref_1", PaymentWebhookPayload.SuccessStatus);
            var callback = await WalletTopUpServiceTests.BuildService(context, gateway, WalletTopUpServiceTests.Enabled())
                .HandleCallbackAsync(new PaymentWebhookRequest(
                    topUp.GatewayOrderId, "ref_1", PaymentWebhookPayload.SuccessStatus, gateway.SignPayload(payload), 400m));
            callback.Error.Code.Should().Be("WalletTopUp.AmountMismatch");
        }

        (await BalanceAsync(customer.Id)).Should().Be(0m, "a mismatched payment is never credited");

        using var readContext = _db.CreateContext();
        var item = (await BuildAdminService(readContext).GetAsync(topUp.Id)).Value;
        item.Status.Should().Be(WalletTopUpStatus.Pending);
        item.Attention.Should().Be(AdminWalletTopUpAttention.NeedsReview);
        item.AttentionReason.Should().Contain("400").And.Contain("500");
    }

    [Fact]
    public void Flagging_is_idempotent_and_a_resolved_top_up_clears_it()
    {
        var customer = SeedCustomer();
        var topUp = SeedTopUp(customer.Id);

        topUp.FlagForReview("first reason");
        var firstFlaggedAt = topUp.ReviewFlaggedAtUtc;
        topUp.FlagForReview("second reason");

        topUp.ReviewReason.Should().Be("first reason", "a redelivery that disagrees the same way must not rewrite the original");
        topUp.ReviewFlaggedAtUtc.Should().Be(firstFlaggedAt);

        topUp.MarkFailed("Declined");
        topUp.NeedsReview.Should().BeFalse("a top-up that has resolved has nothing left to review");
    }

    // ---- Reconcile now ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Reconcile_credits_the_wallet_when_the_gateway_confirms_the_payment()
    {
        var customer = SeedCustomer();
        var topUp = SeedTopUp(customer.Id, 800m, ageMinutes: 40);
        var gateway = new WalletTopUpServiceTests.ScriptedGateway { NextVerify = new("success", "pay_777") };

        using var context = _db.CreateContext();
        var result = await BuildAdminService(context, gateway).ReconcileAsync(topUp.Id, AdminId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Outcome.Should().Be(WalletTopUpReconcileOutcome.Credited);
        result.Value.TopUp.Status.Should().Be(WalletTopUpStatus.Success);
        result.Value.TopUp.GatewayPaymentRef.Should().Be("pay_777");
        result.Value.TopUp.Attention.Should().Be(AdminWalletTopUpAttention.None);
        (await BalanceAsync(customer.Id)).Should().Be(800m);
    }

    [Fact]
    public async Task Reconciling_twice_credits_once()
    {
        var customer = SeedCustomer();
        var topUp = SeedTopUp(customer.Id, 800m);
        var gateway = new WalletTopUpServiceTests.ScriptedGateway { NextVerify = new("success", "pay_1") };

        using (var context = _db.CreateContext())
        {
            (await BuildAdminService(context, gateway).ReconcileAsync(topUp.Id, AdminId)).Value.Outcome
                .Should().Be(WalletTopUpReconcileOutcome.Credited);
        }

        using (var context = _db.CreateContext())
        {
            (await BuildAdminService(context, gateway).ReconcileAsync(topUp.Id, AdminId)).Value.Outcome
                .Should().Be(WalletTopUpReconcileOutcome.Unchanged);
        }

        (await BalanceAsync(customer.Id)).Should().Be(800m);
    }

    [Fact]
    public async Task Reconcile_marks_the_top_up_failed_when_the_gateway_says_it_was_declined()
    {
        var customer = SeedCustomer();
        var topUp = SeedTopUp(customer.Id, 800m, ageMinutes: 40);
        var gateway = new WalletTopUpServiceTests.ScriptedGateway { NextVerify = new("failed", FailureReason: "Card declined") };

        using var context = _db.CreateContext();
        var result = await BuildAdminService(context, gateway).ReconcileAsync(topUp.Id, AdminId);

        result.Value.Outcome.Should().Be(WalletTopUpReconcileOutcome.MarkedFailed);
        result.Value.TopUp.Status.Should().Be(WalletTopUpStatus.Failed);
        result.Value.TopUp.FailureReason.Should().Be("Card declined");
        (await BalanceAsync(customer.Id)).Should().Be(0m);
    }

    [Fact]
    public async Task Reconcile_changes_nothing_while_the_gateway_still_says_pending()
    {
        var customer = SeedCustomer();
        var topUp = SeedTopUp(customer.Id, ageMinutes: 40);

        using var context = _db.CreateContext();
        var result = await BuildAdminService(context, new WalletTopUpServiceTests.ScriptedGateway()).ReconcileAsync(topUp.Id, AdminId);

        result.Value.Outcome.Should().Be(WalletTopUpReconcileOutcome.StillPending);
        result.Value.TopUp.Status.Should().Be(WalletTopUpStatus.Pending);
        result.Value.TopUp.Attention.Should().Be(AdminWalletTopUpAttention.Stuck);
    }

    [Fact]
    public async Task Reconcile_recovers_a_payment_that_arrived_after_the_top_up_was_written_off()
    {
        var customer = SeedCustomer();
        var topUp = SeedTopUp(customer.Id, 600m, WalletTopUpStatus.Failed);
        var gateway = new WalletTopUpServiceTests.ScriptedGateway { NextVerify = new("success", "pay_late") };

        using var context = _db.CreateContext();
        var result = await BuildAdminService(context, gateway).ReconcileAsync(topUp.Id, AdminId);

        result.Value.Outcome.Should().Be(WalletTopUpReconcileOutcome.Credited);
        result.Value.TopUp.Status.Should().Be(WalletTopUpStatus.Success);
        (await BalanceAsync(customer.Id)).Should().Be(600m, "money the customer paid must not be lost to a write-off");
    }

    [Fact]
    public async Task Reconcile_leaves_a_failed_top_up_failed_when_the_gateway_agrees()
    {
        var customer = SeedCustomer();
        var topUp = SeedTopUp(customer.Id, 600m, WalletTopUpStatus.Failed);
        var gateway = new WalletTopUpServiceTests.ScriptedGateway { NextVerify = new("failed", FailureReason: "Declined") };

        using var context = _db.CreateContext();
        var result = await BuildAdminService(context, gateway).ReconcileAsync(topUp.Id, AdminId);

        result.Value.Outcome.Should().Be(WalletTopUpReconcileOutcome.Unchanged);
        result.Value.TopUp.Status.Should().Be(WalletTopUpStatus.Failed);
    }

    [Fact]
    public async Task Reconcile_clears_a_review_flag_once_the_top_up_resolves()
    {
        var customer = SeedCustomer();
        var topUp = SeedTopUp(customer.Id, 500m, ageMinutes: 5, reviewReason: "Amount differs");
        var gateway = new WalletTopUpServiceTests.ScriptedGateway { NextVerify = new("success", "pay_ok") };

        using var context = _db.CreateContext();
        var result = await BuildAdminService(context, gateway).ReconcileAsync(topUp.Id, AdminId);

        result.Value.TopUp.Attention.Should().Be(AdminWalletTopUpAttention.None);
        (await BuildAdminService(_db.CreateContext()).SearchAsync(new AdminWalletTopUpFilterRequest())).Value.NeedsReviewCount.Should().Be(0);
    }

    [Fact]
    public async Task Reconcile_writes_an_audit_entry_naming_the_admin_and_the_outcome()
    {
        var customer = SeedCustomer();
        var topUp = SeedTopUp(customer.Id, 500m);
        var gateway = new WalletTopUpServiceTests.ScriptedGateway { NextVerify = new("success", "pay_audit") };

        using (var context = _db.CreateContext())
        {
            await BuildAdminService(context, gateway).ReconcileAsync(topUp.Id, AdminId);
        }

        using var readContext = _db.CreateContext();
        var entry = await readContext.Set<AuditLog>().SingleAsync(a => a.EntityName == "WalletTopUp" && a.Action == "AdminReconcile");
        entry.EntityId.Should().Be(topUp.Id.ToString());
        entry.NewValues.Should().Contain(AdminId.ToString()).And.Contain("Outcome");
    }
}
