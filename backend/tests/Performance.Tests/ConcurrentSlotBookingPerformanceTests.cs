using System.Diagnostics;
using FluentAssertions;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Pricing;
using Nestly.Application.Serviceability;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Performance.Tests;

/// <summary>
/// Task 135c: concurrent slot booking under promotion-level traffic (SRS
/// 29.1-29.2). Many independent customers - independent DbContexts and
/// connections, raced via Task.WhenAll, mirroring separate concurrent HTTP
/// requests - all try to book the same capacity-limited slot at once.
///
/// Before this task, SlotWindow.MaxBookingsPerSlot was reported to the
/// customer (SlotOptionResponse) but never enforced anywhere in
/// BookingService/BookingSummaryService - BookingConcurrencyTests.cs
/// documented this gap explicitly (see its
/// Two_different_customers_can_both_book_the_identical_slot... test, which
/// still passes unchanged because it never configures a capacity - null
/// stays "unlimited"). Task 135c added SlotCapacityRepository's atomic
/// conditional-update reservation (mirroring
/// CouponRepository.TryReserveRedemptionAsync) and wired it into
/// BookingService.CreateAsync via ISlotAvailabilityService.ReserveSlotAsync.
/// These tests are the proof: overbooking must be structurally impossible,
/// not just unlikely.
/// </summary>
public sealed class ConcurrentSlotBookingPerformanceTests : IClassFixture<PerfTestDatabase>
{
    private readonly PerfTestDatabase _db;

