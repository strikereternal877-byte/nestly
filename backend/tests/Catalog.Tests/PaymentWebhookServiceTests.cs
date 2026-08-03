using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Payments;
using Nestly.Application.Pricing;
using Nestly.Application.Serviceability;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers tasks 69a-c: signed callback verification, idempotent duplicate handling, and the booking-payment mapping.</summary>
public sealed class PaymentWebhookServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public PaymentWebhookServiceTests(TestDatabase db) => _db = db;

    private static SandboxPaymentGateway BuildGateway() =>
        new(Options.Create(new SandboxGatewayOptions { WebhookSigningSecret = "unit-test-signing-secret-value" }));

    private static BookingService BuildBookingService(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        var couponService = new CouponService(
            new CouponRepository(context),
            new CouponRedemptionRepository(context),
            new BookingRepository(context),
            TimeProvider.System);

        var summaryService = new BookingSummaryService(
            new ServiceRepository(context),
            new ServiceAddOnRepository(context),
            new CustomerAddressRepository(context),
            new SlotAvailabilityService(
                new ServiceabilityRepository(context),
                new ServiceabilityValidationService(new ServiceabilityRepository(context), new InMemoryCacheService()),
                new SlotWindowRepository(context),
                new SlotBlackoutRepository(context),
                new SlotBookingPolicyRepository(context),
                new SlotCapacityRepository(context),
                TimeProvider.System),
            new PriceCalculationService(
                new ServiceRepository(context),
                new ServiceAddOnRepository(context),
                new ServiceabilityRepository(context),
                new ServiceCityPriceRepository(context),
                new CityPricingPolicyRepository(context)),
            couponService,
            new SubscriptionBenefitService(new CustomerSubscriptionRepository(context)));

        return new BookingService(
            summaryService,
            new BookingRepository(context),
            new CustomerRepository(context),
            couponService,
            new SlotAvailabilityService(
                new ServiceabilityRepository(context),
                new ServiceabilityValidationService(new ServiceabilityRepository(context), new InMemoryCacheService()),
                new SlotWindowRepository(context),
                new SlotBlackoutRepository(context),
                new SlotBookingPolicyRepository(context),
                new SlotCapacityRepository(context),
                TimeProvider.System),
            new NoOpMetricsService(),
            new BookingProviderAssignmentRepository(context),
            new CustomerSubscriptionRepository(context));
    }

    private static (PaymentService Payments, PaymentWebhookService Webhook) BuildServices(
        Nestly.Infrastructure.Persistence.NestlyDbContext context, IPaymentGateway gateway)
    {
        var paymentRepository = new PaymentTransactionRepository(context);
        var bookingRepository = new BookingRepository(context);
        var webhookService = new PaymentWebhookService(
            paymentRepository, bookingRepository, new ServiceRepository(context), gateway,
            new CommissionService(Options.Create(new CommissionOptions())), new EscrowService(new PlatformEscrowLedgerRepository(context)),
            context, new NoOpMetricsService(), NullLogger<PaymentWebhookService>.Instance);
        var paymentService = new PaymentService(paymentRepository, bookingRepository, gateway, (ISandboxPaymentSimulator)gateway, webhookService);

        return (paymentService, webhookService);
    }

    private sealed record Fixture(Customer Customer, Guid BookingId, decimal Total);

    private async Task<Fixture> SeedBookingAsync(Nestly.Infrastructure.Persistence.NestlyDbContext context, decimal? priceOverride = null)
    {
        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        var pincodeCode = Guid.NewGuid().ToString("N")[..6];
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        var address = new CustomerAddress(
            Guid.NewGuid(), customer.Id, "Home", "221B Baker Street", null, null,
            pincodeCode, "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210", true);
        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        var zone = new Zone(Guid.NewGuid(), city.Id, "Central");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, pincodeCode);
        var locality = new Locality(Guid.NewGuid(), zone.Id, pincode.Id, "Koramangala");
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", priceOverride ?? 500m);
        var window = new SlotWindow(Guid.NewGuid(), city.Id, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
        var rule = new SlotWindowRule(Guid.NewGuid(), window.Id, futureDate.DayOfWeek);

        context.Add(customer);
        context.Add(address);
        context.States.Add(state);
        context.Cities.Add(city);
        context.Zones.Add(zone);
        context.Pincodes.Add(pincode);
        context.Localities.Add(locality);
        context.Add(category);
        context.Add(service);
        context.ServicePincodeMappings.Add(new ServicePincodeMapping(Guid.NewGuid(), service.Id, pincode.Id));
        context.SlotWindows.Add(window);
        context.SlotWindowRules.Add(rule);
        context.SaveChanges();

        var request = new BookingSummaryRequest(service.Id, city.Id, address.Id, locality.Id, window.Id, futureDate, Quantity: 1, []);
        var created = await BuildBookingService(context).CreateAsync(customer.Id, request);
        created.IsSuccess.Should().BeTrue();

        return new Fixture(customer, created.Value.Id, created.Value.Price.TotalPayable);
    }

    [Fact]
    public async Task A_successful_callback_confirms_the_booking_and_marks_the_attempt_succeeded()
    {
        var gateway = BuildGateway();
        Fixture fixture;
        string gatewayOrderId;

        using (var seedContext = _db.CreateContext())
        {
            fixture = await SeedBookingAsync(seedContext, priceOverride: 501m); // avoids the .13 deterministic-failure paisa convention
            var (payments, _) = BuildServices(seedContext, gateway);
            var order = await payments.CreateOrderAsync(fixture.Customer.Id, new CreatePaymentOrderRequest(fixture.BookingId, IdempotencyKey: null));
            gatewayOrderId = order.Value.GatewayOrderId;
        }

        using (var callbackContext = _db.CreateContext())
        {
            var (_, webhook) = BuildServices(callbackContext, gateway);
            string paymentRef = "sandbox_pay_test_ref";
            string payload = PaymentWebhookPayload.Build(gatewayOrderId, paymentRef, PaymentWebhookPayload.SuccessStatus);
            string signature = gateway.SignPayload(payload);

            var result = await webhook.HandleCallbackAsync(new PaymentWebhookRequest(gatewayOrderId, paymentRef, PaymentWebhookPayload.SuccessStatus, signature));
            result.IsSuccess.Should().BeTrue();
        }

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(fixture.BookingId);
        booking!.Status.Should().Be(BookingStatus.Confirmed);

        var transaction = await new PaymentTransactionRepository(readContext).GetByBookingIdAsync(fixture.BookingId);
        transaction!.Status.Should().Be(PaymentTransactionStatus.Success);
        transaction.Attempts[0].Status.Should().Be(PaymentAttemptStatus.Success);
    }

    [Fact]
    public async Task A_failed_callback_moves_the_booking_to_PaymentFailed()
    {
        var gateway = BuildGateway();
        Fixture fixture;
        string gatewayOrderId;

        using (var seedContext = _db.CreateContext())
        {
            fixture = await SeedBookingAsync(seedContext, priceOverride: 501m);
            var (payments, _) = BuildServices(seedContext, gateway);
            var order = await payments.CreateOrderAsync(fixture.Customer.Id, new CreatePaymentOrderRequest(fixture.BookingId, IdempotencyKey: null));
            gatewayOrderId = order.Value.GatewayOrderId;
        }

        using (var callbackContext = _db.CreateContext())
        {
            var (_, webhook) = BuildServices(callbackContext, gateway);
            string paymentRef = "sandbox_declined_ref";
            string payload = PaymentWebhookPayload.Build(gatewayOrderId, paymentRef, PaymentWebhookPayload.FailedStatus);
            string signature = gateway.SignPayload(payload);

            var result = await webhook.HandleCallbackAsync(new PaymentWebhookRequest(gatewayOrderId, paymentRef, PaymentWebhookPayload.FailedStatus, signature));
            result.IsSuccess.Should().BeTrue();
        }

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(fixture.BookingId);
        booking!.Status.Should().Be(BookingStatus.PaymentFailed);
    }

    [Fact]
    public async Task A_callback_with_a_tampered_signature_is_rejected_and_changes_nothing()
    {
        var gateway = BuildGateway();
        Fixture fixture;
        string gatewayOrderId;

        using (var seedContext = _db.CreateContext())
        {
            fixture = await SeedBookingAsync(seedContext, priceOverride: 501m);
            var (payments, _) = BuildServices(seedContext, gateway);
            var order = await payments.CreateOrderAsync(fixture.Customer.Id, new CreatePaymentOrderRequest(fixture.BookingId, IdempotencyKey: null));
            gatewayOrderId = order.Value.GatewayOrderId;
        }

        using (var callbackContext = _db.CreateContext())
        {
            var (_, webhook) = BuildServices(callbackContext, gateway);
            var result = await webhook.HandleCallbackAsync(new PaymentWebhookRequest(gatewayOrderId, "sandbox_pay_test_ref", PaymentWebhookPayload.SuccessStatus, "not-a-real-signature"));

            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("Payment.InvalidWebhookSignature");
        }

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(fixture.BookingId);
        booking!.Status.Should().Be(BookingStatus.PaymentPending, "an unverified callback must never change booking state");
    }

    [Fact]
    public async Task A_duplicate_callback_for_an_already_resolved_attempt_is_an_idempotent_noop()
    {
        var gateway = BuildGateway();
        Fixture fixture;
        string gatewayOrderId;

        using (var seedContext = _db.CreateContext())
        {
            fixture = await SeedBookingAsync(seedContext, priceOverride: 501m);
            var (payments, _) = BuildServices(seedContext, gateway);
            var order = await payments.CreateOrderAsync(fixture.Customer.Id, new CreatePaymentOrderRequest(fixture.BookingId, IdempotencyKey: null));
            gatewayOrderId = order.Value.GatewayOrderId;
        }

        string paymentRef = "sandbox_pay_test_ref";
        string payload = PaymentWebhookPayload.Build(gatewayOrderId, paymentRef, PaymentWebhookPayload.SuccessStatus);
        string signature = gateway.SignPayload(payload);

        using (var firstCallbackContext = _db.CreateContext())
        {
            var (_, webhook) = BuildServices(firstCallbackContext, gateway);
            var first = await webhook.HandleCallbackAsync(new PaymentWebhookRequest(gatewayOrderId, paymentRef, PaymentWebhookPayload.SuccessStatus, signature));
            first.IsSuccess.Should().BeTrue();
        }

        // The gateway redelivers the identical callback (a very common real-world occurrence).
        using (var secondCallbackContext = _db.CreateContext())
        {
            var (_, webhook) = BuildServices(secondCallbackContext, gateway);
            var second = await webhook.HandleCallbackAsync(new PaymentWebhookRequest(gatewayOrderId, paymentRef, PaymentWebhookPayload.SuccessStatus, signature));
            second.IsSuccess.Should().BeTrue("a duplicate callback must be idempotent, not an error");
        }

        using var readContext = _db.CreateContext();
        var transaction = await new PaymentTransactionRepository(readContext).GetByBookingIdAsync(fixture.BookingId);
        transaction!.Attempts.Should().ContainSingle("the duplicate callback must not create or mutate a second attempt");
        var booking = await new BookingRepository(readContext).GetByIdAsync(fixture.BookingId);
        booking!.StatusHistory.Should().HaveCount(3, "Initiated -> PaymentPending -> Confirmed, with no extra entry from the duplicate callback");
    }

    [Fact]
    public async Task A_conflicting_duplicate_callback_does_not_override_the_first_resolution()
    {
        var gateway = BuildGateway();
        Fixture fixture;
        string gatewayOrderId;

        using (var seedContext = _db.CreateContext())
        {
            fixture = await SeedBookingAsync(seedContext, priceOverride: 501m);
            var (payments, _) = BuildServices(seedContext, gateway);
            var order = await payments.CreateOrderAsync(fixture.Customer.Id, new CreatePaymentOrderRequest(fixture.BookingId, IdempotencyKey: null));
            gatewayOrderId = order.Value.GatewayOrderId;
        }

        // First callback: success.
        using (var firstCallbackContext = _db.CreateContext())
        {
            var (_, webhook) = BuildServices(firstCallbackContext, gateway);
            string paymentRef = "sandbox_pay_test_ref";
            string payload = PaymentWebhookPayload.Build(gatewayOrderId, paymentRef, PaymentWebhookPayload.SuccessStatus);
            string signature = gateway.SignPayload(payload);
            var first = await webhook.HandleCallbackAsync(new PaymentWebhookRequest(gatewayOrderId, paymentRef, PaymentWebhookPayload.SuccessStatus, signature));
            first.IsSuccess.Should().BeTrue();
        }

        // A second, conflicting callback claiming failure for the same order - must not flip the outcome.
        using (var secondCallbackContext = _db.CreateContext())
        {
            var (_, webhook) = BuildServices(secondCallbackContext, gateway);
            string paymentRef = "sandbox_pay_test_ref";
            string payload = PaymentWebhookPayload.Build(gatewayOrderId, paymentRef, PaymentWebhookPayload.FailedStatus);
            string signature = gateway.SignPayload(payload);
            var second = await webhook.HandleCallbackAsync(new PaymentWebhookRequest(gatewayOrderId, paymentRef, PaymentWebhookPayload.FailedStatus, signature));
            second.IsSuccess.Should().BeTrue();
        }

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(fixture.BookingId);
        booking!.Status.Should().Be(BookingStatus.Confirmed, "the first resolution wins and is never overwritten by a later, conflicting callback");
    }

    [Fact]
    public async Task An_unknown_gateway_order_id_returns_not_found()
    {
        var gateway = BuildGateway();
        using var context = _db.CreateContext();
        var (_, webhook) = BuildServices(context, gateway);

        string payload = PaymentWebhookPayload.Build("sandbox_order_does_not_exist", "ref", PaymentWebhookPayload.SuccessStatus);
        string signature = gateway.SignPayload(payload);

        var result = await webhook.HandleCallbackAsync(new PaymentWebhookRequest("sandbox_order_does_not_exist", "ref", PaymentWebhookPayload.SuccessStatus, signature));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Payment.OrderNotFound");
    }
}
