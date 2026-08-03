using System.Diagnostics;
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
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Performance.Tests;

/// <summary>
/// Task 135b: load/performance testing for checkout (SRS 29.1-29.2) - the
/// booking-creation-through-payment-order path (POST /bookings then POST
/// /payments/orders), driven by many independent customers checking out
/// concurrently.
///
/// Each simulated customer gets their own address and their own slot window
/// (so this test measures checkout throughput in isolation, not slot
/// contention - see ConcurrentSlotBookingPerformanceTests for the
/// same-slot-contention case, task 135c's specific concern). No
/// MaxBookingsPerSlot is configured here either, for the same reason.
/// </summary>
public sealed class CheckoutPerformanceTests : IClassFixture<PerfTestDatabase>
{
    private readonly PerfTestDatabase _db;

    public CheckoutPerformanceTests(PerfTestDatabase db) => _db = db;

    private static SandboxPaymentGateway BuildGateway() =>
        new(Options.Create(new SandboxGatewayOptions { WebhookSigningSecret = "perf-test-signing-secret-value" }));

    private static BookingService BuildBookingService(NestlyDbContext context)
    {
        var couponService = new CouponService(
            new CouponRepository(context),
            new CouponRedemptionRepository(context),
            new BookingRepository(context),
            TimeProvider.System);

        var slotAvailabilityService = new SlotAvailabilityService(
            new ServiceabilityRepository(context),
            new ServiceabilityValidationService(new ServiceabilityRepository(context), new InMemoryCacheService()),
            new SlotWindowRepository(context),
            new SlotBlackoutRepository(context),
            new SlotBookingPolicyRepository(context),
            new SlotCapacityRepository(context),
            TimeProvider.System);

        var summaryService = new BookingSummaryService(
            new ServiceRepository(context),
            new ServiceAddOnRepository(context),
            new CustomerAddressRepository(context),
            slotAvailabilityService,
            new PriceCalculationService(
                new ServiceRepository(context),
                new ServiceAddOnRepository(context),
                new ServiceabilityRepository(context),
                new ServiceCityPriceRepository(context),
                new CityPricingPolicyRepository(context)),
            couponService,
            new SubscriptionBenefitService(new CustomerSubscriptionRepository(context)));

        return new BookingService(
            summaryService, new BookingRepository(context), new CustomerRepository(context), couponService, slotAvailabilityService,
            new NoOpMetricsService(),
            new BookingProviderAssignmentRepository(context),
            new CustomerSubscriptionRepository(context));
    }

    private static PaymentService BuildPaymentService(NestlyDbContext context, IPaymentGateway gateway)
    {
        var paymentRepository = new PaymentTransactionRepository(context);
        var bookingRepository = new BookingRepository(context);
        var simulator = (ISandboxPaymentSimulator)gateway;
        var webhookService = new PaymentWebhookService(
            paymentRepository, bookingRepository, new ServiceRepository(context), gateway,
            new CommissionService(Options.Create(new CommissionOptions())), new EscrowService(new PlatformEscrowLedgerRepository(context)),
            context, new NoOpMetricsService(), NullLogger<PaymentWebhookService>.Instance);

        return new PaymentService(paymentRepository, bookingRepository, gateway, simulator, webhookService);
    }

    private sealed record CustomerFixture(Customer Customer, CustomerAddress Address, Guid ServiceId, Guid CityId, Guid LocalityId, Guid SlotWindowId, DateOnly Date);