    public ConcurrentSlotBookingPerformanceTests(PerfTestDatabase db) => _db = db;

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
            summaryService,
            new BookingRepository(context),
            new CustomerRepository(context),
            couponService,
            slotAvailabilityService,
            new NoOpMetricsService(),
            new BookingProviderAssignmentRepository(context),
            new CustomerSubscriptionRepository(context));
    }

    private sealed record Fixture(
        IReadOnlyList<(Customer Customer, CustomerAddress Address)> Customers,
        Guid ServiceId, Guid CityId, Guid LocalityId, Guid SlotWindowId, DateOnly Date);

    /// <summary>Seeds one city/service/slot window plus <paramref name="customerCount"/> independent customers, each with their own address.</summary>
    private Fixture SeedSlotWithCapacity(int? capacity, int customerCount)
    {
        using var context = _db.CreateContext();

        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        var pincodeCode = Guid.NewGuid().ToString("N")[..6];

        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        var zone = new Zone(Guid.NewGuid(), city.Id, "Central");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, pincodeCode);
        var locality = new Locality(Guid.NewGuid(), zone.Id, pincode.Id, "Koramangala");
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 500m);
        var window = new SlotWindow(Guid.NewGuid(), city.Id, "Promo Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
        if (capacity is not null)
        {
            window.SetCapacity(capacity.Value);
        }

        var rule = new SlotWindowRule(Guid.NewGuid(), window.Id, futureDate.DayOfWeek);

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

        var customers = new List<(Customer, CustomerAddress)>(customerCount);
        for (int i = 0; i < customerCount; i++)
        {
            var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], $"Customer {i}", CustomerStatus.Active);
            var address = new CustomerAddress(
                Guid.NewGuid(), customer.Id, "Home", $"{i} Residency Road", null, null,
                pincodeCode, "Bengaluru", "Karnataka", 12.9716m, 77.5946m, $"Customer {i}", "9876500000", true);
            context.Add(customer);
            context.Add(address);
            customers.Add((customer, address));
        }

        context.SaveChanges();

        return new Fixture(customers, service.Id, city.Id, locality.Id, window.Id, futureDate);
    }

    private static BookingSummaryRequest RequestFor(Fixture f, Guid addressId) =>
        new(f.ServiceId, f.CityId, addressId, f.LocalityId, f.SlotWindowId, f.Date, Quantity: 1, []);

    [Fact]
    public async Task Exactly_capacity_many_concurrent_bookings_succeed_and_the_rest_are_rejected_as_conflicts()
    {
        const int capacity = 5;
        const int concurrentCustomers = 40;

        var fixture = SeedSlotWithCapacity(capacity, concurrentCustomers);

        var tasks = fixture.Customers.Select(async pair =>
        {
            using var context = _db.CreateContext();
            return await BuildBookingService(context).CreateAsync(pair.Customer.Id, RequestFor(fixture, pair.Address.Id));
        });

        var stopwatch = Stopwatch.StartNew();
        Result<BookingDetailResponse>[] results = await Task.WhenAll(tasks);
        stopwatch.Stop();

        var succeeded = results.Where(r => r.IsSuccess).ToList();
        var failed = results.Where(r => r.IsFailure).ToList();

        succeeded.Should().HaveCount(capacity, "exactly capacity-many bookings must win the race, no more and no fewer");
        failed.Should().HaveCount(concurrentCustomers - capacity);
        failed.Should().OnlyContain(r => r.Error.Code == "Booking.SlotCapacityReached");
        failed.Should().OnlyContain(r => r.Error.Type == ErrorType.Conflict, "a capacity-exhausted slot is a 409, not a validation or business-rule failure");

        // The definitive check: no more bookings were actually persisted for
        // this slot+date than the configured capacity, regardless of what the
        // in-memory Result objects claim.
        using var readContext = _db.CreateContext();
        int persistedBookings = readContext.Bookings.Count(b => b.SlotWindowId == fixture.SlotWindowId && b.SlotDate == fixture.Date);
        persistedBookings.Should().Be(capacity, "the database must never contain more bookings for a slot+date than its configured capacity");

        var counter = readContext.Set<SlotBookingCounter>()
            .Single(c => c.SlotWindowId == fixture.SlotWindowId && c.SlotDate == fixture.Date);
        counter.BookedCount.Should().Be(capacity);

        // Soft load-characteristic assertion: 40 concurrent bookings racing
        // for one slot, resolved (correctly) in well under a second's worth
        // of headroom even serialized behind SQLite's write lock. Generous on
        // purpose - this is a regression guard against a gross slowdown, not
        // a strict benchmark.
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task A_slot_with_no_configured_capacity_accepts_unlimited_concurrent_bookings()
    {
        const int concurrentCustomers = 25;
        var fixture = SeedSlotWithCapacity(capacity: null, concurrentCustomers);

        var tasks = fixture.Customers.Select(async pair =>
        {
            using var context = _db.CreateContext();
            return await BuildBookingService(context).CreateAsync(pair.Customer.Id, RequestFor(fixture, pair.Address.Id));
        });

        Result<BookingDetailResponse>[] results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(r => r.IsSuccess, "MaxBookingsPerSlot null means unlimited - capacity enforcement must not kick in at all");

        using var readContext = _db.CreateContext();
        int persistedBookings = readContext.Bookings.Count(b => b.SlotWindowId == fixture.SlotWindowId && b.SlotDate == fixture.Date);
        persistedBookings.Should().Be(concurrentCustomers);
    }

    [Fact]
    public async Task Capacity_of_one_lets_exactly_one_of_many_racing_customers_win()
    {
        const int concurrentCustomers = 15;
        var fixture = SeedSlotWithCapacity(capacity: 1, concurrentCustomers);

        var tasks = fixture.Customers.Select(async pair =>
        {
            using var context = _db.CreateContext();
            return await BuildBookingService(context).CreateAsync(pair.Customer.Id, RequestFor(fixture, pair.Address.Id));
        });

        Result<BookingDetailResponse>[] results = await Task.WhenAll(tasks);

        results.Count(r => r.IsSuccess).Should().Be(1, "the last-seat race is the sharpest case: many customers, one winner");
        results.Count(r => r.IsFailure && r.Error.Code == "Booking.SlotCapacityReached").Should().Be(concurrentCustomers - 1);
    }
}
