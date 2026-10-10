using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Interceptors;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Occurrence-count integrity fix: covers
/// <see cref="RecurringPlanOccurrenceReleaseHandler"/> - a recurring plan's
/// occurrence budget must be given back when a generated booking is
/// cancelled, expires unpaid, or is refunded from a pre-visit cancellation,
/// but never when a booking that actually reached
/// <see cref="BookingStatus.Completed"/> is later refunded for an unrelated
/// dispute.
/// </summary>
public sealed class RecurringPlanOccurrenceReleaseHandlerTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public RecurringPlanOccurrenceReleaseHandlerTests(TestDatabase db) => _db = db;

    private static RecurringPlanOccurrenceReleaseHandler BuildHandler(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(new BookingRepository(context), new RecurringBookingPlanRepository(context), releaseService: null!,
            new CustomerRepository(context), new ServiceRepository(context), new DeviceTokenRepository(context),
            notificationDispatchService: null!, Options.Create(new RecurringBookingOptions()),
            NullLogger<RecurringPlanOccurrenceReleaseHandler>.Instance);

    /// <summary>
    /// Minimal real geography chain to satisfy RecurringBookingPlan's real
    /// FKs (ServiceId/CityId/LocalityId/AddressId/SlotWindowId - see
    /// RecurringBookingPlanConfiguration) - this test database enforces
    /// foreign keys (PRAGMA foreign_keys = ON), so a plan cannot be built
    /// from bare Guid.NewGuid() references.
    /// </summary>
    private static async Task<(Guid PlanId, Guid BookingId)> SeedBookedOccurrenceAsync(Nestly.Infrastructure.Persistence.NestlyDbContext context, int occurrenceCount = 2)
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        var pincodeCode = Guid.NewGuid().ToString("N")[..6];
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
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 500m);
        var window = new SlotWindow(Guid.NewGuid(), city.Id, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));

        context.Add(customer);
        context.Add(address);
        context.States.Add(state);
        context.Cities.Add(city);
        context.Zones.Add(zone);
        context.Pincodes.Add(pincode);
        context.Localities.Add(locality);
        context.Add(category);
        context.Add(service);
        context.SlotWindows.Add(window);
        await context.SaveChangesAsync();

        var plan = new RecurringBookingPlan(
            Guid.NewGuid(), customer.Id, service.Id, city.Id, locality.Id, address.Id, window.Id,
            quantity: 1, RecurringBookingRecurrenceFrequency.Weekly, DayOfWeek.Monday, recurrenceDayOfMonth: null,
            startDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)), endDate: null, occurrenceCount: occurrenceCount);
        await new RecurringBookingPlanRepository(context).AddAsync(plan);
        plan.RecordOccurrenceBooked(plan.NextOccurrenceDate);
        await new RecurringBookingPlanRepository(context).UpdateAsync(plan);

        var addressSnapshot = new AddressSnapshot("Home", "221B Baker Street", null, null, pincodeCode, "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210");
        var slot = new SlotSnapshot(window.Id, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
        var price = new PriceSnapshot(500m, 1, 500m, 0m, 50m, 550m, 18m, 99m, 10m, 659m);
        var booking = new Booking(
            Guid.NewGuid(), customer.Id, new CustomerSnapshot(customer.Name, customer.Mobile), address.Id, addressSnapshot, slot, price,
            recurringBookingPlanId: plan.Id);
        await new BookingRepository(context).AddAsync(booking);

        return (plan.Id, booking.Id);
    }

    [Fact]
    public async Task Handle_releases_the_occurrence_when_the_booking_is_cancelled_before_the_visit()
    {
        Guid planId, bookingId;
        using (var context = _db.CreateContext())
        {
            (planId, bookingId) = await SeedBookedOccurrenceAsync(context, occurrenceCount: 2);
        }

        using (var context = _db.CreateContext())
        {
            var handler = BuildHandler(context);
            await handler.Handle(
                new DomainEventNotification<BookingStatusChangedEvent>(
                    new BookingStatusChangedEvent(bookingId, BookingStatus.PaymentPending, BookingStatus.CancelledByCustomer)),
                CancellationToken.None);
        }

        using (var context = _db.CreateContext())
        {
            var plan = await new RecurringBookingPlanRepository(context).GetByIdAsync(planId);
            plan!.CompletedOccurrenceCount.Should().Be(0, "the booking that consumed this occurrence's budget never happened");
        }
    }

    [Fact]
    public async Task Handle_releases_the_occurrence_when_the_booking_expires_unpaid()
    {
        Guid planId, bookingId;
        using (var context = _db.CreateContext())
        {
            (planId, bookingId) = await SeedBookedOccurrenceAsync(context, occurrenceCount: 2);
        }

        using (var context = _db.CreateContext())
        {
            var handler = BuildHandler(context);
            await handler.Handle(
                new DomainEventNotification<BookingStatusChangedEvent>(
                    new BookingStatusChangedEvent(bookingId, BookingStatus.PaymentPending, BookingStatus.Expired)),
                CancellationToken.None);
        }

        using (var context = _db.CreateContext())
        {
            var plan = await new RecurringBookingPlanRepository(context).GetByIdAsync(planId);
            plan!.CompletedOccurrenceCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task Handle_releases_the_occurrence_when_a_pre_visit_cancellation_is_later_refunded()
    {
        Guid planId, bookingId;
        using (var context = _db.CreateContext())
        {
            (planId, bookingId) = await SeedBookedOccurrenceAsync(context, occurrenceCount: 2);
        }

        // The real transition sequence a pre-visit cancel-then-refund
        // produces: PaymentPending -> CancelledByCustomer -> Refunded,
        // Completed never appears in the booking's history.
        using (var context = _db.CreateContext())
        {
            var booking = await new BookingRepository(context).GetByIdAsync(bookingId);
            booking!.TransitionTo(BookingStatus.CancelledByCustomer);
            await new BookingRepository(context).UpdateAsync(booking);
        }

        using (var context = _db.CreateContext())
        {
            var booking = await new BookingRepository(context).GetByIdAsync(bookingId);
            booking!.TransitionTo(BookingStatus.Refunded);
            await new BookingRepository(context).UpdateAsync(booking);
        }

        using (var context = _db.CreateContext())
        {
            var handler = BuildHandler(context);
            await handler.Handle(
                new DomainEventNotification<BookingStatusChangedEvent>(
                    new BookingStatusChangedEvent(bookingId, BookingStatus.CancelledByCustomer, BookingStatus.Refunded)),
                CancellationToken.None);
        }

        using (var context = _db.CreateContext())
        {
            var plan = await new RecurringBookingPlanRepository(context).GetByIdAsync(planId);
            plan!.CompletedOccurrenceCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task Handle_does_not_release_the_occurrence_when_a_completed_visit_is_later_refunded()
    {
        Guid planId, bookingId;
        using (var context = _db.CreateContext())
        {
            (planId, bookingId) = await SeedBookedOccurrenceAsync(context, occurrenceCount: 2);
        }

        // The real transition sequence a post-visit dispute refund produces:
        // Completed -> RefundPending -> Refunded. The professional did the
        // job, so this must still count against the plan's budget.
        using (var context = _db.CreateContext())
        {
            var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Cleaning", ProviderType.Individual, "+9198" + Guid.NewGuid().ToString("N")[..8]);
            context.Add(provider);
            await context.SaveChangesAsync();

            var booking = await new BookingRepository(context).GetByIdAsync(bookingId);
            booking!.TransitionTo(BookingStatus.Confirmed);
            booking.TransitionTo(BookingStatus.AwaitingFulfilment);
            booking.TransitionTo(BookingStatus.Assigned);
            booking.AssignProvider(provider.Id);
            booking.TransitionTo(BookingStatus.InProgress);
            booking.TransitionTo(BookingStatus.Completed);
            booking.TransitionTo(BookingStatus.RefundPending);
            await new BookingRepository(context).UpdateAsync(booking);
        }

        using (var context = _db.CreateContext())
        {
            var booking = await new BookingRepository(context).GetByIdAsync(bookingId);
            booking!.TransitionTo(BookingStatus.Refunded);
            await new BookingRepository(context).UpdateAsync(booking);
        }

        using (var context = _db.CreateContext())
        {
            var handler = BuildHandler(context);
            await handler.Handle(
                new DomainEventNotification<BookingStatusChangedEvent>(
                    new BookingStatusChangedEvent(bookingId, BookingStatus.RefundPending, BookingStatus.Refunded)),
                CancellationToken.None);
        }

        using (var context = _db.CreateContext())
        {
            var plan = await new RecurringBookingPlanRepository(context).GetByIdAsync(planId);
            plan!.CompletedOccurrenceCount.Should().Be(1, "the visit actually happened - refunding the money afterward must not give the occurrence back");
        }
    }

    [Fact]
    public async Task Handle_is_a_no_op_for_a_one_off_booking()
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        var address = new AddressSnapshot("Home", "221B Baker Street", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210");
        var slot = new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
        var price = new PriceSnapshot(500m, 1, 500m, 0m, 50m, 550m, 18m, 99m, 10m, 659m);
        Guid bookingId;

        using (var context = _db.CreateContext())
        {
            context.Add(customer);
            await context.SaveChangesAsync();
            var booking = new Booking(Guid.NewGuid(), customer.Id, new CustomerSnapshot(customer.Name, customer.Mobile), null, address, slot, price);
            bookingId = booking.Id;
            await new BookingRepository(context).AddAsync(booking);
        }

        using (var context = _db.CreateContext())
        {
            var handler = BuildHandler(context);
            var act = async () => await handler.Handle(
                new DomainEventNotification<BookingStatusChangedEvent>(
                    new BookingStatusChangedEvent(bookingId, BookingStatus.PaymentPending, BookingStatus.CancelledByCustomer)),
                CancellationToken.None);

            await act.Should().NotThrowAsync();
        }
    }
}
