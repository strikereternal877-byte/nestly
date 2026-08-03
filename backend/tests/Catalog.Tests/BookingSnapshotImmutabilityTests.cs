using FluentAssertions;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Pricing;
using Nestly.Application.Serviceability;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Covers task 66b: snapshot immutability (SRS 14.1, 23.3). Two distinct
/// guarantees, both tested here:
///
/// 1. A persisted booking's snapshot fields never move even after the
///    catalog/address rows they were copied from are later edited - this is
///    the actual "snapshot" contract (task 59), verified end-to-end through
///    the database rather than just by inspecting the in-memory object graph.
/// 2. Once a booking leaves Initiated, its item/add-on list is frozen - not
///    just against a brand-new AddItem call, but against a caller still
///    holding a BookingItem reference obtained before the transition
///    (task 56d, and the AddAddOnToItem guard this suite motivated).
/// </summary>
public sealed class BookingSnapshotImmutabilityTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public BookingSnapshotImmutabilityTests(TestDatabase db) => _db = db;

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
    public async Task Booking_snapshot_is_unaffected_by_later_changes_to_the_source_service_addon_and_address()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
        }

        Guid bookingId;
        using (var createContext = _db.CreateContext())
        {
            var created = await BuildService(createContext).CreateAsync(
                fixture.Customer.Id, RequestFor(fixture, [new AddOnSelection(fixture.AddOn.Id, 1)]));
            created.IsSuccess.Should().BeTrue();
            bookingId = created.Value.Id;
        }

        // Mutate every source row the snapshot was copied from - a real
        // catalog price change, a renamed service, a re-priced add-on, an
        // edited address, and a renamed customer - all well after the
        // booking was placed.
        using (var editContext = _db.CreateContext())
        {
            var service = editContext.Set<Service>().Single(s => s.Id == fixture.Service.Id);
            service.SetPrice(999m);
            service.SetName("Deep Clean XL");

            var addOn = editContext.Set<ServiceAddOn>().Single(a => a.Id == fixture.AddOn.Id);
            addOn.SetPrice(500m);

            var address = editContext.Set<CustomerAddress>().Single(a => a.Id == fixture.Address.Id);
            address.Update(
                "Work", "742 Evergreen Terrace", null, null, "999999", "Pune", "Maharashtra",
                18.5204m, 73.8567m, "Someone Else", "9999999999");

            var customer = editContext.Set<Customer>().Single(c => c.Id == fixture.Customer.Id);
            customer.UpdateProfile("Asha Rao-Verma", customer.Email);

            editContext.SaveChanges();
        }

        using var readContext = _db.CreateContext();
        var reloaded = await new BookingRepository(readContext).GetByIdAsync(bookingId);

        reloaded.Should().NotBeNull();
        reloaded!.Items[0].NameSnapshot.Should().Be("Deep Clean");
        reloaded.Items[0].UnitPriceSnapshot.Should().Be(500m);
        reloaded.Items[0].AddOns[0].UnitPriceSnapshot.Should().Be(150m);
        reloaded.AddressLine1Snapshot.Should().Be("221B Baker Street");
        reloaded.AddressCitySnapshot.Should().Be("Bengaluru");
        reloaded.AddressContactNameSnapshot.Should().Be("Asha Rao");
        reloaded.CustomerNameSnapshot.Should().Be("Asha Rao");
        // Same fixture shape as BookingSummaryServiceTests' 650m total (base
        // 500 + add-on 150, no tax/platform-fee policy seeded) - pinned here
        // to prove the total does not silently recompute against the new
        // service/add-on prices set above (999m / 500m).
        reloaded.TotalPayableSnapshot.Should().Be(650m);
        reloaded.BasePriceSnapshot.Should().Be(500m);
    }

    [Fact]
    public void AddAddOnToItem_is_rejected_once_the_booking_has_moved_past_Initiated_even_with_an_item_reference_from_before_the_transition()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
        }

        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Ravi Kumar", CustomerStatus.Active);
        var address = new AddressSnapshot("Home", "12 MG Road", null, null, "560001", "Bengaluru", "Karnataka", 12.97m, 77.59m, "Ravi Kumar", "9876500000");
        var slot = new SlotSnapshot(Guid.NewGuid(), fixture.Date, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
        var price = new PriceSnapshot(500m, 1, 500m, 0m, 0m, 500m, 18m, 90m, 0m, 590m);

        var booking = new Booking(Guid.NewGuid(), customer.Id, new CustomerSnapshot(customer.Name, customer.Mobile), null, address, slot, price);
        var item = booking.AddItem(Guid.NewGuid(), fixture.Service.Id, "Deep Clean", "deep-clean", 500m, 1);

        // Allowed while still Initiated.
        booking.AddAddOnToItem(item.Id, Guid.NewGuid(), fixture.AddOn.Id, "Sofa Cleaning", 150m, 1);
        item.AddOns.Should().ContainSingle();

        booking.TransitionTo(BookingStatus.PaymentPending);

        var act = () => booking.AddAddOnToItem(item.Id, Guid.NewGuid(), fixture.AddOn.Id, "Extra Add-on", 100m, 1);

        act.Should().Throw<InvalidOperationException>();
        item.AddOns.Should().ContainSingle("the rejected add-on must not have been appended");
    }

    [Fact]
    public void AddItem_stays_locked_across_every_status_the_booking_can_reach_from_Initiated()
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Meera Shah", CustomerStatus.Active);
        var address = new AddressSnapshot("Home", "5 Park Street", null, null, "560002", "Bengaluru", "Karnataka", 12.97m, 77.59m, "Meera Shah", "9876500001");
        var slot = new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(4)), "Evening", TimeSpan.FromHours(17), TimeSpan.FromHours(20));
        var price = new PriceSnapshot(400m, 1, 400m, 0m, 0m, 400m, 18m, 72m, 0m, 472m);

        var booking = new Booking(Guid.NewGuid(), customer.Id, new CustomerSnapshot(customer.Name, customer.Mobile), null, address, slot, price);
        booking.AddItem(Guid.NewGuid(), Guid.NewGuid(), "Deep Clean", "deep-clean", 400m, 1);

        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.TransitionTo(BookingStatus.Confirmed);
        booking.TransitionTo(BookingStatus.AwaitingFulfilment);
        booking.TransitionTo(BookingStatus.Assigned);
        booking.TransitionTo(BookingStatus.InProgress);
        booking.TransitionTo(BookingStatus.Completed);

        var act = () => booking.AddItem(Guid.NewGuid(), Guid.NewGuid(), "Sneaked-in item", "sneaked-in", 1m, 1);

        act.Should().Throw<InvalidOperationException>();
        booking.Items.Should().ContainSingle();
    }
}
