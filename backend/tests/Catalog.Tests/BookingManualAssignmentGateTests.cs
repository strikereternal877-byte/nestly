using FluentAssertions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.ProviderManagement;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Row 33, docs/OPEN-FIXES-FEATURES.csv: a paid Confirmed booking could not
/// be manually assigned to a provider by admin - the panel said "A provider
/// can only be assigned once this booking reaches Preparing Service" and
/// auto-assignment only runs as the slot approaches. Fix: relax the manual
/// (admin) assignment gate to also allow Confirmed, while leaving the
/// automatic engine's own gate untouched.
/// </summary>
public sealed class BookingManualAssignmentGateTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public BookingManualAssignmentGateTests(TestDatabase db) => _db = db;

    private static readonly DateOnly SlotDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10));
    private static readonly Guid AdminUserId = Guid.NewGuid();

    private sealed record Fixture(Guid CustomerId, Guid CategoryId, Guid ServiceId, Guid CityId, string PincodeCode);

    private static Fixture Seed(NestlyDbContext context)
    {
        string pincodeCode = Guid.NewGuid().ToString("N")[..6];
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, pincodeCode);
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 500m);

        context.Add(customer);
        context.States.Add(state);
        context.Cities.Add(city);
        context.Pincodes.Add(pincode);
        context.Add(category);
        context.Add(service);
        context.SaveChanges();

        return new Fixture(customer.Id, category.Id, service.Id, city.Id, pincodeCode);
    }

    /// <summary>Stops at <paramref name="status"/> - either Confirmed (the row's scenario) or AwaitingFulfilment (the pre-existing behaviour, for contrast).</summary>
    private static Booking AddBooking(NestlyDbContext context, Fixture f, BookingStatus status)
    {
        var windowId = Guid.NewGuid();
        context.SlotWindows.Add(new SlotWindow(windowId, f.CityId, "Window", TimeSpan.FromHours(9), TimeSpan.FromHours(11)));

        var address = new AddressSnapshot(
            "Home", "221B Baker Street", null, null, f.PincodeCode, "Bengaluru", "Karnataka",
            12.9352m, 77.6245m, "Asha Rao", "9876543210");
        var slot = new SlotSnapshot(windowId, SlotDate, "Window", TimeSpan.FromHours(9), TimeSpan.FromHours(11));
        var price = new PriceSnapshot(500m, 1, 500m, 0m, 50m, 550m, 18m, 99m, 10m, 659m);

        var booking = new Booking(Guid.NewGuid(), f.CustomerId, new CustomerSnapshot("Asha Rao", "9876543210"), null, address, slot, price);
        booking.AddItem(Guid.NewGuid(), f.ServiceId, "Deep Clean", "deep-clean", 500m, 1);
        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.TransitionTo(BookingStatus.Confirmed);
        if (status == BookingStatus.AwaitingFulfilment)
        {
            booking.TransitionTo(BookingStatus.AwaitingFulfilment);
        }

        context.Add(booking);
        context.SaveChanges();
        return booking;
    }

    private static Provider AddActiveProvider(NestlyDbContext context, Fixture f)
    {
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+9198" + Guid.NewGuid().ToString("N")[..8]);
        provider.ChangeStatus(ProviderStatus.Active);
        context.Add(provider);
        context.Add(new ProviderSkillMapping(Guid.NewGuid(), provider.Id, f.CategoryId));
        context.Add(new ProviderServiceArea(Guid.NewGuid(), provider.Id, f.CityId));
        context.SaveChanges();
        return provider;
    }

    private static BookingProviderAssignmentService BuildAssignmentService(NestlyDbContext context) => new(
        new BookingRepository(context),
        new ProviderRepository(context),
        new ServiceRepository(context),
        new BookingProviderAssignmentRepository(context),
        new ProviderScheduleConflictService(context, TestServices.Occupancy()),
        Options.Create(new AutoAssignmentOptions()),
        context);

    [Fact]
    public async Task Admin_can_manually_assign_a_provider_to_a_Confirmed_booking()
    {
        Guid bookingId, providerId;
        using (var context = _db.CreateContext())
        {
            var f = Seed(context);
            bookingId = AddBooking(context, f, BookingStatus.Confirmed).Id;
            providerId = AddActiveProvider(context, f).Id;
        }

        using var actContext = _db.CreateContext();
        var result = await BuildAssignmentService(actContext).AssignAsync(
            bookingId, AdminUserId, new AssignProviderRequest(providerId, ResponseDeadline: null));

        result.IsSuccess.Should().BeTrue();

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(bookingId);
        booking!.Status.Should().Be(BookingStatus.Assigned, "manual assignment from Confirmed must walk the booking through to Assigned");
        booking.AssignedProviderId.Should().Be(providerId);
    }

    [Fact]
    public async Task Admin_can_still_manually_assign_a_provider_to_an_AwaitingFulfilment_booking()
    {
        Guid bookingId, providerId;
        using (var context = _db.CreateContext())
        {
            var f = Seed(context);
            bookingId = AddBooking(context, f, BookingStatus.AwaitingFulfilment).Id;
            providerId = AddActiveProvider(context, f).Id;
        }

        using var actContext = _db.CreateContext();
        var result = await BuildAssignmentService(actContext).AssignAsync(
            bookingId, AdminUserId, new AssignProviderRequest(providerId, ResponseDeadline: null));

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Automatic_assignment_still_refuses_a_Confirmed_booking()
    {
        Guid bookingId, providerId;
        using (var context = _db.CreateContext())
        {
            var f = Seed(context);
            bookingId = AddBooking(context, f, BookingStatus.Confirmed).Id;
            providerId = AddActiveProvider(context, f).Id;
        }

        using var actContext = _db.CreateContext();
        var result = await BuildAssignmentService(actContext).AssignBySystemAsync(bookingId, providerId);

        result.IsFailure.Should().BeTrue("the automatic engine's own gate must be unchanged - only the manual/admin path was relaxed");
        result.Error.Code.Should().Be("BookingProviderAssignment.InvalidBookingStatus");
    }
}
