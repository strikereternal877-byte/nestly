using FluentAssertions;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Pricing;
using Nestly.Application.Serviceability;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Covers task 66c: concurrent slot race cases.
///
/// These are written as deterministic, sequential interleavings (seed ->
/// customer A's action -> a state change that would happen concurrently in
/// production -> customer B's action) rather than literal Task.WhenAll
/// multi-threading, for the same reason SlotAvailabilityServiceTests'
/// "between offer and confirmation" cutoff test does the same thing:
/// TestDatabase's SQLite connection is a single shared connection held open
/// for the fixture's lifetime (see TestDatabase.cs), and Microsoft.Data.Sqlite
/// does not support issuing commands concurrently on one connection from
/// multiple threads - a literal Task.WhenAll here would fail with
/// connection-contention errors that have nothing to do with the actual
/// business race being tested. Forcing a specific interleaving is the
/// standard, deterministic way to test a time-of-check/time-of-use race
/// without flakiness.
/// </summary>
public sealed class BookingConcurrencyTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public BookingConcurrencyTests(TestDatabase db) => _db = db;

    private BookingService BuildService(Nestly.Infrastructure.Persistence.NestlyDbContext context)
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

    private sealed record Fixture(
        Customer CustomerA, Customer CustomerB, CustomerAddress AddressA, CustomerAddress AddressB,
        City City, Pincode Pincode, Locality Locality, Service Service, SlotWindow Window, DateOnly Date);

    private Fixture SeedTwoCustomers(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        var pincodeCode = Guid.NewGuid().ToString("N")[..6];

        var customerA = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        var customerB = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Ravi Kumar", CustomerStatus.Active);
        var addressA = new CustomerAddress(
            Guid.NewGuid(), customerA.Id, "Home", "221B Baker Street", null, null,
            pincodeCode, "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210", true);
        var addressB = new CustomerAddress(
            Guid.NewGuid(), customerB.Id, "Home", "18 Residency Road", null, null,
            pincodeCode, "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Ravi Kumar", "9876500000", true);

        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        var zone = new Zone(Guid.NewGuid(), city.Id, "Central");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, pincodeCode);
        var locality = new Locality(Guid.NewGuid(), zone.Id, pincode.Id, "Koramangala");
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 500m);
        var window = new SlotWindow(Guid.NewGuid(), city.Id, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
        var rule = new SlotWindowRule(Guid.NewGuid(), window.Id, futureDate.DayOfWeek);

        context.Add(customerA);
        context.Add(customerB);
        context.Add(addressA);
        context.Add(addressB);
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

        return new Fixture(customerA, customerB, addressA, addressB, city, pincode, locality, service, window, futureDate);
    }

    private static BookingSummaryRequest RequestFor(Fixture f, Guid addressId) => new(
        f.Service.Id, f.City.Id, addressId, f.Locality.Id, f.Window.Id, f.Date, Quantity: 1, []);

    /// <summary>
    /// As of task 135c, BookingService.CreateAsync enforces
    /// SlotWindow.MaxBookingsPerSlot via ISlotAvailabilityService.ReserveSlotAsync
    /// (see SlotCapacityRepository) - but only when a window actually has a
    /// capacity configured. This fixture's window never calls SetCapacity, so
    /// MaxBookingsPerSlot stays null ("unlimited", the same convention
    /// ProviderCapacity uses), and reservation is a no-op. This test pins down
    /// that an unlimited window still lets two different customers book the
    /// exact same slot - see Performance.Tests/ConcurrentSlotBookingPerformanceTests.cs
    /// for the capacity-configured, actually-contended case, which is where
    /// the enforcement itself is proven under real concurrent load.
    /// </summary>
    [Fact]
    public async Task Two_different_customers_can_both_book_the_identical_slot_since_capacity_is_not_enforced_at_creation_yet()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = SeedTwoCustomers(context);
        }

        Result<BookingDetailResponse> resultA;
        using (var contextA = _db.CreateContext())
        {
            resultA = await BuildService(contextA).CreateAsync(fixture.CustomerA.Id, RequestFor(fixture, fixture.AddressA.Id));
        }

        Result<BookingDetailResponse> resultB;
        using (var contextB = _db.CreateContext())
        {
            resultB = await BuildService(contextB).CreateAsync(fixture.CustomerB.Id, RequestFor(fixture, fixture.AddressB.Id));
        }

        resultA.IsSuccess.Should().BeTrue();
        resultB.IsSuccess.Should().BeTrue();
        resultA.Value.Id.Should().NotBe(resultB.Value.Id);
        resultA.Value.Slot.SlotWindowId.Should().Be(resultB.Value.Slot.SlotWindowId);

        using var readContext = _db.CreateContext();
        var repo = new BookingRepository(readContext);
        (await repo.GetByIdAsync(resultA.Value.Id)).Should().NotBeNull();
        (await repo.GetByIdAsync(resultB.Value.Id)).Should().NotBeNull();
    }

    /// <summary>
    /// The realistic race: customer A checks out first and succeeds: the
    /// slot's date is then blacked out by an admin (or every window on it
    /// fills some other city-level policy) before customer B - who was
    /// looking at the same now-stale summary - completes checkout. B's
    /// creation must fail gracefully (SRS 11.8.3), and A's already-persisted
    /// booking must be completely unaffected by B's failed attempt.
    /// </summary>
    [Fact]
    public async Task A_slot_that_goes_stale_between_two_customers_checkout_blocks_only_the_customer_who_checks_out_after_it_goes_stale()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = SeedTwoCustomers(context);
        }

        Result<BookingDetailResponse> resultA;
        using (var contextA = _db.CreateContext())
        {
            resultA = await BuildService(contextA).CreateAsync(fixture.CustomerA.Id, RequestFor(fixture, fixture.AddressA.Id));
        }

        resultA.IsSuccess.Should().BeTrue();

        // An admin blacks out the date between the two customers' checkouts.
        using (var blackoutContext = _db.CreateContext())
        {
            blackoutContext.SlotBlackouts.Add(new SlotBlackout(
                Guid.NewGuid(), fixture.City.Id, fixture.Date, fixture.Date, SlotBlackoutType.Blackout, "Emergency capacity block"));
            blackoutContext.SaveChanges();
        }

        Result<BookingDetailResponse> resultB;
        using (var contextB = _db.CreateContext())
        {
            resultB = await BuildService(contextB).CreateAsync(fixture.CustomerB.Id, RequestFor(fixture, fixture.AddressB.Id));
        }

        resultB.IsFailure.Should().BeTrue();
        resultB.Error.Code.Should().Be("Booking.SlotNotAvailable");

        // Customer A's booking must be untouched by B's failed attempt.
        using var readContext = _db.CreateContext();
        var reloadedA = await new BookingRepository(readContext).GetByIdAsync(resultA.Value.Id);
        reloadedA.Should().NotBeNull();
        reloadedA!.Status.Should().Be(BookingStatus.PaymentPending);
        reloadedA.SlotWindowId.Should().Be(fixture.Window.Id);

        var allForCustomerB = await new BookingRepository(readContext)
            .ListByCustomerAsync(fixture.CustomerB.Id, Enum.GetValues<BookingStatus>());
        allForCustomerB.Should().BeEmpty("the failed CreateAsync call must not have left a partial booking behind");
    }

    /// <summary>
    /// The same customer double-submitting (e.g. a duplicated click, or two
    /// browser tabs) is not deduplicated anywhere in BookingService today -
    /// there is no idempotency key on BookingSummaryRequest/POST /bookings.
    /// Documented here as current, verified behaviour: both requests succeed
    /// as two independent bookings rather than one silently overwriting or
    /// erroring against the other.
    /// </summary>
    [Fact]
    public async Task The_same_customer_submitting_the_identical_request_twice_creates_two_independent_bookings()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = SeedTwoCustomers(context);
        }

        var request = RequestFor(fixture, fixture.AddressA.Id);

        Result<BookingDetailResponse> first;
        using (var context1 = _db.CreateContext())
        {
            first = await BuildService(context1).CreateAsync(fixture.CustomerA.Id, request);
        }

        Result<BookingDetailResponse> second;
        using (var context2 = _db.CreateContext())
        {
            second = await BuildService(context2).CreateAsync(fixture.CustomerA.Id, request);
        }

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        first.Value.Id.Should().NotBe(second.Value.Id);

        using var readContext = _db.CreateContext();
        var bookings = await new BookingRepository(readContext)
            .ListByCustomerAsync(fixture.CustomerA.Id, Enum.GetValues<BookingStatus>());
        bookings.Should().HaveCount(2);
    }
}
