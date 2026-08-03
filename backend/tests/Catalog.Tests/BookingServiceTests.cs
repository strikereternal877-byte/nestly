using FluentAssertions;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.ProviderManagement;
using Nestly.Application.Pricing;
using Nestly.Application.Serviceability;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers tasks 58-61: booking creation orchestration, snapshot persistence, customer APIs, status mapping.</summary>
public sealed class BookingServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public BookingServiceTests(TestDatabase db) => _db = db;

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
        Customer Customer, CustomerAddress Address, City City, Pincode Pincode,
        Locality Locality, Service Service, ServiceAddOn AddOn, SlotWindow Window, DateOnly Date);

    private Fixture Seed(Nestly.Infrastructure.Persistence.NestlyDbContext context)
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
        var addOn = new ServiceAddOn(Guid.NewGuid(), service.Id, "Sofa Cleaning", 150m);
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
        context.Add(addOn);
        context.ServicePincodeMappings.Add(new ServicePincodeMapping(Guid.NewGuid(), service.Id, pincode.Id));
        context.SlotWindows.Add(window);
        context.SlotWindowRules.Add(rule);
        context.SaveChanges();

        return new Fixture(customer, address, city, pincode, locality, service, addOn, window, futureDate);
    }

    private static BookingSummaryRequest RequestFor(Fixture f, IReadOnlyList<AddOnSelection>? addOns = null) => new(
        f.Service.Id, f.City.Id, f.Address.Id, f.Locality.Id, f.Window.Id, f.Date, Quantity: 1, addOns ?? []);

    [Fact]
    public async Task CreateAsync_persists_a_booking_in_PaymentPending_with_a_two_entry_timeline()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
        }

        using var createContext = _db.CreateContext();
        var created = await BuildService(createContext).CreateAsync(
            fixture.Customer.Id, RequestFor(fixture, [new AddOnSelection(fixture.AddOn.Id, 2)]));

        created.IsSuccess.Should().BeTrue();
        created.Value.Status.Should().Be(BookingStatus.PaymentPending);
        created.Value.StatusLabel.Should().Be("Awaiting Payment");
        created.Value.Timeline.Should().HaveCount(2);
        created.Value.Timeline[0].ToStatus.Should().Be(BookingStatus.Initiated);
        created.Value.Timeline[1].ToStatus.Should().Be(BookingStatus.PaymentPending);
        created.Value.AddOns.Should().ContainSingle(a => a.Id == fixture.AddOn.Id);
        created.Value.Price.TotalPayable.Should().Be(800m);

        using var readContext = _db.CreateContext();
        var reloaded = await new BookingRepository(readContext).GetByIdAsync(created.Value.Id);
        reloaded.Should().NotBeNull();
        reloaded!.Items.Should().ContainSingle();
        reloaded.Items[0].AddOns.Should().ContainSingle(a => a.LineTotalSnapshot == 300m);
    }

    [Fact]
    public async Task CreateAsync_rejects_the_same_invalid_input_the_summary_would_reject()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
        }

        using var context2 = _db.CreateContext();
        var request = RequestFor(fixture) with { SlotWindowId = Guid.NewGuid() };
        var result = await BuildService(context2).CreateAsync(fixture.Customer.Id, request);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Booking.SlotNotAvailable");
    }

    [Fact]
    public async Task GetDetailAsync_returns_not_found_for_another_customers_booking()
    {
        Fixture fixture;
        Guid bookingId;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
        }

        using (var createContext = _db.CreateContext())
        {
            var created = await BuildService(createContext).CreateAsync(fixture.Customer.Id, RequestFor(fixture));
            bookingId = created.Value.Id;
        }

        using var readContext = _db.CreateContext();
        var result = await BuildService(readContext).GetDetailAsync(Guid.NewGuid(), bookingId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Booking.NotFound");
    }

    /// <summary>
    /// Task 208 audit: Booking.Status stays "Assigned" through both the
    /// admin's offer and the provider's accept, so before this fix the
    /// customer's GetDetailAsync response had no way to distinguish the two -
    /// a provider accepting never touched Booking.Status/StatusHistory at all.
    /// ProviderAssignmentStatus (sourced from the live BookingProviderAssignment
    /// row, not the booking) is how the accept becomes visible immediately.
    /// </summary>
    [Fact]
    public async Task GetDetailAsync_reflects_a_providers_Accept_immediately()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
        }

        Guid bookingId;
        using (var createContext = _db.CreateContext())
        {
            var created = await BuildService(createContext).CreateAsync(fixture.Customer.Id, RequestFor(fixture));
            bookingId = created.Value.Id;
        }

        var beforeAssignment = await BuildService(_db.CreateContext()).GetDetailAsync(fixture.Customer.Id, bookingId);
        beforeAssignment.Value.ProviderAssignmentStatus.Should().BeNull();

        Guid providerId;
        var adminUserId = Guid.NewGuid();
        using (var setupContext = _db.CreateContext())
        {
            var booking = await new BookingRepository(setupContext).GetByIdAsync(bookingId);
            booking!.TransitionTo(BookingStatus.Confirmed);
            booking.TransitionTo(BookingStatus.AwaitingFulfilment);
            await new BookingRepository(setupContext).UpdateAsync(booking);

            var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");
            provider.ChangeStatus(ProviderStatus.Active);
            setupContext.Add(provider);
            await setupContext.SaveChangesAsync();
            providerId = provider.Id;

            var assignmentService = new BookingProviderAssignmentService(
                new BookingRepository(setupContext), new ProviderRepository(setupContext), new BookingProviderAssignmentRepository(setupContext));
            var assignResult = await assignmentService.AssignAsync(bookingId, adminUserId, new AssignProviderRequest(providerId, ResponseDeadline: null));
            assignResult.IsSuccess.Should().BeTrue();
        }

        var afterAssign = await BuildService(_db.CreateContext()).GetDetailAsync(fixture.Customer.Id, bookingId);
        afterAssign.Value.Status.Should().Be(BookingStatus.Assigned);
        afterAssign.Value.ProviderAssignmentStatus.Should().Be(BookingProviderAssignmentStatus.Assigned);

        using (var acceptContext = _db.CreateContext())
        {
            var assignmentService = new BookingProviderAssignmentService(
                new BookingRepository(acceptContext), new ProviderRepository(acceptContext), new BookingProviderAssignmentRepository(acceptContext));
            var acceptResult = await assignmentService.AcceptAsync(bookingId, providerId);
            acceptResult.IsSuccess.Should().BeTrue();
        }

        var afterAccept = await BuildService(_db.CreateContext()).GetDetailAsync(fixture.Customer.Id, bookingId);
        afterAccept.Value.Status.Should().Be(BookingStatus.Assigned, "Booking.Status has no separate accepted state by design (SRS 13.1)");
        afterAccept.Value.ProviderAssignmentStatus.Should().Be(BookingProviderAssignmentStatus.Accepted, "the accept must be visible to the customer the moment it happens");
    }

    [Fact]
    public async Task ListAsync_filters_by_bucket()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
        }

        using (var createContext = _db.CreateContext())
        {
            await BuildService(createContext).CreateAsync(fixture.Customer.Id, RequestFor(fixture));
        }

        using var readContext = _db.CreateContext();
        var service = BuildService(readContext);

        var upcoming = await service.ListAsync(fixture.Customer.Id, BookingStatusBucket.Upcoming);
        upcoming.Value.Should().ContainSingle();

        var completed = await service.ListAsync(fixture.Customer.Id, BookingStatusBucket.Completed);
        completed.Value.Should().BeEmpty();

        var all = await service.ListAsync(fixture.Customer.Id, bucket: null);
        all.Value.Should().ContainSingle();
    }
}
