using FluentAssertions;
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
/// The admin payment transaction view's read side (SRS 12.13.1, task 311) -
/// list/filter/paginate and per-transaction detail (attempts + refunds).
/// Seeding mirrors <see cref="PaymentReconciliationTests"/> exactly (real
/// Customer/Booking rows are required by <c>payment_transaction</c>'s FK,
/// see <c>PaymentTransactionConfiguration</c>), since that is the only way
/// to get a valid <see cref="PaymentTransaction"/> row into the database.
/// </summary>
public sealed class AdminPaymentQueryServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public AdminPaymentQueryServiceTests(TestDatabase db) => _db = db;

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

    private sealed record SeededBooking(Guid CustomerId, Guid BookingId);

    private async Task<SeededBooking> SeedPayableBookingAsync(Nestly.Infrastructure.Persistence.NestlyDbContext context, decimal price)
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

    private AdminPaymentQueryService CreateService(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(new PaymentTransactionRepository(context), new RefundTransactionRepository(context));

    [Fact]
    public async Task SearchAsync_filters_by_status_and_returns_the_total_match_count()
    {
        var gateway = BuildGateway();
        SeededBooking pending, succeeded;

        using (var seedContext = _db.CreateContext())
        {
            pending = await SeedPayableBookingAsync(seedContext, 501m);
            succeeded = await SeedPayableBookingAsync(seedContext, 777m);
        }

        Guid succeededTransactionId;
        using (var context = _db.CreateContext())
        {
            var paymentRepository = new PaymentTransactionRepository(context);
            var bookingRepository = new BookingRepository(context);
            var paymentService = new PaymentService(
                paymentRepository, bookingRepository, gateway, (ISandboxPaymentSimulator)gateway,
                BuildWebhookService(paymentRepository, bookingRepository, context, gateway),
                new AlwaysEligibleProviderSearchStub());

            await paymentService.CreateOrderAsync(pending.CustomerId, new CreatePaymentOrderRequest(pending.BookingId, null));
            var succeededOrder = await paymentService.CreateOrderAsync(succeeded.CustomerId, new CreatePaymentOrderRequest(succeeded.BookingId, null));
            succeededTransactionId = succeededOrder.Value.PaymentTransactionId;

            string payload = PaymentWebhookPayload.Build(succeededOrder.Value.GatewayOrderId, "sandbox_pay_ref", PaymentWebhookPayload.SuccessStatus);
            string signature = gateway.SignPayload(payload);
            var webhookService = BuildWebhookService(paymentRepository, bookingRepository, context, gateway);
            var callback = await webhookService.HandleCallbackAsync(
                new PaymentWebhookRequest(succeededOrder.Value.GatewayOrderId, "sandbox_pay_ref", PaymentWebhookPayload.SuccessStatus, signature));
            callback.IsSuccess.Should().BeTrue();
        }

        using var readContext = _db.CreateContext();
        var service = CreateService(readContext);

        var succeededOnly = await service.SearchAsync(new AdminPaymentTransactionFilterRequest(Status: PaymentTransactionStatus.Success));
        succeededOnly.IsSuccess.Should().BeTrue();
        succeededOnly.Value.Items.Should().Contain(i => i.Id == succeededTransactionId);
        succeededOnly.Value.Items.Should().OnlyContain(i => i.Status == PaymentTransactionStatus.Success);

        var byBooking = await service.SearchAsync(new AdminPaymentTransactionFilterRequest(BookingId: pending.BookingId));
        byBooking.Value.TotalCount.Should().Be(1);
        byBooking.Value.Items.Single().BookingId.Should().Be(pending.BookingId);

        var succeededItem = succeededOnly.Value.Items.Single(i => i.Id == succeededTransactionId);
        succeededItem.LatestGatewayPaymentRef.Should().Be("sandbox_pay_ref");
    }

    [Fact]
    public async Task SearchAsync_echoes_the_clamped_page_size_and_paginates_by_booking()
    {
        var seeded = new List<SeededBooking>();
        using (var seedContext = _db.CreateContext())
        {
            seeded.Add(await SeedPayableBookingAsync(seedContext, 601m));
            seeded.Add(await SeedPayableBookingAsync(seedContext, 602m));
            seeded.Add(await SeedPayableBookingAsync(seedContext, 603m));
        }

        using (var context = _db.CreateContext())
        {
            var paymentRepository = new PaymentTransactionRepository(context);
            var bookingRepository = new BookingRepository(context);
            var gateway = BuildGateway();
            var paymentService = new PaymentService(
                paymentRepository, bookingRepository, gateway, (ISandboxPaymentSimulator)gateway,
                BuildWebhookService(paymentRepository, bookingRepository, context, gateway),
                new AlwaysEligibleProviderSearchStub());

            foreach (var booking in seeded)
            {
                await paymentService.CreateOrderAsync(booking.CustomerId, new CreatePaymentOrderRequest(booking.BookingId, null));
            }
        }

        using var readContext = _db.CreateContext();
        var service = CreateService(readContext);

        // An oversized page size is clamped, same as AuditLogQueryService.
        var oversized = await service.SearchAsync(new AdminPaymentTransactionFilterRequest(PageSize: 1000));
        oversized.Value.PageSize.Should().Be(100);

        // Each of this test's three bookings has exactly one transaction -
        // filtering by booking id isolates this assertion from every other
        // transaction TestDatabase's shared tables may already hold.
        foreach (var booking in seeded)
        {
            var byBooking = await service.SearchAsync(new AdminPaymentTransactionFilterRequest(BookingId: booking.BookingId));
            byBooking.Value.TotalCount.Should().Be(1);
            byBooking.Value.Items.Single().BookingId.Should().Be(booking.BookingId);
        }
    }

    [Fact]
    public async Task GetDetailAsync_returns_attempts_and_refunds_for_a_real_transaction()
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
            var paymentService = new PaymentService(
                paymentRepository, bookingRepository, gateway, (ISandboxPaymentSimulator)gateway,
                BuildWebhookService(paymentRepository, bookingRepository, context, gateway),
                new AlwaysEligibleProviderSearchStub());

            var order = await paymentService.CreateOrderAsync(seeded.CustomerId, new CreatePaymentOrderRequest(seeded.BookingId, null));
            transactionId = order.Value.PaymentTransactionId;

            string payload = PaymentWebhookPayload.Build(order.Value.GatewayOrderId, "sandbox_pay_ref_2", PaymentWebhookPayload.SuccessStatus);
            string signature = gateway.SignPayload(payload);
            var webhookService = BuildWebhookService(paymentRepository, bookingRepository, context, gateway);
            var callback = await webhookService.HandleCallbackAsync(
                new PaymentWebhookRequest(order.Value.GatewayOrderId, "sandbox_pay_ref_2", PaymentWebhookPayload.SuccessStatus, signature));
            callback.IsSuccess.Should().BeTrue();

            // A refund raised against the now-succeeded transaction - seeded
            // directly (RefundService's own workflow is covered by
            // RefundServiceTests) so this test only exercises the read side.
            var refund = RefundTransaction.ForPayment(
                Guid.NewGuid(), seeded.BookingId, transactionId, RefundType.Partial, RefundMethod.Wallet, 100m, "Partial cancellation");
            await new RefundTransactionRepository(context).AddAsync(refund);
        }

        using var readContext = _db.CreateContext();
        var service = CreateService(readContext);

        var detail = await service.GetDetailAsync(transactionId);
        detail.IsSuccess.Should().BeTrue();
        detail.Value.Id.Should().Be(transactionId);
        detail.Value.BookingId.Should().Be(seeded.BookingId);
        detail.Value.Status.Should().Be(PaymentTransactionStatus.Success);
        detail.Value.Attempts.Should().ContainSingle(a => a.GatewayPaymentRef == "sandbox_pay_ref_2");
        detail.Value.Refunds.Should().ContainSingle(r => r.Amount == 100m && r.Method == RefundMethod.Wallet);
    }

    [Fact]
    public async Task GetDetailAsync_returns_not_found_for_an_unknown_id()
    {
        using var context = _db.CreateContext();
        var service = CreateService(context);

        var result = await service.GetDetailAsync(Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AdminPayment.NotFound");
    }
}
