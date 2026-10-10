using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Payments;
using Nestly.Application.Wallet;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Adding money to the wallet through the gateway. The properties that matter are the ones a mistake turns into
/// real money problems: the wallet is credited exactly once however many routes deliver the outcome, never for a
/// payment that does not match what was asked for, never through a forged callback or the sandbox shortcut on a
/// real gateway, and a payment that arrives late is not lost.
/// </summary>
public sealed class WalletTopUpServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public WalletTopUpServiceTests(TestDatabase db)
    {
        _db = db;
        SetWalletSettings(allowTopUp: true, maxWalletBalance: 50_000m);
    }

    /// <summary>
    /// The admin's wallet settings group (the "Allow wallet top-up" switch and the balance cap) - one row per
    /// database, so this updates it in place. Every test starts from "allowed, 50,000" like the seeded default.
    /// </summary>
    private void SetWalletSettings(bool allowTopUp, decimal maxWalletBalance)
    {
        using var context = _db.CreateContext();
        var cap = maxWalletBalance.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var allow = allowTopUp ? "true" : "false";
        var json = "{\"maxWalletBalance\":" + cap + ",\"maxWalletUsagePercentagePerBooking\":100,\"walletCreditExpiryDays\":null,\"allowWalletTopUp\":" + allow + "}";
        var row = context.Set<SystemSetting>().SingleOrDefault(s => s.GroupKey == SystemSettingGroups.Wallet);
        if (row is null)
        {
            context.Add(new SystemSetting(Guid.NewGuid(), SystemSettingGroups.Wallet, json));
        }
        else
        {
            row.UpdateValue(json, null);
        }

        context.SaveChanges();
    }

    private void RemoveWalletSettings()
    {
        using var context = _db.CreateContext();
        var row = context.Set<SystemSetting>().SingleOrDefault(s => s.GroupKey == SystemSettingGroups.Wallet);
        if (row is not null)
        {
            context.Remove(row);
            context.SaveChanges();
        }
    }

    /// <summary>A gateway whose verify answer the test scripts, to exercise the reconciliation paths a real gateway drives.</summary>
    internal sealed class ScriptedGateway : IPaymentGateway, ISandboxPaymentSimulator
    {
        public GatewayVerifyResult NextVerify { get; set; } = new("pending");

        public Task<GatewayOrderResult> CreateOrderAsync(GatewayCreateOrderRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GatewayOrderResult(request.ExistingGatewayOrderId ?? $"scripted_{Guid.NewGuid():N}", "created"));

        public Task<GatewayRefundResult> RefundAsync(GatewayRefundRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GatewayRefundResult("refund", "processed"));

        public Task<GatewayVerifyResult> VerifyOrderStatusAsync(string gatewayOrderId, CancellationToken cancellationToken = default) =>
            Task.FromResult(NextVerify);

        public string BuildCanonicalPayload(PaymentWebhookRequest request) =>
            PaymentWebhookPayload.Build(request.GatewayOrderId, request.GatewayPaymentRef, request.Status);

        public bool VerifyWebhookSignature(string canonicalPayload, string signature) => signature == SignPayload(canonicalPayload);

        public SandboxPaymentOutcome DetermineOutcome(decimal amount) => new(true, "scripted_ref", null);

        public string SignPayload(string canonicalPayload) => "sig:" + canonicalPayload;
    }

    private static SandboxPaymentGateway Sandbox() =>
        new(Options.Create(new SandboxGatewayOptions { WebhookSigningSecret = "unit-test-signing-secret-value" }));

    internal static WalletTopUpOptions Enabled(Action<WalletTopUpOptions>? tweak = null)
    {
        var options = new WalletTopUpOptions { Enabled = true };
        tweak?.Invoke(options);
        return options;
    }

    internal static WalletTopUpService BuildService(
        NestlyDbContext context, IPaymentGateway gateway, WalletTopUpOptions options, ISandboxPaymentSimulator? simulator = null,
        ILogger<WalletTopUpService>? logger = null) => new(
        new WalletTopUpRepository(context),
        new WalletService(new WalletLedgerRepository(context), context),
        new CustomerRepository(context),
        gateway,
        // As in the real wiring: the simulator is always the sandbox, whichever gateway is active.
        simulator ?? (gateway as ISandboxPaymentSimulator) ?? Sandbox(),
        context,
        TestServices.SystemSettings(context),
        Options.Create(options),
        TimeProvider.System,
        logger ?? NullLogger<WalletTopUpService>.Instance);

    private Customer SeedCustomer(decimal startingBalance = 0m)
    {
        using var context = _db.CreateContext();
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        context.Add(customer);
        context.SaveChanges();

        if (startingBalance > 0)
        {
            new WalletService(new WalletLedgerRepository(context), context)
                .CreditAsync(customer.Id, startingBalance, WalletSourceType.PromotionalCredit, null, "Starting balance")
                .GetAwaiter().GetResult();
        }

        return customer;
    }

    private async Task<decimal> BalanceAsync(Guid customerId)
    {
        using var context = _db.CreateContext();
        return (await new WalletService(new WalletLedgerRepository(context), context).GetBalanceAsync(customerId)).Value.Balance;
    }

    private async Task<WalletTopUpOrderResponse> StartAsync(Guid customerId, decimal amount, WalletTopUpOptions? options = null, IPaymentGateway? gateway = null)
    {
        using var context = _db.CreateContext();
        var result = await BuildService(context, gateway ?? Sandbox(), options ?? Enabled()).CreateAsync(customerId, amount);
        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : string.Empty);
        return result.Value;
    }

    private async Task<Result> DeliverAsync(
        string gatewayOrderId, string status, IPaymentGateway? gateway = null, decimal? amount = null, string? signatureOverride = null,
        ILogger<WalletTopUpService>? logger = null)
    {
        gateway ??= Sandbox();
        var simulator = (ISandboxPaymentSimulator)gateway;
        string payload = PaymentWebhookPayload.Build(gatewayOrderId, "ref_123", status);
        using var context = _db.CreateContext();
        return await BuildService(context, gateway, Enabled(), logger: logger).HandleCallbackAsync(
            new PaymentWebhookRequest(gatewayOrderId, "ref_123", status, signatureOverride ?? simulator.SignPayload(payload), amount));
    }

    private async Task<WalletTopUp> ReloadAsync(Guid topUpId)
    {
        using var context = _db.CreateContext();
        return (await new WalletTopUpRepository(context).GetByIdAsync(topUpId))!;
    }

    // ---- Switch and limits ----------------------------------------------------------------

    [Fact]
    public async Task Nothing_is_created_while_top_ups_are_switched_off()
    {
        var customer = SeedCustomer();
        using var context = _db.CreateContext();

        var result = await BuildService(context, Sandbox(), new WalletTopUpOptions { Enabled = false }).CreateAsync(customer.Id, 500m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("WalletTopUp.Disabled");
        (await new WalletTopUpRepository(context).CountCreatedSinceAsync(customer.Id, DateTime.UtcNow.AddDays(-1))).Should().Be(0);
    }

    [Fact]
    public async Task The_config_says_off_by_default_and_only_offers_suggestions_inside_the_limits()
    {
        using var context = _db.CreateContext();

        var off = await BuildService(context, Sandbox(), new WalletTopUpOptions()).GetConfigAsync();
        off.Enabled.Should().BeFalse("a feature that holds customers' money must not switch itself on");

        var on = await BuildService(context, Sandbox(), Enabled(o => { o.MinAmount = 200; o.MaxAmount = 3000; o.SuggestedAmounts = "100, 500,1000,5000,abc,500"; })).GetConfigAsync();
        on.Enabled.Should().BeTrue();
        on.SuggestedAmounts.Should().Equal(500m, 1000m);
    }

    [Fact]
    public async Task The_admin_can_switch_top_ups_off_even_when_the_deployment_has_them_on()
    {
        var customer = SeedCustomer();
        SetWalletSettings(allowTopUp: false, maxWalletBalance: 50_000m);
        using var context = _db.CreateContext();
        var service = BuildService(context, Sandbox(), Enabled());

        (await service.GetConfigAsync()).Enabled.Should().BeFalse("the screens must stop offering it too");
        var result = await service.CreateAsync(customer.Id, 500m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("WalletTopUp.Disabled");
        (await new WalletTopUpRepository(context).CountCreatedSinceAsync(customer.Id, DateTime.UtcNow.AddDays(-1))).Should().Be(0);
    }

    [Fact]
    public async Task The_admin_switch_does_not_switch_on_what_the_deployment_has_off()
    {
        // "Allow" is true (the seeded default) but WalletTopUp:Enabled is not: both must agree.
        using var context = _db.CreateContext();

        (await BuildService(context, Sandbox(), new WalletTopUpOptions { Enabled = false }).GetConfigAsync())
            .Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Top_ups_are_refused_when_the_wallet_settings_cannot_be_read()
    {
        var customer = SeedCustomer();
        RemoveWalletSettings();
        using var context = _db.CreateContext();
        var service = BuildService(context, Sandbox(), Enabled());

        (await service.GetConfigAsync()).Enabled.Should().BeFalse("it fails closed rather than open");
        (await service.CreateAsync(customer.Id, 500m)).Error.Code.Should().Be("WalletTopUp.Disabled");
    }

    [Fact]
    public async Task The_lower_of_the_admins_and_the_deployments_balance_cap_applies()
    {
        var customer = SeedCustomer(startingBalance: 900m);
        SetWalletSettings(allowTopUp: true, maxWalletBalance: 1_000m); // lower than the deployment's 20,000
        using var context = _db.CreateContext();
        var service = BuildService(context, Sandbox(), Enabled());

        (await service.GetConfigAsync()).MaxWalletBalance.Should().Be(1_000m);
        var result = await service.CreateAsync(customer.Id, 200m); // 900 + 200 > 1,000

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("WalletTopUp.BalanceLimit");
        result.Error.Message.Should().Contain("100", "it tells the customer how much room is left");
    }

    [Theory]
    [InlineData(99.99)]
    [InlineData(10000.01)]
    public async Task An_amount_outside_the_limits_is_refused(double amount)
    {
        var customer = SeedCustomer();
        using var context = _db.CreateContext();

        var result = await BuildService(context, Sandbox(), Enabled()).CreateAsync(customer.Id, (decimal)amount);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("WalletTopUp.AmountOutOfRange");
    }

    [Theory]
    [InlineData(100)]
    [InlineData(10000)]
    public async Task The_limits_themselves_are_allowed(double amount)
    {
        var customer = SeedCustomer();

        var order = await StartAsync(customer.Id, (decimal)amount);

        order.Amount.Should().Be((decimal)amount);
    }

    [Fact]
    public async Task A_wallet_cannot_be_topped_up_past_its_balance_limit_counting_top_ups_still_in_flight()
    {
        var customer = SeedCustomer(startingBalance: 19_000m);
        var options = Enabled();

        await StartAsync(customer.Id, 500m, options); // 19,000 + 500 pending = 19,500

        using var context = _db.CreateContext();
        var result = await BuildService(context, Sandbox(), options).CreateAsync(customer.Id, 600m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("WalletTopUp.BalanceLimit");
        result.Error.Message.Should().Contain("500", "it tells the customer how much room is left");
    }

    [Fact]
    public async Task The_daily_limit_stops_a_run_of_top_ups()
    {
        var customer = SeedCustomer();
        var options = Enabled(o => o.MaxTopUpsPerDay = 2);

        await StartAsync(customer.Id, 100m, options);
        await StartAsync(customer.Id, 200m, options);

        using var context = _db.CreateContext();
        var third = await BuildService(context, Sandbox(), options).CreateAsync(customer.Id, 300m);

        third.IsFailure.Should().BeTrue();
        third.Error.Code.Should().Be("WalletTopUp.DailyLimit");
    }

    [Fact]
    public async Task Asking_again_for_the_same_amount_returns_the_checkout_already_in_flight()
    {
        var customer = SeedCustomer();

        var first = await StartAsync(customer.Id, 500m);
        var again = await StartAsync(customer.Id, 500m);
        var different = await StartAsync(customer.Id, 700m);

        again.TopUpId.Should().Be(first.TopUpId);
        again.GatewayOrderId.Should().Be(first.GatewayOrderId);
        different.TopUpId.Should().NotBe(first.TopUpId);
    }

    // ---- Crediting exactly once -----------------------------------------------------------------

    [Fact]
    public async Task A_confirmed_payment_credits_the_wallet_once_with_the_customers_own_non_expiring_money()
    {
        var customer = SeedCustomer(startingBalance: 50m);
        var order = await StartAsync(customer.Id, 500m);

        (await DeliverAsync(order.GatewayOrderId, PaymentWebhookPayload.SuccessStatus, amount: 500m)).IsSuccess.Should().BeTrue();
        // The gateway redelivers, and the customer's return page also asks: neither may credit again.
        (await DeliverAsync(order.GatewayOrderId, PaymentWebhookPayload.SuccessStatus, amount: 500m)).IsSuccess.Should().BeTrue();
        (await DeliverAsync(order.GatewayOrderId, PaymentWebhookPayload.FailedStatus)).IsSuccess.Should().BeTrue();

        (await BalanceAsync(customer.Id)).Should().Be(550m);

        var topUp = await ReloadAsync(order.TopUpId);
        topUp.Status.Should().Be(WalletTopUpStatus.Success);
        topUp.WalletLedgerEntryId.Should().NotBeNull();

        using var context = _db.CreateContext();
        var credit = (await new WalletLedgerRepository(context).ListByCustomerAsync(customer.Id))
            .Single(e => e.SourceType == WalletSourceType.TopUp);
        credit.Amount.Should().Be(500m);
        credit.SourceReferenceId.Should().Be(order.TopUpId);
        credit.ExpiresAtUtc.Should().BeNull("a top-up is the customer's own cash, not an expiring credit");
        credit.Id.Should().Be(topUp.WalletLedgerEntryId!.Value);
    }

    [Fact]
    public async Task A_failed_payment_credits_nothing()
    {
        var customer = SeedCustomer();
        var order = await StartAsync(customer.Id, 500m);

        await DeliverAsync(order.GatewayOrderId, PaymentWebhookPayload.FailedStatus);

        (await BalanceAsync(customer.Id)).Should().Be(0m);
        (await ReloadAsync(order.TopUpId)).Status.Should().Be(WalletTopUpStatus.Failed);
    }

    [Fact]
    public async Task A_payment_that_arrives_after_the_top_up_was_written_off_still_reaches_the_wallet()
    {
        var customer = SeedCustomer();
        var order = await StartAsync(customer.Id, 500m);
        await DeliverAsync(order.GatewayOrderId, PaymentWebhookPayload.FailedStatus);
        (await ReloadAsync(order.TopUpId)).Status.Should().Be(WalletTopUpStatus.Failed);

        await DeliverAsync(order.GatewayOrderId, PaymentWebhookPayload.SuccessStatus, amount: 500m);

        (await BalanceAsync(customer.Id)).Should().Be(500m, "money the gateway took must never be left uncredited");
        (await ReloadAsync(order.TopUpId)).Status.Should().Be(WalletTopUpStatus.Success);
    }

    // ---- Callback safety ---------------------------------------------------------------------------

    [Fact]
    public async Task A_callback_with_a_bad_signature_changes_nothing()
    {
        var customer = SeedCustomer();
        var order = await StartAsync(customer.Id, 500m);

        var result = await DeliverAsync(order.GatewayOrderId, PaymentWebhookPayload.SuccessStatus, signatureOverride: "forged");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Payment.InvalidWebhookSignature");
        (await BalanceAsync(customer.Id)).Should().Be(0m);
        (await ReloadAsync(order.TopUpId)).Status.Should().Be(WalletTopUpStatus.Pending);
    }

    [Fact]
    public async Task A_forged_callback_cannot_split_a_log_line_through_its_gateway_order_id()
    {
        var logger = new CapturingLogger<WalletTopUpService>();
        const string forgedOrderId = "order_1\r\n[Error] Wallet top-up credited manually";

        var result = await DeliverAsync(forgedOrderId, PaymentWebhookPayload.SuccessStatus, signatureOverride: "forged", logger: logger);

        result.Error.Code.Should().Be("Payment.InvalidWebhookSignature");
        logger.Messages.Should().ContainSingle();
        logger.Messages[0].Should().NotContainAny("\r", "\n").And.Contain("order_1__[Error]");
    }

    [Fact]
    public async Task A_callback_whose_amount_differs_from_what_was_asked_for_is_not_credited()
    {
        var customer = SeedCustomer();
        var order = await StartAsync(customer.Id, 500m);

        var result = await DeliverAsync(order.GatewayOrderId, PaymentWebhookPayload.SuccessStatus, amount: 5000m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("WalletTopUp.AmountMismatch");
        (await BalanceAsync(customer.Id)).Should().Be(0m);
        (await ReloadAsync(order.TopUpId)).Status.Should().Be(WalletTopUpStatus.Pending, "left for a person to review, not written off");
    }

    [Fact]
    public async Task A_callback_for_an_order_that_is_not_a_top_up_says_so_so_other_handlers_can_claim_it()
    {
        var result = await DeliverAsync("someone-elses-order", PaymentWebhookPayload.SuccessStatus);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Payment.OrderNotFound");
    }

    // ---- Sandbox shortcut ------------------------------------------------------------------------------

    [Fact]
    public async Task The_sandbox_shortcut_completes_a_top_up_through_the_real_callback_path()
    {
        var customer = SeedCustomer();
        var order = await StartAsync(customer.Id, 500m);

        using var context = _db.CreateContext();
        var result = await BuildService(context, Sandbox(), Enabled()).SimulateAsync(customer.Id, order.TopUpId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(WalletTopUpStatus.Success);
        result.Value.WalletBalance.Should().Be(500m);
    }

    [Fact]
    public async Task The_sandbox_shortcut_declines_the_amount_the_sandbox_is_told_to_decline()
    {
        var customer = SeedCustomer();
        var order = await StartAsync(customer.Id, 199.13m); // a .13 paisa amount is the sandbox's deterministic decline

        using var context = _db.CreateContext();
        var result = await BuildService(context, Sandbox(), Enabled()).SimulateAsync(customer.Id, order.TopUpId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(WalletTopUpStatus.Failed);
        result.Value.WalletBalance.Should().Be(0m);
    }

    [Fact]
    public async Task The_sandbox_shortcut_is_refused_when_a_real_gateway_is_configured_so_nobody_can_credit_themselves()
    {
        var customer = SeedCustomer();
        var realGateway = new PayUPaymentGateway(
            new StubHttpClientFactory(StubHttpMessageHandler.Responding(System.Net.HttpStatusCode.OK)),
            Options.Create(new PayUOptions { MerchantKey = "test-key", MerchantSalt = "test-salt", CheckoutReturnBaseUrl = "https://app.nestly.test" }),
            NullLogger<PayUPaymentGateway>.Instance);

        var order = await StartAsync(customer.Id, 500m, gateway: realGateway);

        using var context = _db.CreateContext();
        var result = await BuildService(context, realGateway, Enabled(), simulator: Sandbox()).SimulateAsync(customer.Id, order.TopUpId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("WalletTopUp.SimulateNotAvailable");
        (await BalanceAsync(customer.Id)).Should().Be(0m);
    }

    [Fact]
    public async Task A_real_gateway_checkout_comes_back_to_the_wallet_not_to_a_booking()
    {
        var customer = SeedCustomer();
        var realGateway = new PayUPaymentGateway(
            new StubHttpClientFactory(StubHttpMessageHandler.Responding(System.Net.HttpStatusCode.OK)),
            Options.Create(new PayUOptions { MerchantKey = "test-key", MerchantSalt = "test-salt", CheckoutReturnBaseUrl = "https://app.nestly.test" }),
            NullLogger<PayUPaymentGateway>.Instance);

        var order = await StartAsync(customer.Id, 500m, gateway: realGateway);

        order.CheckoutRedirectUrl.Should().NotBeNullOrEmpty();
        order.CheckoutFormFields!["surl"].Should().Be($"https://app.nestly.test/wallet/topup/{order.TopUpId}/return");
        order.CheckoutFormFields["furl"].Should().Be(order.CheckoutFormFields["surl"]);
        order.CheckoutFormFields["amount"].Should().Be("500.00");
        order.CheckoutFormFields["productinfo"].Should().Be("Glavyx wallet top-up");
    }

    // ---- Ownership -----------------------------------------------------------------------------------------

    [Fact]
    public async Task One_customer_cannot_read_verify_or_simulate_anothers_top_up()
    {
        var owner = SeedCustomer();
        var other = SeedCustomer();
        var order = await StartAsync(owner.Id, 500m);

        using var context = _db.CreateContext();
        var service = BuildService(context, Sandbox(), Enabled());

        (await service.GetAsync(other.Id, order.TopUpId)).Error.Code.Should().Be("WalletTopUp.NotFound");
        (await service.VerifyPendingAsync(other.Id, order.TopUpId)).Error.Code.Should().Be("WalletTopUp.NotFound");
        (await service.SimulateAsync(other.Id, order.TopUpId)).Error.Code.Should().Be("WalletTopUp.NotFound");
        (await BalanceAsync(owner.Id)).Should().Be(0m);
    }

    // ---- Reconciliation ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Verifying_a_pending_top_up_credits_it_when_the_gateway_says_it_succeeded()
    {
        var customer = SeedCustomer();
        var gateway = new ScriptedGateway();
        var order = await StartAsync(customer.Id, 500m, gateway: gateway);

        gateway.NextVerify = new GatewayVerifyResult(PaymentWebhookPayload.SuccessStatus, GatewayPaymentRef: "pay_1");
        using var context = _db.CreateContext();
        var result = await BuildService(context, gateway, Enabled()).VerifyPendingAsync(customer.Id, order.TopUpId);

        result.Value.Status.Should().Be(WalletTopUpStatus.Success);
        (await BalanceAsync(customer.Id)).Should().Be(500m);
    }

    [Fact]
    public async Task Verifying_leaves_a_top_up_alone_while_the_gateway_still_says_pending()
    {
        var customer = SeedCustomer();
        var gateway = new ScriptedGateway { NextVerify = new GatewayVerifyResult("pending") };
        var order = await StartAsync(customer.Id, 500m, gateway: gateway);

        using var context = _db.CreateContext();
        var result = await BuildService(context, gateway, Enabled()).VerifyPendingAsync(customer.Id, order.TopUpId);

        result.Value.Status.Should().Be(WalletTopUpStatus.Pending);
        (await BalanceAsync(customer.Id)).Should().Be(0m);
    }

    [Fact]
    public async Task The_sweep_resolves_old_pending_top_ups_and_leaves_recent_ones_alone()
    {
        var customer = SeedCustomer();
        var gateway = new ScriptedGateway();
        var oldOrder = await StartAsync(customer.Id, 500m, gateway: gateway);
        var newOrder = await StartAsync(customer.Id, 700m, gateway: gateway);

        using (var context = _db.CreateContext())
        {
            var longAgo = DateTime.UtcNow.AddHours(-3);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE wallet_top_up SET created_at_utc = {longAgo} WHERE id = {oldOrder.TopUpId}");
        }

        gateway.NextVerify = new GatewayVerifyResult(PaymentWebhookPayload.SuccessStatus, GatewayPaymentRef: "pay_9");
        using (var context = _db.CreateContext())
        {
            var service = BuildService(context, gateway, Enabled());
            await new WalletTopUpSweepJob(
                new WalletTopUpRepository(context), service, Options.Create(Enabled()), TimeProvider.System,
                NullLogger<WalletTopUpSweepJob>.Instance).SweepAsync();
        }

        (await ReloadAsync(oldOrder.TopUpId)).Status.Should().Be(WalletTopUpStatus.Success);
        (await ReloadAsync(newOrder.TopUpId)).Status.Should().Be(WalletTopUpStatus.Pending, "too recent to be an abandoned checkout");
        (await BalanceAsync(customer.Id)).Should().Be(500m);
    }

    // ---- Callback routing ---------------------------------------------------------------------------------------

    /// <summary>Keeps the rendered text of every entry, so a test can see exactly what a plain-text log sink would print.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class FakeBookingPayments : IPaymentWebhookService
    {
        public HashSet<string> KnownOrders { get; } = [];

        public Task<Result> HandleCallbackAsync(PaymentWebhookRequest request)
        {
            if (request.Signature == "forged")
            {
                return Task.FromResult(Result.Failure(Error.Unauthorized("Payment.InvalidWebhookSignature", "bad")));
            }

            return Task.FromResult(KnownOrders.Contains(request.GatewayOrderId)
                ? Result.Success()
                : Result.Failure(Error.NotFound("Payment.OrderNotFound", "none")));
        }

        public Task<Result<PaymentTransaction>> VerifyPendingAttemptAsync(Guid bookingId) => throw new NotSupportedException();

        public Task<Result<PaymentTransaction>> RecordManualPaymentAsync(Guid bookingId, ManualPaymentMethod method, string reference) =>
            throw new NotSupportedException();
    }

    private sealed class FakeTopUps : IWalletTopUpService
    {
        public List<string> Handled { get; } = [];

        public Task<Result> HandleCallbackAsync(PaymentWebhookRequest request)
        {
            Handled.Add(request.GatewayOrderId);
            return Task.FromResult(Result.Success());
        }

        public Task<WalletTopUpConfigResponse> GetConfigAsync() => throw new NotSupportedException();
        public Task<Result<WalletTopUpOrderResponse>> CreateAsync(Guid customerId, decimal amount) => throw new NotSupportedException();
        public Task<Result<WalletTopUpResponse>> GetAsync(Guid customerId, Guid topUpId) => throw new NotSupportedException();
        public Task<Result<WalletTopUpResponse>> VerifyPendingAsync(Guid customerId, Guid topUpId) => throw new NotSupportedException();
        public Task<Result<WalletTopUpResponse>> SimulateAsync(Guid customerId, Guid topUpId) => throw new NotSupportedException();
        public Task<bool> ReconcileAsync(Guid topUpId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<WalletTopUpReconcileOutcome>> ReconcileNowAsync(Guid topUpId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static PaymentWebhookRequest Callback(string orderId, string signature = "ok") =>
        new(orderId, "ref", PaymentWebhookPayload.SuccessStatus, signature);

    [Fact]
    public async Task The_router_gives_booking_orders_to_the_booking_handler_and_only_unclaimed_ones_to_top_ups()
    {
        var bookings = new FakeBookingPayments();
        bookings.KnownOrders.Add("booking-order");
        var topUps = new FakeTopUps();
        var router = new PaymentCallbackRouter(bookings, topUps);

        (await router.HandleAsync(Callback("booking-order"))).IsSuccess.Should().BeTrue();
        topUps.Handled.Should().BeEmpty("a booking's callback must never reach the wallet handler");

        (await router.HandleAsync(Callback("topup-order"))).IsSuccess.Should().BeTrue();
        topUps.Handled.Should().Equal("topup-order");
    }

    [Fact]
    public async Task The_router_does_not_retry_a_real_failure_against_the_wallet_handler()
    {
        var topUps = new FakeTopUps();
        var router = new PaymentCallbackRouter(new FakeBookingPayments(), topUps);

        var result = await router.HandleAsync(Callback("anything", signature: "forged"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Payment.InvalidWebhookSignature");
        topUps.Handled.Should().BeEmpty();
    }
}
