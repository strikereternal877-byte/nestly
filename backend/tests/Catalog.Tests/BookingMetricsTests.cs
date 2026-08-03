using FluentAssertions;
using Nestly.Application;
using Nestly.Application.Abstractions.Observability;
using Nestly.Application.Bookings;
using Nestly.Application.Pricing;
using Nestly.Application.Serviceability;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Covers task 137b: BookingService.CreateAsync's IMetricsService wiring -
/// success/failure counters and, specifically, that a slot-capacity rejection
/// (SlotAvailabilityService.ReserveSlotAsync's "Booking.SlotCapacityReached"
/// error) is reported through both RecordSlotConflict and RecordBookingCreated,
/// not just folded silently into the generic failure count.
/// </summary>
public sealed class BookingMetricsTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public BookingMetricsTests(TestDatabase db) => _db = db;

    private static BookingService BuildService(Nestly.Infrastructure.Persistence.NestlyDbContext context, IMetricsService metricsService)
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
            metricsService,
            new BookingProviderAssignmentRepository(context),
            new CustomerSubscriptionRepository(context));
    }

    private sealed record Fixture(Customer Customer, CustomerAddress Address, City City, Locality Locality, Service Service, SlotWindow Window, DateOnly Date);

    /// <summary>Same shape as BookingServiceTests.Seed, plus an optional per-day capacity on the slot window so the conflict path is reachable.</summary>
    private Fixture Seed(Nestly.Infrastructure.Persistence.NestlyDbContext context, int? slotCapacity = null)
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
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 500m);
        var window = new SlotWindow(Guid.NewGuid(), city.Id, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
        if (slotCapacity is not null)
        {
            window.SetCapacity(slotCapacity.Value);
        }

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

        return new Fixture(customer, address, city, locality, service, window, futureDate);
    }

    private static BookingSummaryRequest RequestFor(Fixture f) => new(
        f.Service.Id, f.City.Id, f.Address.Id, f.Locality.Id, f.Window.Id, f.Date, Quantity: 1, []);

    [Fact]
    public async Task CreateAsync_records_a_successful_outcome_on_the_happy_path()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
        }

        var recorder = new RecordingMetricsService();
        using var createContext = _db.CreateContext();
        var result = await BuildService(createContext, recorder).CreateAsync(fixture.Customer.Id, RequestFor(fixture));

        result.IsSuccess.Should().BeTrue();
        recorder.BookingOutcomes.Should().ContainSingle(o => o.Succeeded);
        recorder.SlotConflicts.Should().Be(0);
    }

    [Fact]
    public async Task CreateAsync_records_a_slot_conflict_and_a_failure_outcome_when_the_slot_is_at_capacity()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            // Capacity of 1: the first booking below consumes the only seat,
            // so the second request must be rejected with
            // Booking.SlotCapacityReached.
            fixture = Seed(context, slotCapacity: 1);
        }

        using (var firstBookingContext = _db.CreateContext())
        {
            var firstResult = await BuildService(firstBookingContext, new RecordingMetricsService()).CreateAsync(fixture.Customer.Id, RequestFor(fixture));
            firstResult.IsSuccess.Should().BeTrue("the fixture's single slot seat should still be free for the first booking");
        }

        var recorder = new RecordingMetricsService();
        using var secondBookingContext = _db.CreateContext();
        var secondResult = await BuildService(secondBookingContext, recorder).CreateAsync(fixture.Customer.Id, RequestFor(fixture));

        secondResult.IsFailure.Should().BeTrue();
        secondResult.Error.Code.Should().Be("Booking.SlotCapacityReached");
        recorder.SlotConflicts.Should().Be(1);
        recorder.BookingOutcomes.Should().ContainSingle(o => !o.Succeeded && o.FailureReason == "Booking.SlotCapacityReached");
    }

    /// <summary>Records every call made through it, for assertions Verify-based mocking would otherwise need a library for.</summary>
    private sealed class RecordingMetricsService : IMetricsService
    {
        public List<(bool Succeeded, string? FailureReason)> BookingOutcomes { get; } = [];

        public int SlotConflicts { get; private set; }

        public void RecordPaymentOutcome(bool succeeded, TimeSpan processingDuration, string? failureReason = null)
        {
        }

        public void RecordBookingCreated(bool succeeded, string? failureReason = null) => BookingOutcomes.Add((succeeded, failureReason));

        public void RecordBookingStatusTransition(string fromStatus, string toStatus)
        {
        }

        public void RecordSlotConflict() => SlotConflicts++;

        public void RecordNotificationOutcome(string channel, bool succeeded, string? failureReason = null)
        {
        }
    }
}
