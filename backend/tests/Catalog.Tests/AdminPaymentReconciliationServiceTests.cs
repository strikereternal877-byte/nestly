using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Payments;
using Nestly.Application.Pricing;
using Nestly.Application.Refunds;
using Nestly.Application.Serviceability;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Payment reconciliation (docs/OPEN-FIXES-FEATURES.csv "Payment
/// reconciliation"): the stuck-pending/failed/orphaned classification
/// <see cref="AdminPaymentReconciliationService.GetReconciliationAsync"/>
/// builds, and the <see cref="AdminPaymentReconciliationService.VoidAsync"/>
/// write action. Seeding mirrors <see cref="AdminPaymentQueryServiceTests"/>
/// exactly - see its own doc comment for why (real Customer/Booking rows are
/// required by payment_transaction's FK).
/// </summary>
public sealed class AdminPaymentReconciliationServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public AdminPaymentReconciliationServiceTests(TestDatabase db) => _db = db;

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
            new ServiceGroupRepository(context),
            new CustomerAddressRepository(context),
            new SlotAvailabilityService(
                new ServiceabilityRepository(context),
                new ServiceabilityValidationService(new ServiceabilityRepository(context), new InMemoryCacheService()),
                new SlotWindowRepository(context),
                new SlotBlackoutRepository(context),
                new SlotBookingPolicyRepository(context),
                new SlotCapacityRepository(context),
                TestServices.Clock()),
            new PriceCalculationService(
                new ServiceRepository(context),
                new ServiceAddOnRepository(context),
                new ServiceabilityRepository(context),
                new ServiceCityPriceRepository(context),
                new CityPricingPolicyRepository(context), new ServiceVariantRepository(context), new ServiceAddOnGroupRepository(context), new InMemoryCacheService()),
            couponService,
            new SubscriptionBenefitService(new CustomerSubscriptionRepository(context)),
            new WalletService(new WalletLedgerRepository(context), context),
            new ServiceabilityRepository(context),
            TestServices.BookingOptions());

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
                TestServices.Clock()),
            new NoOpMetricsService(),
            new BookingProviderAssignmentRepository(context),
            new ProviderRepository(context),
            new ReviewRepository(context),
            new CustomerSubscriptionRepository(context),
            new WalletService(new WalletLedgerRepository(context), context),
            new AlwaysEligibleProviderSearchStub(),
            context);
    }

    private static PaymentWebhookService BuildWebhookService(
        IPaymentTransactionRepository paymentRepository, IBookingRepository bookingRepository,
        Nestly.Infrastructure.Persistence.NestlyDbContext context, IPaymentGateway gateway) =>
        new(
            paymentRepository, bookingRepository, new ServiceRepository(context), gateway,
            new CommissionService(Options.Create(new CommissionOptions())), new EscrowService(new PlatformEscrowLedgerRepository(context)),
            context, new NoOpMetricsService(), Microsoft.Extensions.Logging.Abstractions.NullLogger<PaymentWebhookService>.Instance);

    private static PaymentService BuildPaymentService(
        IPaymentTransactionRepository paymentRepository, IBookingRepository bookingRepository,
        Nestly.Infrastructure.Persistence.NestlyDbContext context, SandboxPaymentGateway gateway) =>
        new(
            paymentRepository, bookingRepository, gateway, (ISandboxPaymentSimulator)gateway,
            BuildWebhookService(paymentRepository, bookingRepository, context, gateway),
            new AlwaysEligibleProviderSearchStub());

    private sealed record SeededBooking(Guid CustomerId, Guid BookingId);

    private async Task<SeededBooking> SeedPayableBookingAsync(Nestly.Infrastructure.Persistence.NestlyDbContext context, decimal price)
    {
        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        var pincodeCode = Guid.NewGuid().ToString("N")[..6];
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Priya Nair", CustomerStatus.Active);
        var address = new CustomerAddress(
            Guid.NewGuid(), customer.Id, "Home", "12 MG Road", null, null,
            pincodeCode, "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Priya Nair", "9876500000", true);
        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        var zone = new Zone(Guid.NewGuid(), city.Id, "Central");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, pincodeCode);
        var locality = new Locality(Guid.NewGuid(), zone.Id, pincode.Id, "Indiranagar");
        address.LinkToGeography(pincode.Id, locality.Id);
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", price);
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
        return new SeededBooking(customer.Id, created.Value.Id);
    }

    private AdminPaymentReconciliationService CreateService(Nestly.Infrastructure.Persistence.NestlyDbContext context, TimeProvider timeProvider) =>
        new(new PaymentTransactionRepository(context), new BookingRepository(context), timeProvider);

    [Fact]
    public async Task GetReconciliationAsync_classifies_every_bucket_and_excludes_a_fresh_pending_order()
    {
        var gateway = BuildGateway();
        SeededBooking stuckPending, freshPending, failed, orphanedNoTransaction, orphanedVoided, paid;

        using (var seedContext = _db.CreateContext())
        {
            stuckPending = await SeedPayableBookingAsync(seedContext, 501m);
            freshPending = await SeedPayableBookingAsync(seedContext, 502m);
            failed = await SeedPayableBookingAsync(seedContext, 503m);
            orphanedNoTransaction = await SeedPayableBookingAsync(seedContext, 504m);
            orphanedVoided = await SeedPayableBookingAsync(seedContext, 505m);
            paid = await SeedPayableBookingAsync(seedContext, 506m);
        }

        Guid voidedTransactionId, stuckAttemptId;
        using (var context = _db.CreateContext())
        {
            var paymentRepository = new PaymentTransactionRepository(context);
            var bookingRepository = new BookingRepository(context);
            var paymentService = BuildPaymentService(paymentRepository, bookingRepository, context, gateway);

            // Stuck pending: order created, never resolved - backdated below
            // past the 30-minute stuck threshold. PaymentAttempt.CreatedAtUtc
            // is set from DateTime.UtcNow inside the constructor, not from an
            // injectable clock, so a real elapsed 31 minutes cannot be waited
            // out in a unit test; backdating the persisted row directly is
            // the same ExecuteUpdateAsync idiom PaymentTransactionRepository.
            // TryMarkAttemptResolvedAsync already uses on this same table.
            var stuckOrder = await paymentService.CreateOrderAsync(stuckPending.CustomerId, new CreatePaymentOrderRequest(stuckPending.BookingId, null));
            stuckAttemptId = stuckOrder.Value.AttemptId;
            await context.Set<PaymentAttempt>()
                .Where(a => a.Id == stuckAttemptId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.CreatedAtUtc, DateTime.UtcNow.AddMinutes(-31)));

            // Fresh pending: order created just now - must not show up as "needs attention" yet.
            await paymentService.CreateOrderAsync(freshPending.CustomerId, new CreatePaymentOrderRequest(freshPending.BookingId, null));

            // Failed: order created, gateway callback reports failure - booking moves to PaymentFailed.
            var failedOrder = await paymentService.CreateOrderAsync(failed.CustomerId, new CreatePaymentOrderRequest(failed.BookingId, null));
            string failedPayload = PaymentWebhookPayload.Build(failedOrder.Value.GatewayOrderId, "sandbox_declined_ref", PaymentWebhookPayload.FailedStatus);
            string failedSignature = gateway.SignPayload(failedPayload);
            var webhookService = BuildWebhookService(paymentRepository, bookingRepository, context, gateway);
            var failedCallback = await webhookService.HandleCallbackAsync(
                new PaymentWebhookRequest(failedOrder.Value.GatewayOrderId, "sandbox_declined_ref", PaymentWebhookPayload.FailedStatus, failedSignature));
            failedCallback.IsSuccess.Should().BeTrue();

            // Orphaned (voided): order created, then voided by admin - the booking stays Awaiting Payment.
            var voidedOrder = await paymentService.CreateOrderAsync(orphanedVoided.CustomerId, new CreatePaymentOrderRequest(orphanedVoided.BookingId, null));
            voidedTransactionId = voidedOrder.Value.PaymentTransactionId;
            var voidService = CreateService(context, TimeProvider.System);
            var voidResult = await voidService.VoidAsync(voidedTransactionId, "test void");
            voidResult.IsSuccess.Should().BeTrue();

            // Paid: fully succeeds, so it leaves Awaiting Payment entirely and must never appear.
            var paidOrder = await paymentService.CreateOrderAsync(paid.CustomerId, new CreatePaymentOrderRequest(paid.BookingId, null));
            string paidPayload = PaymentWebhookPayload.Build(paidOrder.Value.GatewayOrderId, "sandbox_pay_ref", PaymentWebhookPayload.SuccessStatus);
            string paidSignature = gateway.SignPayload(paidPayload);
            var paidCallback = await webhookService.HandleCallbackAsync(
                new PaymentWebhookRequest(paidOrder.Value.GatewayOrderId, "sandbox_pay_ref", PaymentWebhookPayload.SuccessStatus, paidSignature));
            paidCallback.IsSuccess.Should().BeTrue();

            // orphanedNoTransaction is left exactly as SeedPayableBookingAsync created it: PaymentPending, no transaction ever created.
        }

        using var readContext = _db.CreateContext();
        var service = CreateService(readContext, TimeProvider.System);

        var result = await service.GetReconciliationAsync(page: 1, pageSize: 100);
        result.IsSuccess.Should().BeTrue();
        var items = result.Value.Items;

        items.Should().Contain(i => i.BookingId == stuckPending.BookingId && i.Category == PaymentReconciliationCategory.StuckPending);
        items.Should().NotContain(i => i.BookingId == freshPending.BookingId);
        items.Should().Contain(i => i.BookingId == failed.BookingId && i.Category == PaymentReconciliationCategory.Failed);
        items.Should().Contain(i =>
            i.BookingId == orphanedNoTransaction.BookingId && i.Category == PaymentReconciliationCategory.Orphaned && i.PaymentTransactionId == null);
        items.Should().Contain(i =>
            i.BookingId == orphanedVoided.BookingId && i.Category == PaymentReconciliationCategory.Orphaned && i.PaymentTransactionId == voidedTransactionId);
        items.Should().NotContain(i => i.BookingId == paid.BookingId);

        result.Value.StuckPendingCount.Should().BeGreaterThanOrEqualTo(1);
        result.Value.FailedCount.Should().BeGreaterThanOrEqualTo(1);
        result.Value.OrphanedCount.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task GetReconciliationAsync_clamps_an_oversized_page_size()
    {
        using var context = _db.CreateContext();
        var service = CreateService(context, TimeProvider.System);

        var result = await service.GetReconciliationAsync(page: 1, pageSize: 1000);

        result.IsSuccess.Should().BeTrue();
        result.Value.PageSize.Should().Be(100);
    }

    [Fact]
    public async Task VoidAsync_cancels_a_pending_transaction_and_it_becomes_orphaned()
    {
        var gateway = BuildGateway();
        SeededBooking seeded = default!;
        using (var seedContext = _db.CreateContext())
        {
            seeded = await SeedPayableBookingAsync(seedContext, 700m);
        }

        Guid transactionId;
        using (var context = _db.CreateContext())
        {
            var paymentRepository = new PaymentTransactionRepository(context);
            var bookingRepository = new BookingRepository(context);
            var paymentService = BuildPaymentService(paymentRepository, bookingRepository, context, gateway);
            var order = await paymentService.CreateOrderAsync(seeded.CustomerId, new CreatePaymentOrderRequest(seeded.BookingId, null));
            transactionId = order.Value.PaymentTransactionId;
        }

        using (var context = _db.CreateContext())
        {
            var service = CreateService(context, TimeProvider.System);
            var voidResult = await service.VoidAsync(transactionId, "Ops closed a stuck order");

            voidResult.IsSuccess.Should().BeTrue();
            voidResult.Value.Id.Should().Be(transactionId);
            voidResult.Value.Status.Should().Be(PaymentTransactionStatus.Cancelled);
        }

        using (var readContext = _db.CreateContext())
        {
            var transaction = await new PaymentTransactionRepository(readContext).GetByIdAsync(transactionId);
            transaction!.Status.Should().Be(PaymentTransactionStatus.Cancelled);
            transaction.Attempts.Should().ContainSingle(a => a.FailureReason == "Ops closed a stuck order");
        }
    }

    [Fact]
    public async Task VoidAsync_returns_not_found_for_an_unknown_transaction()
    {
        using var context = _db.CreateContext();
        var service = CreateService(context, TimeProvider.System);

        var result = await service.VoidAsync(Guid.NewGuid(), null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AdminPayment.NotFound");
    }

    [Fact]
    public async Task VoidAsync_rejects_a_transaction_that_is_not_pending()
    {
        var gateway = BuildGateway();
        SeededBooking seeded = default!;
        using (var seedContext = _db.CreateContext())
        {
            seeded = await SeedPayableBookingAsync(seedContext, 900m);
        }

        Guid transactionId;
        using (var context = _db.CreateContext())
        {
            var paymentRepository = new PaymentTransactionRepository(context);
            var bookingRepository = new BookingRepository(context);
            var paymentService = BuildPaymentService(paymentRepository, bookingRepository, context, gateway);
            var order = await paymentService.CreateOrderAsync(seeded.CustomerId, new CreatePaymentOrderRequest(seeded.BookingId, null));
            transactionId = order.Value.PaymentTransactionId;

            string payload = PaymentWebhookPayload.Build(order.Value.GatewayOrderId, "sandbox_pay_ref_3", PaymentWebhookPayload.SuccessStatus);
            string signature = gateway.SignPayload(payload);
            var webhookService = BuildWebhookService(paymentRepository, bookingRepository, context, gateway);
            var callback = await webhookService.HandleCallbackAsync(
                new PaymentWebhookRequest(order.Value.GatewayOrderId, "sandbox_pay_ref_3", PaymentWebhookPayload.SuccessStatus, signature));
            callback.IsSuccess.Should().BeTrue();
        }

        using var readContext = _db.CreateContext();
        var service = CreateService(readContext, TimeProvider.System);

        var result = await service.VoidAsync(transactionId, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AdminPayment.NotVoidable");
    }
}
