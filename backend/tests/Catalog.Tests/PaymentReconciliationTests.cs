using FluentAssertions;
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

/// <summary>
/// Covers task 71 (SRS 14.3 "payment and booking reconciliation should be
/// possible"): every payment transaction is a queryable record, filterable
/// by date range and status - the shape a reconciliation job or admin report
/// needs. There is deliberately no HTTP endpoint for this yet: admin
/// authentication/RBAC doesn't exist until Phase 6, and exposing a raw
/// financial-transaction dump with no access control would itself be the
/// "broken access control" SRS 28.3 warns against. IPaymentTransactionRepository.ListAsync
/// is the capability; Phase 6's admin API wires a controller on top of it.
/// </summary>
public sealed class PaymentReconciliationTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public PaymentReconciliationTests(TestDatabase db) => _db = db;

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

    [Fact]
    public async Task ListAsync_supports_filtering_transaction_records_by_status_for_reconciliation()
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
                BuildWebhookService(paymentRepository, bookingRepository, context, gateway));

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
        var repository = new PaymentTransactionRepository(readContext);

        var succeededOnly = await repository.ListAsync(fromUtc: null, toUtc: null, status: PaymentTransactionStatus.Success);
        succeededOnly.Should().Contain(t => t.Id == succeededTransactionId);
        succeededOnly.Should().OnlyContain(t => t.Status == PaymentTransactionStatus.Success);

        var pendingOnly = await repository.ListAsync(fromUtc: null, toUtc: null, status: PaymentTransactionStatus.Pending);
        pendingOnly.Should().OnlyContain(t => t.Status == PaymentTransactionStatus.Pending);
        pendingOnly.Should().NotContain(t => t.Id == succeededTransactionId);

        var future = DateTime.UtcNow.AddDays(1);
        var outsideDateRange = await repository.ListAsync(fromUtc: future, toUtc: null, status: null);
        outsideDateRange.Should().BeEmpty("both transactions were created before the reconciliation window starts");

        var all = await repository.ListAsync(fromUtc: null, toUtc: null, status: null);
        all.Should().HaveCountGreaterThanOrEqualTo(2);
        // Every record traces back to its booking, satisfying SRS 11.11.3's
        // "booking-payment mapping" for reconciliation purposes.
        all.Should().OnlyContain(t => t.BookingId != Guid.Empty);
    }
}
