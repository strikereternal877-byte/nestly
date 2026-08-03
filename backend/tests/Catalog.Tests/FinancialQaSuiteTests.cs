using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Payments;
using Nestly.Application.Pricing;
using Nestly.Application.Serviceability;
using Nestly.Application.Wallet;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Task 79's dedicated QA pass over financial flows - webhook idempotency,
/// duplicate callbacks, coupon abuse, and refund calculations. The features
/// themselves already carry substantial direct-unit coverage from when they
/// were built (PaymentWebhookServiceTests, CouponServiceTests,
/// RefundServiceTests); this file adds adversarial/integration-level
/// scenarios those didn't already exercise, rather than duplicating them.
/// </summary>
public sealed class FinancialQaSuiteTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public FinancialQaSuiteTests(TestDatabase db) => _db = db;

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

    private static EscrowService BuildEscrowService(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(new PlatformEscrowLedgerRepository(context));

    private static PaymentWebhookService BuildWebhookService(
        IPaymentTransactionRepository paymentRepository, IBookingRepository bookingRepository,
        Nestly.Infrastructure.Persistence.NestlyDbContext context, IPaymentGateway gateway) =>
        new(
            paymentRepository, bookingRepository, new ServiceRepository(context), gateway,
            new CommissionService(Options.Create(new CommissionOptions())), BuildEscrowService(context),
            context, new NoOpMetricsService(), NullLogger<PaymentWebhookService>.Instance);

    private sealed record Fixture(Customer Customer, City City, CustomerAddress Address, Locality Locality, Service Service, Guid BookingId, decimal Total);

    private async Task<Fixture> SeedAndBookAsync(Nestly.Infrastructure.Persistence.NestlyDbContext context, decimal servicePrice, string? couponCode = null)
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

        var request = new BookingSummaryRequest(service.Id, city.Id, address.Id, locality.Id, window.Id, futureDate, 1, [], couponCode);
        var created = await BuildBookingService(context).CreateAsync(customer.Id, request);
        created.IsSuccess.Should().BeTrue($"seed booking should succeed: {(created.IsFailure ? created.Error.Message : "")}");

        return new Fixture(customer, city, address, locality, service, created.Value.Id, created.Value.Price.TotalPayable);
    }

    // --- Webhook idempotency / duplicate callbacks -------------------------

    /// <summary>
    /// A retry supersedes attempt 1 with a new attempt 2. A late, duplicate
    /// callback for attempt 1's (now-stale) gateway order id must not be
    /// able to resurrect a resolved attempt or touch a booking that has
    /// since moved on - idempotency must key off the specific attempt, not
    /// "the transaction's current attempt."
    /// </summary>
    [Fact]
    public async Task A_late_duplicate_callback_for_a_superseded_attempt_cannot_override_the_retry_that_replaced_it()
    {
        var gateway = BuildGateway();
        Fixture fixture;
        using (var seedContext = _db.CreateContext())
        {
            fixture = await SeedAndBookAsync(seedContext, 1000m);
        }

        string firstOrderId, secondOrderId;
        using (var context = _db.CreateContext())
        {
            var paymentRepository = new PaymentTransactionRepository(context);
            var bookingRepository = new BookingRepository(context);
            var webhookService = BuildWebhookService(paymentRepository, bookingRepository, context, gateway);
            var paymentService = new PaymentService(paymentRepository, bookingRepository, gateway, (ISandboxPaymentSimulator)gateway, webhookService);

            var first = await paymentService.CreateOrderAsync(fixture.Customer.Id, new CreatePaymentOrderRequest(fixture.BookingId, null));
            firstOrderId = first.Value.GatewayOrderId;

            // Attempt 1 fails, and the customer retries (attempt 2).
            string failPayload = PaymentWebhookPayload.Build(firstOrderId, "ref1", PaymentWebhookPayload.FailedStatus);
            await webhookService.HandleCallbackAsync(new PaymentWebhookRequest(firstOrderId, "ref1", PaymentWebhookPayload.FailedStatus, gateway.SignPayload(failPayload)));

            var second = await paymentService.CreateOrderAsync(fixture.Customer.Id, new CreatePaymentOrderRequest(fixture.BookingId, null));
            secondOrderId = second.Value.GatewayOrderId;

            string succeedPayload = PaymentWebhookPayload.Build(secondOrderId, "ref2", PaymentWebhookPayload.SuccessStatus);
            var secondCallback = await webhookService.HandleCallbackAsync(new PaymentWebhookRequest(secondOrderId, "ref2", PaymentWebhookPayload.SuccessStatus, gateway.SignPayload(succeedPayload)));
            secondCallback.IsSuccess.Should().BeTrue();
        }

        // A late duplicate for the ORIGINAL (already-Failed, now superseded)
        // attempt arrives - must be a no-op, not an error, and must not
        // touch the booking the retry already confirmed.
        using (var lateContext = _db.CreateContext())
        {
            var paymentRepository = new PaymentTransactionRepository(lateContext);
            var bookingRepository = new BookingRepository(lateContext);
            var webhookService = BuildWebhookService(paymentRepository, bookingRepository, lateContext, gateway);

            string latePayload = PaymentWebhookPayload.Build(firstOrderId, "ref1-late", PaymentWebhookPayload.SuccessStatus);
            var lateResult = await webhookService.HandleCallbackAsync(new PaymentWebhookRequest(firstOrderId, "ref1-late", PaymentWebhookPayload.SuccessStatus, gateway.SignPayload(latePayload)));
            lateResult.IsSuccess.Should().BeTrue("a duplicate for a resolved (even if superseded) attempt is an idempotent no-op, not an error");
        }

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(fixture.BookingId);
        booking!.Status.Should().Be(BookingStatus.Confirmed, "the retry's real confirmation must survive the late duplicate untouched");

        var transaction = await new PaymentTransactionRepository(readContext).GetByGatewayOrderIdAsync(secondOrderId);
        transaction!.Attempts.Should().HaveCount(2);
        transaction.Attempts.Single(a => a.GatewayOrderId == firstOrderId).Status.Should().Be(PaymentAttemptStatus.Failed);
        transaction.Attempts.Single(a => a.GatewayOrderId == secondOrderId).Status.Should().Be(PaymentAttemptStatus.Success);
    }

    // --- Coupon abuse --------------------------------------------------

    /// <summary>
    /// A customer tries to reuse a single-use-per-customer coupon on a
    /// second booking. The end-to-end BookingService.CreateAsync path (not
    /// just CouponService.ValidateAsync in isolation) must refuse to create
    /// the second booking at all - never silently drop the coupon and book
    /// at full price, and never double-spend the coupon's per-customer allowance.
    /// </summary>
    [Fact]
    public async Task A_customer_cannot_reuse_a_single_use_coupon_across_two_of_their_own_bookings()
    {
        Fixture fixture;
        Guid couponId;
        using (var seedContext = _db.CreateContext())
        {
            fixture = await SeedAndBookAsync(seedContext, 1000m); // unrelated first booking, no coupon
            var coupon = new Coupon(
                Guid.NewGuid(), "ONETIME", "One use per customer", CouponDiscountType.Flat, 100m, null, 0m,
                DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30), usageLimitTotal: null, usageLimitPerCustomer: 1,
                applicableCategoryId: null, CouponCustomerSegment.All);
            couponId = coupon.Id;
            seedContext.Coupons.Add(coupon);
            seedContext.SaveChanges();
        }

        // Reuse the same fixture's service/address for two booking attempts with the coupon.
        using (var context = _db.CreateContext())
        {
            var window = context.SlotWindows.First(w => w.CityId == fixture.City.Id);
            var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
            var request = new BookingSummaryRequest(fixture.Service.Id, fixture.City.Id, fixture.Address.Id, fixture.Locality.Id, window.Id, futureDate, 1, [], "ONETIME");
            var firstCouponBooking = await BuildBookingService(context).CreateAsync(fixture.Customer.Id, request);
            firstCouponBooking.IsSuccess.Should().BeTrue();
        }

        using (var secondContext = _db.CreateContext())
        {
            var window = secondContext.SlotWindows.First(w => w.CityId == fixture.City.Id);
            var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
            var request = new BookingSummaryRequest(fixture.Service.Id, fixture.City.Id, fixture.Address.Id, fixture.Locality.Id, window.Id, futureDate, 1, [], "ONETIME");
            var secondCouponBooking = await BuildBookingService(secondContext).CreateAsync(fixture.Customer.Id, request);

            secondCouponBooking.IsFailure.Should().BeTrue("the coupon's per-customer allowance is already spent");
            secondCouponBooking.Error.Code.Should().Be("Coupon.AlreadyUsedByCustomer");
        }

        using var readContext = _db.CreateContext();
        var coupon2 = await new CouponRepository(readContext).GetByIdAsync(couponId);
        coupon2!.RedemptionCount.Should().Be(1, "the rejected second attempt must not have consumed another redemption");
    }

    // --- Refund calculations --------------------------------------------

    /// <summary>
    /// A partial gateway refund followed by a partial wallet refund on the
    /// same booking - the remaining-refundable-balance calculation must
    /// track correctly across heterogeneous settlement methods, and the
    /// booking only reaches the terminal Refunded status once their sum
    /// covers the original payment exactly.
    /// </summary>
    [Fact]
    public async Task Remaining_refundable_balance_is_tracked_correctly_across_mixed_gateway_and_wallet_partial_refunds()
    {
        var gateway = BuildGateway();
        Fixture fixture;
        using (var seedContext = _db.CreateContext())
        {
            fixture = await SeedAndBookAsync(seedContext, 1000m);
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
            string payload = PaymentWebhookPayload.Build(gatewayOrderId, "ref", PaymentWebhookPayload.SuccessStatus);
            var callback = await webhookService.HandleCallbackAsync(new PaymentWebhookRequest(gatewayOrderId, "ref", PaymentWebhookPayload.SuccessStatus, gateway.SignPayload(payload)));
            callback.IsSuccess.Should().BeTrue();
        }

        using (var cancelContext = _db.CreateContext())
        {
            var bookingRepository = new BookingRepository(cancelContext);
            var booking = await bookingRepository.GetByIdAsync(fixture.BookingId);
            booking!.TransitionTo(BookingStatus.CancelledByCustomer, "Cancelled.");
            await bookingRepository.UpdateAsync(booking);
        }

        RefundService BuildRefundService(Nestly.Infrastructure.Persistence.NestlyDbContext context) => new(
            new BookingRepository(context), new PaymentTransactionRepository(context), new RefundTransactionRepository(context),
            new WalletService(new WalletLedgerRepository(context)), BuildEscrowService(context), gateway, context);

        // 600 via gateway, then an over-ask of 500 (only 400 remains) must be rejected...
        using (var partialGatewayContext = _db.CreateContext())
        {
            var partial = await BuildRefundService(partialGatewayContext).InitiatePartialRefundAsync(fixture.BookingId, 600m, "Partial gateway refund");
            partial.IsSuccess.Should().BeTrue();
            partial.Value.Method.Should().Be(Nestly.Domain.RefundMethod.Gateway);
        }

        using (var overAskContext = _db.CreateContext())
        {
            var overAsk = await BuildRefundService(overAskContext).InitiatePartialRefundAsync(fixture.BookingId, 500m, "Too much", Nestly.Domain.RefundMethod.Wallet);
            overAsk.IsFailure.Should().BeTrue();
            overAsk.Error.Code.Should().Be("Refund.ExceedsRemainingBalance");
        }

        // ...but the exact remaining 400 via wallet succeeds and completes the refund.
        using (var walletContext = _db.CreateContext())
        {
            var remaining = await BuildRefundService(walletContext).InitiatePartialRefundAsync(fixture.BookingId, 400m, "Remaining balance via wallet", Nestly.Domain.RefundMethod.Wallet);
            remaining.IsSuccess.Should().BeTrue();
        }

        using var readContext = _db.CreateContext();
        var booking2 = await new BookingRepository(readContext).GetByIdAsync(fixture.BookingId);
        booking2!.Status.Should().Be(BookingStatus.Refunded);

        var refunds = await new RefundTransactionRepository(readContext).ListByBookingAsync(fixture.BookingId);
        refunds.Should().HaveCount(2);
        refunds.Sum(r => r.Amount).Should().Be(1000m);

        var walletBalance = await new WalletService(new WalletLedgerRepository(readContext)).GetBalanceAsync(fixture.Customer.Id);
        walletBalance.Value.Balance.Should().Be(400m, "only the wallet-settled portion credits the wallet");
    }
}
