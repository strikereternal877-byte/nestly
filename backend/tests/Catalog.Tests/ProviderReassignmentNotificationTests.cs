using FluentAssertions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.ProviderManagement;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Task 295 (b): replacing the professional on a booking must not be silent.
///
/// <para>
/// The hole this closes: <c>BookingProviderAssignmentService.AssignInternalAsync</c>
/// guards its transition with <c>if (booking.Status == AwaitingFulfilment)</c>,
/// so reassigning an already-Assigned booking moved no status and raised no
/// <see cref="BookingStatusChangedEvent"/> at all. Every booking-lifecycle
/// handler in the system subscribes to that one stream, so the swap was
/// invisible to all of them and the customer - who had been told who was
/// coming - would have met a stranger at the door.
/// </para>
///
/// <para>
/// The signal now comes from <see cref="BookingProviderAssignment.MarkReassigned"/>,
/// which is reached on both branches of that <c>if</c> and therefore does not
/// depend on a status change happening. What the notification path then does
/// with it is covered by <c>NotificationTriggerWiringTests</c>; this file pins
/// that the event is raised at all, and that its payload says enough for that
/// decision to be made.
/// </para>
/// </summary>
public sealed class ProviderReassignmentNotificationTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public ProviderReassignmentNotificationTests(TestDatabase db) => _db = db;

    private static readonly DateOnly SlotDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(9));
    private static readonly Guid AdminUserId = Guid.NewGuid();

    private static BookingProviderAssignment NewAssignment(Guid bookingId, Guid providerId) =>
        new(Guid.NewGuid(), bookingId, providerId, BookingAssignedByType.System, null, null);

    // --- The domain event itself ---

    [Fact]
    public void Superseding_an_accepted_assignment_raises_BookingProviderChanged_marked_accepted()
    {
        var bookingId = Guid.NewGuid();
        var outgoing = Guid.NewGuid();
        var incoming = Guid.NewGuid();
        var assignment = NewAssignment(bookingId, outgoing);
        assignment.Accept();
        assignment.ClearDomainEvents();

        assignment.MarkReassigned(incoming);

        var raised = assignment.DomainEvents.OfType<BookingProviderChangedEvent>().Should().ContainSingle().Subject;
        raised.BookingId.Should().Be(bookingId);
        raised.PreviousAssignmentId.Should().Be(assignment.Id);
        raised.PreviousProviderId.Should().Be(outgoing);
        raised.NewProviderId.Should().Be(incoming);
        raised.PreviousAssignmentAccepted.Should().BeTrue("the customer had been told this provider was coming");
    }

    /// <summary>
    /// The event is still raised for a never-accepted offer - the assigned
    /// provider genuinely did change - but flagged so the notification path
    /// can stay quiet about a name that was never announced. The flag has to
    /// travel on the event because <see cref="BookingProviderAssignment.Status"/>
    /// is overwritten by the same call that raises it.
    /// </summary>
    [Fact]
    public void Superseding_an_unanswered_offer_raises_the_event_marked_not_accepted()
    {
        var assignment = NewAssignment(Guid.NewGuid(), Guid.NewGuid());

        assignment.MarkReassigned(Guid.NewGuid());

        var raised = assignment.DomainEvents.OfType<BookingProviderChangedEvent>().Should().ContainSingle().Subject;
        raised.PreviousAssignmentAccepted.Should().BeFalse();
    }

    /// <summary>Re-offering the same booking to the same provider changes nothing a customer could notice.</summary>
    [Fact]
    public void Superseding_an_assignment_with_the_same_provider_raises_nothing()
    {
        var providerId = Guid.NewGuid();
        var assignment = NewAssignment(Guid.NewGuid(), providerId);
        assignment.Accept();
        assignment.ClearDomainEvents();

        assignment.MarkReassigned(providerId);

        assignment.DomainEvents.Should().BeEmpty();
    }

    /// <summary>Ids only, same rule the tracking family states - a payload is one broadcast away from a browser.</summary>
    [Fact]
    public void The_change_event_payload_carries_no_free_text()
    {
        typeof(BookingProviderChangedEvent)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Should().BeEmpty();
    }

    // --- Through the real service, on the path that used to be silent ---

    private static BookingProviderAssignmentService BuildAssignmentService(NestlyDbContext context) => new(
        new BookingRepository(context),
        new ProviderRepository(context),
        new ServiceRepository(context),
        new BookingProviderAssignmentRepository(context),
        new ProviderScheduleConflictService(context, TestServices.Occupancy()),
        Options.Create(new AutoAssignmentOptions()),
        TestServices.ProviderNotificationPublisher(context),
        context);

    private sealed record Fixture(Guid BookingId, Guid FirstProviderId, Guid SecondProviderId);

    private async Task<Fixture> SeedAwaitingFulfilmentBookingAndTwoProvidersAsync()
    {
        using var context = _db.CreateContext();

        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 999m);

        var booking = new Booking(
            Guid.NewGuid(), customer.Id, new CustomerSnapshot(customer.Name, customer.Mobile), null,
            new AddressSnapshot("Home", "221B", null, null, "560001", "Bengaluru", "Karnataka", 12.9m, 77.5m, "Asha Rao", "9876543210"),
            new SlotSnapshot(Guid.NewGuid(), SlotDate, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
            new PriceSnapshot(999m, 1, 999m, 0, 0, 999m, 0, 0, 0, 999m));
        booking.AddItem(Guid.NewGuid(), service.Id, service.Name, service.Slug, 999m, 1);
        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.TransitionTo(BookingStatus.Confirmed);
        booking.TransitionTo(BookingStatus.AwaitingFulfilment);

        var first = NewActiveProvider("Rajesh Nair");
        var second = NewActiveProvider("Meera Iyer");

        context.AddRange(customer, category, service, booking, first, second);
        await context.SaveChangesAsync();

        return new Fixture(booking.Id, first.Id, second.Id);
    }

    /// <summary>Provider.Phone is uniquely indexed and the fixture's database is shared by every test in this class.</summary>
    private static Provider NewActiveProvider(string displayName)
    {
        var provider = new Provider(
            Guid.NewGuid(), displayName, displayName, ProviderType.Individual, "+9198" + Guid.NewGuid().ToString("N")[..8]);
        provider.ChangeStatus(ProviderStatus.Active);
        return provider;
    }

    /// <summary>
    /// Reads the events sitting on the aggregates this unit of work touched.
    /// <see cref="TestDatabase"/> attaches no <c>DomainEventDispatchInterceptor</c>,
    /// so nothing clears them - which is what makes them readable here.
    /// <c>BookingTrackingEventsTests</c> covers the other half (that an
    /// assignment's events really are published on save, the aggregate-root
    /// promotion being load-bearing for exactly that).
    /// </summary>
    private static IReadOnlyList<BookingProviderChangedEvent> ChangeEvents(NestlyDbContext context) =>
        context.ChangeTracker.Entries<BookingProviderAssignment>()
            .SelectMany(entry => entry.Entity.DomainEvents)
            .OfType<BookingProviderChangedEvent>()
            .ToList();

    [Fact]
    public async Task Reassigning_an_already_Assigned_booking_raises_the_change_event()
    {
        var fixture = await SeedAwaitingFulfilmentBookingAndTwoProvidersAsync();

        using var context = _db.CreateContext();
        var service = BuildAssignmentService(context);

        var offered = await service.AssignAsync(fixture.BookingId, AdminUserId, new AssignProviderRequest(fixture.FirstProviderId, null));
        offered.IsSuccess.Should().BeTrue();
        var accepted = await service.AcceptAsync(fixture.BookingId, fixture.FirstProviderId);
        accepted.IsSuccess.Should().BeTrue();

        var reassigned = await service.AssignAsync(fixture.BookingId, AdminUserId, new AssignProviderRequest(fixture.SecondProviderId, null));

        reassigned.IsSuccess.Should().BeTrue();
        var raised = ChangeEvents(context).Should().ContainSingle().Subject;
        raised.BookingId.Should().Be(fixture.BookingId);
        raised.PreviousProviderId.Should().Be(fixture.FirstProviderId);
        raised.NewProviderId.Should().Be(fixture.SecondProviderId);
        raised.PreviousAssignmentAccepted.Should().BeTrue();

        // The point of the row: this whole flow moved no booking status, which
        // is why BookingStatusChangedEvent could never have carried it.
        var booking = await new BookingRepository(_db.CreateContext()).GetByIdAsync(fixture.BookingId);
        booking!.Status.Should().Be(BookingStatus.Assigned);
        booking.AssignedProviderId.Should().Be(fixture.SecondProviderId);
    }

    [Fact]
    public async Task Reassigning_an_offer_nobody_answered_raises_the_event_marked_not_accepted()
    {
        var fixture = await SeedAwaitingFulfilmentBookingAndTwoProvidersAsync();

        using var context = _db.CreateContext();
        var service = BuildAssignmentService(context);

        await service.AssignAsync(fixture.BookingId, AdminUserId, new AssignProviderRequest(fixture.FirstProviderId, null));
        var reassigned = await service.AssignAsync(fixture.BookingId, AdminUserId, new AssignProviderRequest(fixture.SecondProviderId, null));

        reassigned.IsSuccess.Should().BeTrue();
        ChangeEvents(context).Should().ContainSingle()
            .Which.PreviousAssignmentAccepted.Should().BeFalse();
    }

    /// <summary>
    /// The first assignment of a booking has nothing to supersede, so it
    /// raises no change event - the customer learns who is coming from the
    /// acceptance instead.
    /// </summary>
    [Fact]
    public async Task A_first_assignment_raises_no_change_event()
    {
        var fixture = await SeedAwaitingFulfilmentBookingAndTwoProvidersAsync();

        using var context = _db.CreateContext();
        var service = BuildAssignmentService(context);

        var offered = await service.AssignAsync(fixture.BookingId, AdminUserId, new AssignProviderRequest(fixture.FirstProviderId, null));

        offered.IsSuccess.Should().BeTrue();
        ChangeEvents(context).Should().BeEmpty();
    }
}
