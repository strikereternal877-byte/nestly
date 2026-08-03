using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Payments;
using Nestly.Application.Pricing;
using Nestly.Application.Refunds;
using Nestly.Application.Serviceability;
using Nestly.Application.Wallet;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers tasks 75a-d: refund entity/types, full refund, partial refund calculation + policy rules, and status lifecycle.</summary>
public sealed class RefundServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public RefundServiceTests(TestDatabase db) => _db = db;

    private static SandboxPaymentGateway BuildGateway() =>
        new(Options.Create(new SandboxGatewayOptions { WebhookSigningSecret = "unit-test-signing-secret-value" }));

    private static BookingService BuildBookingService(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        var couponService = new CouponService(new CouponRepository(context), new CouponRedemptionRepository(context), new BookingRepository(context), TimeProvider.System);
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

    private static RefundService BuildRefundService(Nestly.Infrastructure.Persistence.NestlyDbContext context, IPaymentGateway gateway) =>
        new(
            new BookingRepository(context),
            new PaymentTransactionRepository(context),
            new RefundTransactionRepository(context),
            new WalletService(new WalletLedgerRepository(context)),
            new EscrowService(new PlatformEscrowLedgerRepository(context)),
            gateway,
            context);

    private static PaymentWebhookService BuildWebhookService(
        IPaymentTransactionRepository paymentRepository, IBookingRepository bookingRepository,
        Nestly.Infrastructure.Persistence.NestlyDbContext context, IPaymentGateway gateway) =>
        new(
            paymentRepository, bookingRepository, new ServiceRepository(context), gateway,
            new CommissionService(Options.Create(new CommissionOptions())), new EscrowService(new PlatformEscrowLedgerRepository(context)),
            context, new NoOpMetricsService(), NullLogger<PaymentWebhookService>.Instance);

    private sealed record Fixture(Customer Customer, Guid BookingId, decimal Total);

    private async Task<Fixture> SeedBookingAsync(Nestly.Infrastructure.Persistence.NestlyDbContext context, decimal servicePrice = 1000m)
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
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", servicePrice);
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

    /// <summary>Drives a fresh booking through payment success and cancellation, leaving it eligible for refund (Confirmed -> CancelledByCustomer).</summary>
    private async Task<Fixture> SeedCancelledPaidBookingAsync(IPaymentGateway gateway, decimal servicePrice = 1000m)
    {
        Fixture fixture;
        using (var seedContext = _db.CreateContext())
        {
            fixture = await SeedBookingAsync(seedContext, servicePrice);
        }

        string gatewayOrderId;
        using (var orderContext = _db.CreateContext())
        {
            var paymentRepository = new PaymentTransactionRepository(orderContext);
            var bookingRepository = new BookingRepository(orderContext);
            var paymentService = new PaymentService(
                paymentRepository, bookingRepository, gateway, (ISandboxPaymentSimulator)gateway,
                BuildWebhookService(paymentRepository, bookingRepository, orderContext, gateway));
            var order = await paymentService.CreateOrderAsync(fixture.Customer.Id, new CreatePaymentOrderRequest(fixture.BookingId, null));
            gatewayOrderId = order.Value.GatewayOrderId;
        }

        using (var callbackContext = _db.CreateContext())
        {
            var paymentRepository = new PaymentTransactionRepository(callbackContext);
            var bookingRepository = new BookingRepository(callbackContext);
            var webhookService = BuildWebhookService(paymentRepository, bookingRepository, callbackContext, gateway);
            string payload = PaymentWebhookPayload.Build(gatewayOrderId, "sandbox_pay_ref", PaymentWebhookPayload.SuccessStatus);
            string signature = gateway.SignPayload(payload);
            var callback = await webhookService.HandleCallbackAsync(new PaymentWebhookRequest(gatewayOrderId, "sandbox_pay_ref", PaymentWebhookPayload.SuccessStatus, signature));
            callback.IsSuccess.Should().BeTrue();
        }

        using (var cancelContext = _db.CreateContext())
        {
            var bookingRepository = new BookingRepository(cancelContext);
            var booking = await bookingRepository.GetByIdAsync(fixture.BookingId);
            booking!.TransitionTo(BookingStatus.CancelledByCustomer, "Customer changed their mind.");
            await bookingRepository.UpdateAsync(booking);
        }

        return fixture;
    }

    [Fact]
    public async Task InitiateFullRefundAsync_refunds_the_full_amount_via_gateway_and_marks_the_booking_Refunded()
    {
        var gateway = BuildGateway();
        var fixture = await SeedCancelledPaidBookingAsync(gateway, servicePrice: 1001m); // avoid the .13 paisa failure convention

        using var context = _db.CreateContext();
        var result = await BuildRefundService(context, gateway).InitiateFullRefundAsync(fixture.BookingId, "Customer cancellation");

        result.IsSuccess.Should().BeTrue();
        result.Value.Type.Should().Be(RefundType.Full);
        result.Value.Method.Should().Be(RefundMethod.Gateway);
        result.Value.Status.Should().Be(RefundStatus.Refunded);
        result.Value.Amount.Should().Be(fixture.Total);
        result.Value.GatewayRefundRef.Should().NotBeNullOrEmpty();

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(fixture.BookingId);
        booking!.Status.Should().Be(BookingStatus.Refunded);
    }

    [Fact]
    public async Task InitiateFullRefundAsync_via_wallet_credits_the_customers_wallet()
    {
        var gateway = BuildGateway();
        var fixture = await SeedCancelledPaidBookingAsync(gateway, servicePrice: 1002m);

        using (var context = _db.CreateContext())
        {
            var result = await BuildRefundService(context, gateway).InitiateFullRefundAsync(fixture.BookingId, "Goodwill wallet refund", RefundMethod.Wallet);
            result.IsSuccess.Should().BeTrue();
            result.Value.Method.Should().Be(RefundMethod.Wallet);
            result.Value.GatewayRefundRef.Should().BeNull("a wallet-settled refund never calls the gateway");
        }

        using var readContext = _db.CreateContext();
        var balance = await new WalletService(new WalletLedgerRepository(readContext)).GetBalanceAsync(fixture.Customer.Id);
        balance.Value.Balance.Should().Be(fixture.Total);
    }

    [Fact]
    public async Task Two_partial_refunds_that_sum_to_the_full_amount_move_the_booking_to_Refunded_only_on_the_second()
    {
        var gateway = BuildGateway();
        var fixture = await SeedCancelledPaidBookingAsync(gateway, servicePrice: 1000m);
        decimal half = fixture.Total / 2;

        using (var firstContext = _db.CreateContext())
        {
            var first = await BuildRefundService(firstContext, gateway).InitiatePartialRefundAsync(fixture.BookingId, half, "Partial refund 1");
            first.IsSuccess.Should().BeTrue();
            first.Value.Type.Should().Be(RefundType.Partial);
            first.Value.Status.Should().Be(RefundStatus.Refunded);
        }

        using (var midContext = _db.CreateContext())
        {
            var booking = await new BookingRepository(midContext).GetByIdAsync(fixture.BookingId);
            booking!.Status.Should().Be(BookingStatus.RefundPending, "only part of the payment has been refunded so far");
        }

        using (var secondContext = _db.CreateContext())
        {
            var second = await BuildRefundService(secondContext, gateway).InitiatePartialRefundAsync(fixture.BookingId, half, "Partial refund 2");
            second.IsSuccess.Should().BeTrue();
        }

        using var readContext = _db.CreateContext();
        var finalBooking = await new BookingRepository(readContext).GetByIdAsync(fixture.BookingId);
        finalBooking!.Status.Should().Be(BookingStatus.Refunded);

        var refunds = await new RefundTransactionRepository(readContext).ListByBookingAsync(fixture.BookingId);
        refunds.Should().HaveCount(2);
        refunds.Sum(r => r.Amount).Should().Be(fixture.Total);
    }

    [Fact]
    public async Task InitiatePartialRefundAsync_rejects_an_amount_exceeding_the_remaining_refundable_balance()
    {
        var gateway = BuildGateway();
        var fixture = await SeedCancelledPaidBookingAsync(gateway, servicePrice: 1000m);

        using var context = _db.CreateContext();
        var result = await BuildRefundService(context, gateway).InitiatePartialRefundAsync(fixture.BookingId, fixture.Total + 1, "Too much");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Refund.ExceedsRemainingBalance");
    }

    [Fact]
    public async Task InitiateFullRefundAsync_rejects_a_booking_that_is_not_eligible()
    {
        Fixture fixture;
        using (var seedContext = _db.CreateContext())
        {
            fixture = await SeedBookingAsync(seedContext);
        }

        var gateway = BuildGateway();
        using var context = _db.CreateContext();
        // Still PaymentPending - never paid, never cancelled/completed.
        var result = await BuildRefundService(context, gateway).InitiateFullRefundAsync(fixture.BookingId, "Not eligible");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Refund.BookingNotEligible");
    }

    [Fact]
    public async Task InitiateFullRefundAsync_rejects_a_second_attempt_once_the_booking_is_already_fully_Refunded()
    {
        var gateway = BuildGateway();
        var fixture = await SeedCancelledPaidBookingAsync(gateway, servicePrice: 1003m);

        using (var firstContext = _db.CreateContext())
        {
            var first = await BuildRefundService(firstContext, gateway).InitiateFullRefundAsync(fixture.BookingId, "First refund");
            first.IsSuccess.Should().BeTrue();
        }

        using var secondContext = _db.CreateContext();
        var second = await BuildRefundService(secondContext, gateway).InitiateFullRefundAsync(fixture.BookingId, "Duplicate refund attempt");

        // A fully-refunded booking moves to the terminal Refunded status, which
        // the eligibility gate (not the balance check) correctly rejects -
        // Refunded has no outgoing transitions in BookingLifecycle, so a
        // second refund is blocked before "how much is left" is ever asked.
        second.IsFailure.Should().BeTrue();
        second.Error.Code.Should().Be("Refund.BookingNotEligible");
    }

    [Fact]
    public async Task ListByBookingAsync_does_not_return_another_customers_refunds()
    {
        var gateway = BuildGateway();
        var fixture = await SeedCancelledPaidBookingAsync(gateway, servicePrice: 1004m);

        using (var context = _db.CreateContext())
        {
            var refund = await BuildRefundService(context, gateway).InitiateFullRefundAsync(fixture.BookingId, "Refund");
            refund.IsSuccess.Should().BeTrue();
        }

        using var readContext = _db.CreateContext();
        var ownerResult = await BuildRefundService(readContext, gateway).ListByBookingAsync(fixture.Customer.Id, fixture.BookingId);
        ownerResult.IsSuccess.Should().BeTrue();
        ownerResult.Value.Should().ContainSingle();

        var strangerResult = await BuildRefundService(readContext, gateway).ListByBookingAsync(Guid.NewGuid(), fixture.BookingId);
        strangerResult.IsFailure.Should().BeTrue();
        strangerResult.Error.Code.Should().Be("Refund.BookingNotFound");
    }
}
