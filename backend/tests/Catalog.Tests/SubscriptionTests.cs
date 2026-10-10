using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.Subscriptions;
using Nestly.Domain;
using Nestly.Infrastructure.Auditing;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers tasks 177-181, 183 - plan CRUD, subscribe/cancel, and the recurring billing sweep.</summary>
public sealed class SubscriptionTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;
    private readonly DateTime _now = new(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);

    public SubscriptionTests(TestDatabase db) => _db = db;

    private static SandboxPaymentGateway BuildGateway() =>
        new(Options.Create(new SandboxGatewayOptions { WebhookSigningSecret = "unit-test-signing-secret-value" }));

    private sealed class StubAuditContextProvider : IAuditContextProvider
    {
        public AuditContext GetCurrent() =>
            new(AuditActorType.AdminUser, Guid.NewGuid(), IpAddress: "127.0.0.1", CorrelationId: "test-correlation-id");
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _fixedNow;
        public FakeTimeProvider(DateTime now) => _fixedNow = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));
        public override DateTimeOffset GetUtcNow() => _fixedNow;
    }

    [Fact]
    public async Task PlanManagementService_create_then_deactivate_then_activate_round_trips()
    {
        using var context = _db.CreateContext();
        var service = new SubscriptionPlanManagementService(new SubscriptionPlanRepository(context), new AuditLogWriter(context, new StubAuditContextProvider()));

        var created = await service.CreateAsync(new SubscriptionPlanCreateRequest("Nestly Plus " + Guid.NewGuid(), "desc", 499m, SubscriptionBillingCycle.Monthly, 2, 10m, true));
        created.IsSuccess.Should().BeTrue();
        created.Value.IsActive.Should().BeTrue();

        var deactivated = await service.DeactivateAsync(created.Value.Id, Guid.NewGuid());
        deactivated.IsSuccess.Should().BeTrue();
        (await service.GetByIdAsync(created.Value.Id)).Value.IsActive.Should().BeFalse();

        var activated = await service.ActivateAsync(created.Value.Id, Guid.NewGuid());
        activated.IsSuccess.Should().BeTrue();
        (await service.GetByIdAsync(created.Value.Id)).Value.IsActive.Should().BeTrue();
    }

    private async Task<SubscriptionPlan> SeedActivePlanAsync(Nestly.Infrastructure.Persistence.NestlyDbContext context, decimal price = 499m)
    {
        var plan = new SubscriptionPlan(Guid.NewGuid(), "Nestly Plus " + Guid.NewGuid(), "desc", price, SubscriptionBillingCycle.Monthly, 2, 10m, false);
        context.Add(plan);
        await context.SaveChangesAsync();
        return plan;
    }

    private static async Task<Guid> SeedCustomerAsync(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Test Customer", CustomerStatus.Active);
        context.Add(customer);
        await context.SaveChangesAsync();
        return customer.Id;
    }

    [Fact]
    public async Task CustomerSubscriptionService_subscribe_snapshots_plan_terms()
    {
        using var context = _db.CreateContext();
        var plan = await SeedActivePlanAsync(context);
        var customerId = await SeedCustomerAsync(context);
        var service = new CustomerSubscriptionService(new SubscriptionPlanRepository(context), new CustomerSubscriptionRepository(context), new FakeTimeProvider(_now));

        var result = await service.SubscribeAsync(customerId, new SubscribeRequest(plan.Id));

        result.IsSuccess.Should().BeTrue();
        result.Value.PlanName.Should().Be(plan.Name);
        result.Value.FreeVisitsRemaining.Should().Be(2);
        result.Value.Status.Should().Be(CustomerSubscriptionStatus.Active);
    }

    [Fact]
    public async Task CustomerSubscriptionService_rejects_a_second_live_subscription()
    {
        using var context = _db.CreateContext();
        var plan = await SeedActivePlanAsync(context);
        var customerId = await SeedCustomerAsync(context);
        var service = new CustomerSubscriptionService(new SubscriptionPlanRepository(context), new CustomerSubscriptionRepository(context), new FakeTimeProvider(_now));

        (await service.SubscribeAsync(customerId, new SubscribeRequest(plan.Id))).IsSuccess.Should().BeTrue();
        var second = await service.SubscribeAsync(customerId, new SubscribeRequest(plan.Id));

        second.IsFailure.Should().BeTrue();
        second.Error.Code.Should().Be("Subscription.AlreadySubscribed");
    }

    [Fact]
    public async Task CustomerSubscriptionService_cancel_lets_the_customer_subscribe_again()
    {
        using var context = _db.CreateContext();
        var plan = await SeedActivePlanAsync(context);
        var customerId = await SeedCustomerAsync(context);
        var service = new CustomerSubscriptionService(new SubscriptionPlanRepository(context), new CustomerSubscriptionRepository(context), new FakeTimeProvider(_now));

        var subscribed = await service.SubscribeAsync(customerId, new SubscribeRequest(plan.Id));
        (await service.CancelAsync(customerId, subscribed.Value.Id)).IsSuccess.Should().BeTrue();

        (await service.GetMyCurrentSubscriptionAsync(customerId)).Should().BeNull();

        var resubscribed = await service.SubscribeAsync(customerId, new SubscribeRequest(plan.Id));
        resubscribed.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task BillingJob_renews_a_due_subscription_on_a_successful_charge()
    {
        using var context = _db.CreateContext();
        // Whole-rupee amount - SandboxPaymentGateway's convention: paisa != 13 succeeds.
        var plan = await SeedActivePlanAsync(context, price: 499.00m);
        var customerId = await SeedCustomerAsync(context);
        var subscription = new CustomerSubscription(Guid.NewGuid(), customerId, plan, _now.AddDays(-31));
        context.Add(subscription);
        await context.SaveChangesAsync();

        var timeProvider = new FakeTimeProvider(_now);
        var gateway = BuildGateway();
        var job = new SubscriptionBillingJob(
            new CustomerSubscriptionRepository(context), gateway, gateway,
            Options.Create(new SubscriptionBillingOptions()), timeProvider, NullLogger<SubscriptionBillingJob>.Instance);

        await job.ProcessDueBillingAsync();

        var reloaded = await new CustomerSubscriptionRepository(context).GetByIdAsync(subscription.Id);
        reloaded!.Status.Should().Be(CustomerSubscriptionStatus.Active);
        reloaded.RetryCount.Should().Be(0);
        reloaded.CurrentPeriodStartUtc.Should().Be(_now);
    }

    [Fact]
    public async Task BillingJob_suspends_then_expires_a_subscription_after_repeated_failed_charges()
    {
        using var context = _db.CreateContext();
        // Paisa component 13 forces SandboxPaymentGateway to fail deterministically.
        var plan = await SeedActivePlanAsync(context, price: 499.13m);
        var customerId = await SeedCustomerAsync(context);
        var subscription = new CustomerSubscription(Guid.NewGuid(), customerId, plan, _now.AddDays(-31));
        context.Add(subscription);
        await context.SaveChangesAsync();

        var options = new SubscriptionBillingOptions { RetryLimit = 1, RetryBackoffDays = 1 };
        var gateway = BuildGateway();

        // First failure: within retry limit, suspends but stays retryable.
        var job1 = new SubscriptionBillingJob(
            new CustomerSubscriptionRepository(context), gateway, gateway,
            Options.Create(options), new FakeTimeProvider(_now), NullLogger<SubscriptionBillingJob>.Instance);
        await job1.ProcessDueBillingAsync();

        var afterFirstFailure = await new CustomerSubscriptionRepository(context).GetByIdAsync(subscription.Id);
        afterFirstFailure!.Status.Should().Be(CustomerSubscriptionStatus.PaymentFailed);
        afterFirstFailure.RetryCount.Should().Be(1);

        // Second failure, once the retry backoff has elapsed: retry limit exhausted -> Expired.
        var afterBackoff = new FakeTimeProvider(_now.AddDays(2));
        var job2 = new SubscriptionBillingJob(
            new CustomerSubscriptionRepository(context), gateway, gateway,
            Options.Create(options), afterBackoff, NullLogger<SubscriptionBillingJob>.Instance);
        await job2.ProcessDueBillingAsync();

        var afterSecondFailure = await new CustomerSubscriptionRepository(context).GetByIdAsync(subscription.Id);
        afterSecondFailure!.Status.Should().Be(CustomerSubscriptionStatus.Expired);
    }

    [Fact]
    public async Task BillingJob_marks_subscriptions_entering_the_expiring_soon_window()
    {
        using var context = _db.CreateContext();
        var plan = await SeedActivePlanAsync(context);
        var customerId = await SeedCustomerAsync(context);
        // Subscribed such that CurrentPeriodEndUtc falls 2 days from "now" - within the default 3-day lead time.
        var subscription = new CustomerSubscription(Guid.NewGuid(), customerId, plan, _now.AddDays(-28));
        context.Add(subscription);
        await context.SaveChangesAsync();

        var gateway = BuildGateway();
        var job = new SubscriptionBillingJob(
            new CustomerSubscriptionRepository(context), gateway, gateway,
            Options.Create(new SubscriptionBillingOptions()), new FakeTimeProvider(_now), NullLogger<SubscriptionBillingJob>.Instance);

        await job.ProcessDueBillingAsync();

        var reloaded = await new CustomerSubscriptionRepository(context).GetByIdAsync(subscription.Id);
        reloaded!.ExpiringSoonNotifiedForPeriodEndUtc.Should().Be(reloaded.CurrentPeriodEndUtc);
    }

    /// <summary>
    /// Regression: with a real gateway configured, this job used to still
    /// resolve the charge via ISandboxPaymentSimulator regardless (it is
    /// always bound to the concrete sandbox - see PaymentGatewayRegistration's
    /// own doc comment) and record a successful renewal despite no real
    /// charge ever being attempted. It must now record a failed charge -
    /// the same retry/backoff path a genuine decline already takes -
    /// instead of fabricating a renewal nobody paid for.
    /// </summary>
    [Fact]
    public async Task BillingJob_records_a_failed_charge_instead_of_a_fake_renewal_when_a_real_gateway_is_configured()
    {
        using var context = _db.CreateContext();
        // A whole-rupee amount that would deterministically SUCCEED under
        // the sandbox convention - proving a real-gateway failure here is
        // not just an artifact of the sandbox's own paisa-13 decline rule.
        var plan = await SeedActivePlanAsync(context, price: 499.00m);
        var customerId = await SeedCustomerAsync(context);
        var subscription = new CustomerSubscription(Guid.NewGuid(), customerId, plan, _now.AddDays(-31));
        context.Add(subscription);
        await context.SaveChangesAsync();

        var realGateway = new PayUPaymentGateway(
            new StubHttpClientFactory(StubHttpMessageHandler.Responding(System.Net.HttpStatusCode.OK)),
            Options.Create(new PayUOptions
            {
                MerchantKey = "test-key",
                MerchantSalt = "test-salt",
                CheckoutReturnBaseUrl = "https://app.nestly.test",
            }),
            NullLogger<PayUPaymentGateway>.Instance);
        var sandboxSimulator = BuildGateway();

        var job = new SubscriptionBillingJob(
            new CustomerSubscriptionRepository(context), realGateway, sandboxSimulator,
            Options.Create(new SubscriptionBillingOptions()), new FakeTimeProvider(_now), NullLogger<SubscriptionBillingJob>.Instance);

        await job.ProcessDueBillingAsync();

        var reloaded = await new CustomerSubscriptionRepository(context).GetByIdAsync(subscription.Id);
        reloaded!.Status.Should().Be(CustomerSubscriptionStatus.PaymentFailed);
        reloaded.RetryCount.Should().Be(1);
    }
}