    /// <summary>Seeds one independent, fully serviceable checkout scenario per simulated customer - own address, own slot window, so nobody contends with anybody else.</summary>
    private IReadOnlyList<CustomerFixture> SeedIndependentCheckouts(int customerCount)
    {
        using var context = _db.CreateContext();
        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));

        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 500m);
        context.States.Add(state);
        context.Cities.Add(city);
        context.Add(category);
        context.Add(service);

        var fixtures = new List<CustomerFixture>(customerCount);
        for (int i = 0; i < customerCount; i++)
        {
            var pincodeCode = Guid.NewGuid().ToString("N")[..6];
            var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], $"Customer {i}", CustomerStatus.Active);
            var address = new CustomerAddress(
                Guid.NewGuid(), customer.Id, "Home", $"{i} Residency Road", null, null,
                pincodeCode, "Bengaluru", "Karnataka", 12.9716m, 77.5946m, $"Customer {i}", "9876500000", true);
            var zone = new Zone(Guid.NewGuid(), city.Id, $"Zone {i}");
            var pincode = new Pincode(Guid.NewGuid(), city.Id, pincodeCode);
            var locality = new Locality(Guid.NewGuid(), zone.Id, pincode.Id, $"Locality {i}");
            var window = new SlotWindow(Guid.NewGuid(), city.Id, $"Morning {i}", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
            var rule = new SlotWindowRule(Guid.NewGuid(), window.Id, futureDate.DayOfWeek);

            context.Add(customer);
            context.Add(address);
            context.Zones.Add(zone);
            context.Pincodes.Add(pincode);
            context.Localities.Add(locality);
            context.ServicePincodeMappings.Add(new ServicePincodeMapping(Guid.NewGuid(), service.Id, pincode.Id));
            context.SlotWindows.Add(window);
            context.SlotWindowRules.Add(rule);

            fixtures.Add(new CustomerFixture(customer, address, service.Id, city.Id, locality.Id, window.Id, futureDate));
        }

        context.SaveChanges();
        return fixtures;
    }

    private static BookingSummaryRequest RequestFor(CustomerFixture f) =>
        new(f.ServiceId, f.CityId, f.Address.Id, f.LocalityId, f.SlotWindowId, f.Date, Quantity: 1, []);

    [Fact]
    public async Task Many_concurrent_customers_can_check_out_and_place_a_payment_order_at_the_same_time()
    {
        const int concurrentCustomers = 60;
        var fixtures = SeedIndependentCheckouts(concurrentCustomers);
        var gateway = BuildGateway();

        var tasks = fixtures.Select(async f =>
        {
            using var context = _db.CreateContext();
            var bookingResult = await BuildBookingService(context).CreateAsync(f.Customer.Id, RequestFor(f));
            if (bookingResult.IsFailure)
            {
                return bookingResult.Error;
            }

            var orderResult = await BuildPaymentService(context, gateway).CreateOrderAsync(
                f.Customer.Id, new CreatePaymentOrderRequest(bookingResult.Value.Id, IdempotencyKey: null));

            return orderResult.IsFailure ? orderResult.Error : Nestly.BuildingBlocks.Results.Error.None;
        });

        var stopwatch = Stopwatch.StartNew();
        var errors = await Task.WhenAll(tasks);
        stopwatch.Stop();

        errors.Should().OnlyContain(e => e == Nestly.BuildingBlocks.Results.Error.None, "every independent checkout must succeed end to end under concurrent load");

        using var readContext = _db.CreateContext();
        int bookingCount = readContext.Bookings.Count(b => b.Status == BookingStatus.PaymentPending);
        bookingCount.Should().Be(concurrentCustomers);

        int transactionCount = readContext.PaymentTransactions.Count();
        transactionCount.Should().Be(concurrentCustomers, "each booking must have exactly one payment transaction, with no duplicate or missing orders under load");

        // Soft load-characteristic assertion (regression guard, not a strict
        // benchmark): 60 full checkout+payment-order flows concurrently.
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_customer_double_submitting_checkout_concurrently_gets_the_same_idempotent_payment_order()
    {
        var fixtures = SeedIndependentCheckouts(1);
        var fixture = fixtures[0];
        var gateway = BuildGateway();

        Guid bookingId;
        using (var seedContext = _db.CreateContext())
        {
            var created = await BuildBookingService(seedContext).CreateAsync(fixture.Customer.Id, RequestFor(fixture));
            created.IsSuccess.Should().BeTrue();
            bookingId = created.Value.Id;
        }

        var idempotencyKey = Guid.NewGuid().ToString();

        // A plain Task.WhenAll over unsynchronized tasks is not a reliable
        // way to hit a narrow time-of-check/time-of-use window - the reads
        // are so fast relative to scheduling jitter that the tasks can
        // easily run near-sequentially and never actually race. A start gate
        // forces every task's CreateOrderAsync call to begin at the same
        // instant, which is what actually exercises the gap between
        // PaymentService.CreateOrderAsync's "read existing transaction" and
        // "insert a new one" under real contention.
        using var startGate = new SemaphoreSlim(0);
        const int concurrentSubmits = 20;

        var tasks = Enumerable.Range(0, concurrentSubmits).Select(async _ =>
        {
            using var context = _db.CreateContext();
            var paymentService = BuildPaymentService(context, gateway);
            await startGate.WaitAsync();
            return await paymentService.CreateOrderAsync(
                fixture.Customer.Id, new CreatePaymentOrderRequest(bookingId, idempotencyKey));
        }).ToList();

        await Task.Delay(TimeSpan.FromMilliseconds(100));
        startGate.Release(concurrentSubmits);

        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(r => r.IsSuccess);
        results.Select(r => r.Value.PaymentTransactionId).Distinct().Should().ContainSingle(
            "double-submitting the same idempotency key concurrently must still resolve to exactly one payment transaction");

        using var readContext = _db.CreateContext();
        var transaction = await new PaymentTransactionRepository(readContext).GetByBookingIdAsync(bookingId);
        transaction!.Attempts.Should().ContainSingle("no duplicate gateway order should have been minted by the concurrent double-submit");
    }
}
