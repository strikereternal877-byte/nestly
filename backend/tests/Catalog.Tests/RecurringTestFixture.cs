using Nestly.Application;
using Nestly.Domain;

namespace Nestly.Catalog.Tests;

/// <summary>A customer with a serviceable address and three windows: two bookable every day, one with no days at all.</summary>
internal sealed record RecurringFixture(
    Customer Customer, CustomerAddress Address, City City, Locality Locality, Service Service,
    SlotWindow Morning, SlotWindow Afternoon, SlotWindow Closed);

/// <summary>The rows the recurring-plan suites need before they can create a plan or a visit, seeded once per call.</summary>
internal static class RecurringFixtures
{
    /// <summary>
    /// A booking in <paramref name="window"/> on <paramref name="date"/>. With a plan it is one of that plan's visits,
    /// without one an unrelated order. <paramref name="assignedTo"/> sets the denormalised assigned provider the way the
    /// assignment service would.
    /// </summary>
    public static Booking AddBooking(
        TestDatabase db, RecurringFixture fixture, DateOnly date, SlotWindow window, BookingStatus status,
        RecurringBookingPlan? plan = null, Provider? assignedTo = null)
    {
        using var context = db.CreateContext();
        var booking = new Booking(
            Guid.NewGuid(), fixture.Customer.Id,
            new CustomerSnapshot(fixture.Customer.Name, fixture.Customer.Mobile),
            fixture.Address.Id,
            new AddressSnapshot("Home", "12 MG Road", null, null, fixture.Address.Pincode, "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Priya Nair", "9876543210"),
            new SlotSnapshot(window.Id, date, window.Name, window.StartTime, window.EndTime),
            new PriceSnapshot(500m, 1, 500m, 0m, 50m, 550m, 18m, 99m, 10m, 620m),
            recurringBookingPlanId: plan?.Id);
        booking.AddItem(Guid.NewGuid(), fixture.Service.Id, fixture.Service.Name, fixture.Service.Slug, 500m, 1);
        booking.TransitionTo(BookingStatus.PaymentPending);
        switch (status)
        {
            case BookingStatus.PaymentPending:
                break;
            case BookingStatus.Confirmed:
                booking.TransitionTo(BookingStatus.Confirmed);
                break;
            case BookingStatus.Expired:
                booking.TransitionTo(BookingStatus.Expired);
                break;
            case BookingStatus.CancelledByCustomer:
                booking.TransitionTo(BookingStatus.Confirmed);
                booking.TransitionTo(BookingStatus.CancelledByCustomer);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (assignedTo is not null)
        {
            booking.AssignProvider(assignedTo.Id);
        }

        context.Bookings.Add(booking);
        context.SaveChanges();
        return booking;
    }

    public static RecurringFixture Seed(TestDatabase db)
    {
        using var context = db.CreateContext();
        var pincodeCode = Guid.NewGuid().ToString("N")[..6];
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Priya Nair", CustomerStatus.Active);
        var address = new CustomerAddress(
            Guid.NewGuid(), customer.Id, "Home", "12 MG Road", null, null,
            pincodeCode, "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Priya Nair", "9876543210", true);
        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        var zone = new Zone(Guid.NewGuid(), city.Id, "Central");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, pincodeCode);
        var locality = new Locality(Guid.NewGuid(), zone.Id, pincode.Id, "Koramangala");
        address.LinkToGeography(pincode.Id, locality.Id);
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 500m);
        var morning = new SlotWindow(Guid.NewGuid(), city.Id, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
        var afternoon = new SlotWindow(Guid.NewGuid(), city.Id, "Afternoon", TimeSpan.FromHours(14), TimeSpan.FromHours(18));
        var closed = new SlotWindow(Guid.NewGuid(), city.Id, "Never", TimeSpan.FromHours(19), TimeSpan.FromHours(21));

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
        context.SlotWindows.AddRange(morning, afternoon, closed);
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            context.SlotWindowRules.Add(new SlotWindowRule(Guid.NewGuid(), morning.Id, day));
            context.SlotWindowRules.Add(new SlotWindowRule(Guid.NewGuid(), afternoon.Id, day));
        }

        context.SaveChanges();
        return new RecurringFixture(customer, address, city, locality, service, morning, afternoon, closed);
    }
}
