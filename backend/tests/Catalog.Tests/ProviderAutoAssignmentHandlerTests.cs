using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.ProviderManagement;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Interceptors;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers task 246: automatic provider assignment on a booking reaching AwaitingFulfilment.</summary>
public sealed class ProviderAutoAssignmentHandlerTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public ProviderAutoAssignmentHandlerTests(TestDatabase db) => _db = db;

    private static readonly DateOnly SlotDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10));

    private sealed record Fixture(Guid CustomerId, Guid CategoryId, Guid ServiceId, Guid CityId, Guid SlotWindowId, Guid BookingId);

    private static Fixture Seed(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        var pincodeCode = Guid.NewGuid().ToString("N")[..6];
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, pincodeCode);
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 500m);
        var window = new SlotWindow(Guid.NewGuid(), city.Id, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));

        context.Add(customer);
        context.States.Add(state);
        context.Cities.Add(city);
        context.Pincodes.Add(pincode);
        context.Add(category);
        context.Add(service);
        context.SlotWindows.Add(window);

        var address = new AddressSnapshot(
            "Home", "221B Baker Street", null, null, pincodeCode, "Bengaluru", "Karnataka",
            12.9352m, 77.6245m, "Asha Rao", "9876543210");
        var slot = new SlotSnapshot(window.Id, SlotDate, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
        var price = new PriceSnapshot(500m, 1, 500m, 0m, 50m, 550m, 18m, 99m, 10m, 659m);
        var booking = new Booking(Guid.NewGuid(), customer.Id, new CustomerSnapshot(customer.Name, customer.Mobile), null, address, slot, price);
        booking.AddItem(Guid.NewGuid(), service.Id, service.Name, service.Slug, 500m, 1);
        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.TransitionTo(BookingStatus.Confirmed);
        booking.TransitionTo(BookingStatus.AwaitingFulfilment);
        context.Add(booking);
        context.SaveChanges();

        return new Fixture(customer.Id, category.Id, service.Id, city.Id, window.Id, booking.Id);
    }

    private static Provider AddActiveEligibleProvider(Nestly.Infrastructure.Persistence.NestlyDbContext context, Fixture f, decimal lat, decimal lng)
    {
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+9198" + Guid.NewGuid().ToString("N")[..8]);
        provider.ChangeStatus(ProviderStatus.Active);
        provider.UpdateLocation(lat, lng);
        context.Add(provider);
        context.Add(new ProviderSkillMapping(Guid.NewGuid(), provider.Id, f.CategoryId));
        context.Add(new ProviderServiceArea(Guid.NewGuid(), provider.Id, f.CityId));
        context.Add(new ProviderAvailabilityWindow(Guid.NewGuid(), provider.Id, SlotDate.DayOfWeek, TimeSpan.FromHours(8), TimeSpan.FromHours(18)));
        return provider;
    }

    // The real SandboxRouteEstimateProvider, not a stub: it needs no HTTP and
    // no key, and task 267 defines a sandbox-only response as carrying no road
    // information, so candidate ranking here stays straight-line - exactly the
    // behaviour these tests were written against. Task 267's reordering is
    // covered in ProviderMatchingServiceRouteRankingTests.
    private static ProviderAutoAssignmentHandler BuildHandler(Nestly.Infrastructure.Persistence.NestlyDbContext context, int retryAttempts = 3, bool enabled = true) => new(
        new EligibleProviderSearchService(
            new ProviderMatchingService(
                new BookingRepository(context),
                context,
                new SandboxRouteEstimateProvider(Options.Create(new SandboxRouteEstimateOptions())),
                Options.Create(new AutoAssignmentOptions())),
            BuildEligibilityService(context)),
        BuildEligibilityService(context),
        new BookingProviderAssignmentService(new BookingRepository(context), new ProviderRepository(context), new ServiceRepository(context), new BookingProviderAssignmentRepository(context), new ProviderScheduleConflictService(context, TestServices.Occupancy()), Options.Create(new AutoAssignmentOptions { RetryAttempts = retryAttempts, Enabled = enabled }), TestServices.ProviderNotificationPublisher(context), context),
        new BookingProviderAssignmentRepository(context),
        new BookingRepository(context),
        new RecurringPlanProviderContinuityService(new BookingRepository(context)),
        Options.Create(new AutoAssignmentOptions { RetryAttempts = retryAttempts, Enabled = enabled }),
        NullLogger<ProviderAutoAssignmentHandler>.Instance);

    private static ProviderAssignmentEligibilityService BuildEligibilityService(Nestly.Infrastructure.Persistence.NestlyDbContext context) => new(
        new BookingRepository(context),
        new ProviderAvailabilityWindowRepository(context),
        new ProviderBlackoutDateRepository(context),
        new ProviderCapacityRepository(context),
        new ProviderScheduleConflictService(context, TestServices.Occupancy()),
        TravelFeasibilityFactory.Sandbox(context),
        context);

    private static DomainEventNotification<BookingStatusChangedEvent> AwaitingFulfilmentEvent(Guid bookingId) =>
        new(new BookingStatusChangedEvent(bookingId, BookingStatus.Confirmed, BookingStatus.AwaitingFulfilment));

    [Fact]
    public async Task Handle_assigns_the_nearest_eligible_provider_when_a_booking_reaches_AwaitingFulfilment()
    {
        Fixture f;
        Provider near, far;
        using (var context = _db.CreateContext())
        {
            f = Seed(context);
            far = AddActiveEligibleProvider(context, f, 13.0827m, 80.2707m);
            near = AddActiveEligibleProvider(context, f, 12.9352m, 77.6146m);
            context.SaveChanges();
        }

        using (var context = _db.CreateContext())
        {
            await BuildHandler(context).Handle(AwaitingFulfilmentEvent(f.BookingId), CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(f.BookingId);
        booking!.Status.Should().Be(BookingStatus.Assigned);
        booking.AssignedProviderId.Should().Be(near.Id, "the nearer of the two eligible candidates must win");

        var assignment = await new BookingProviderAssignmentRepository(readContext).GetActiveByBookingAsync(f.BookingId);
        assignment.Should().NotBeNull();
        assignment!.AssignedByType.Should().Be(BookingAssignedByType.System);
        assignment.AssignedByUserId.Should().BeNull();
    }

    [Fact]
    public async Task Handle_ignores_transitions_other_than_AwaitingFulfilment()
    {
        Fixture f;
        using (var context = _db.CreateContext())
        {
            f = Seed(context);
            AddActiveEligibleProvider(context, f, 12.9352m, 77.6146m);
            context.SaveChanges();
        }

        using (var context = _db.CreateContext())
        {
            var evt = new DomainEventNotification<BookingStatusChangedEvent>(
                new BookingStatusChangedEvent(f.BookingId, BookingStatus.PaymentPending, BookingStatus.Confirmed));
            await BuildHandler(context).Handle(evt, CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(f.BookingId);
        booking!.Status.Should().Be(BookingStatus.AwaitingFulfilment, "the fixture booking starts here and this event's ToStatus isn't AwaitingFulfilment, so the handler must not touch it");
        booking.AssignedProviderId.Should().BeNull();
    }

    [Fact]
    public async Task Handle_leaves_the_booking_in_AwaitingFulfilment_when_no_candidate_is_eligible()
    {
        Fixture f;
        using (var context = _db.CreateContext())
        {
            f = Seed(context);
            // No provider seeded at all.
        }

        using (var context = _db.CreateContext())
        {
            await BuildHandler(context).Handle(AwaitingFulfilmentEvent(f.BookingId), CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(f.BookingId);
        booking!.Status.Should().Be(BookingStatus.AwaitingFulfilment, "no eligible provider is not an error - the booking stays exactly where today's manual admin queue already picks it up");
        booking.AssignedProviderId.Should().BeNull();
    }

    [Fact]
    public async Task TryAssignAsync_skips_excluded_providers()
    {
        Fixture f;
        Provider excluded, other;
        using (var context = _db.CreateContext())
        {
            f = Seed(context);
            excluded = AddActiveEligibleProvider(context, f, 12.9352m, 77.6146m);
            other = AddActiveEligibleProvider(context, f, 13.0827m, 80.2707m);
            context.SaveChanges();
        }

        using (var context = _db.CreateContext())
        {
            var assigned = await BuildHandler(context).TryAssignAsync(f.BookingId, [excluded.Id]);
            assigned.Should().BeTrue();
        }

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(f.BookingId);
        booking!.AssignedProviderId.Should().Be(other.Id);
    }

    /// <summary>Task 247's actual end-to-end path: RejectAsync's own TransitionTo re-raises AwaitingFulfilment (no new coupling needed between BookingProviderAssignmentService and this handler), and this handler's Handle() must read that history back and exclude the rejecter.</summary>
    [Fact]
    public async Task Handle_excludes_a_provider_who_already_rejected_this_booking_and_assigns_the_next_one()
    {
        Fixture f;
        Provider rejecter, other;
        using (var context = _db.CreateContext())
        {
            f = Seed(context);
            rejecter = AddActiveEligibleProvider(context, f, 12.9352m, 77.6146m);
            other = AddActiveEligibleProvider(context, f, 13.0827m, 80.2707m);
            context.SaveChanges();
        }

        using (var context = _db.CreateContext())
        {
            var assignmentService = new BookingProviderAssignmentService(
                new BookingRepository(context), new ProviderRepository(context), new ServiceRepository(context), new BookingProviderAssignmentRepository(context), new ProviderScheduleConflictService(context, TestServices.Occupancy()), Options.Create(new AutoAssignmentOptions()), TestServices.ProviderNotificationPublisher(context), context);

            var firstAssign = await assignmentService.AssignBySystemAsync(f.BookingId, rejecter.Id);
            firstAssign.IsSuccess.Should().BeTrue();

            var rejectResult = await assignmentService.RejectByProviderAsync(f.BookingId, rejecter.Id, new RejectAssignmentRequest("Too far"));
            rejectResult.IsSuccess.Should().BeTrue();
        }

        using (var context = _db.CreateContext())
        {
            await BuildHandler(context).Handle(AwaitingFulfilmentEvent(f.BookingId), CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(f.BookingId);
        booking!.AssignedProviderId.Should().Be(other.Id, "the provider who already rejected must be excluded from the retry");

        var history = await new BookingProviderAssignmentRepository(readContext).ListByBookingAsync(f.BookingId);
        history.Should().Contain(a => a.ProviderId == rejecter.Id && a.Status == BookingProviderAssignmentStatus.Rejected);
    }

    /// <summary>
    /// The assignment-response-expiry sweep's action (<see cref="IBookingProviderAssignmentService.ExpireAsync"/>)
    /// re-raises AwaitingFulfilment exactly like <see cref="RejectByProviderAsync"/>'s
    /// path does - same reassignment pool, triggered by silence instead of an
    /// explicit decline. Mirrors <see cref="Handle_excludes_a_provider_who_already_rejected_this_booking_and_assigns_the_next_one"/>.
    /// </summary>
    [Fact]
    public async Task Handle_excludes_a_provider_whose_assignment_expired_and_assigns_the_next_one()
    {
        Fixture f;
        Provider unresponsive, other;
        using (var context = _db.CreateContext())
        {
            f = Seed(context);
            unresponsive = AddActiveEligibleProvider(context, f, 12.9352m, 77.6146m);
            other = AddActiveEligibleProvider(context, f, 13.0827m, 80.2707m);
            context.SaveChanges();
        }

        Guid assignmentId;
        using (var context = _db.CreateContext())
        {
            var assignmentService = new BookingProviderAssignmentService(
                new BookingRepository(context), new ProviderRepository(context), new ServiceRepository(context), new BookingProviderAssignmentRepository(context), new ProviderScheduleConflictService(context, TestServices.Occupancy()), Options.Create(new AutoAssignmentOptions()), TestServices.ProviderNotificationPublisher(context), context);

            var firstAssign = await assignmentService.AssignBySystemAsync(f.BookingId, unresponsive.Id);
            firstAssign.IsSuccess.Should().BeTrue();
            assignmentId = firstAssign.Value.Id;

            // Simulates the sweep finding this assignment past its deadline -
            // ExpireAsync is exactly what AssignmentResponseExpirySweepJob calls
            // per stale row it finds, so this exercises the same path without
            // needing to wait out a real ResponseWindowMinutes.
            var expireResult = await assignmentService.ExpireAsync(assignmentId);
            expireResult.IsSuccess.Should().BeTrue();
            expireResult.Value.Status.Should().Be(BookingProviderAssignmentStatus.Expired);
        }

        using (var context = _db.CreateContext())
        {
            await BuildHandler(context).Handle(AwaitingFulfilmentEvent(f.BookingId), CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(f.BookingId);
        booking!.AssignedProviderId.Should().Be(other.Id, "the provider whose assignment expired must be excluded from the retry");

        var history = await new BookingProviderAssignmentRepository(readContext).ListByBookingAsync(f.BookingId);
        history.Should().Contain(a => a.ProviderId == unresponsive.Id && a.Status == BookingProviderAssignmentStatus.Expired);
    }

    [Fact]
    public async Task Handle_stops_retrying_once_the_configured_cap_is_reached_even_with_an_eligible_candidate_left()
    {
        Fixture f;
        Provider stillEligible;
        using (var context = _db.CreateContext())
        {
            f = Seed(context);
            // Two providers already auto-assigned-then-rejected (retry cap = 2 below), plus one untouched, still-eligible provider.
            var rejectedOne = AddActiveEligibleProvider(context, f, 12.9352m, 77.6146m);
            var rejectedTwo = AddActiveEligibleProvider(context, f, 12.94m, 77.62m);
            stillEligible = AddActiveEligibleProvider(context, f, 13.0827m, 80.2707m);
            context.Add(new BookingProviderAssignment(Guid.NewGuid(), f.BookingId, rejectedOne.Id, BookingAssignedByType.System, null, null));
            context.Add(new BookingProviderAssignment(Guid.NewGuid(), f.BookingId, rejectedTwo.Id, BookingAssignedByType.System, null, null));
            var rows = context.Set<BookingProviderAssignment>().Local.ToList();
            foreach (var row in rows)
            {
                row.Reject("Too far");
            }
            context.SaveChanges();
        }

        using (var context = _db.CreateContext())
        {
            await BuildHandler(context, retryAttempts: 2).Handle(AwaitingFulfilmentEvent(f.BookingId), CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(f.BookingId);
        booking!.Status.Should().Be(BookingStatus.AwaitingFulfilment, "the retry cap was already reached, so even a fresh eligible candidate must not be auto-assigned");
        booking.AssignedProviderId.Should().BeNull();
        _ = stillEligible; // never assigned - proven by the assertions above.
    }

    /// <summary>
    /// Row 81, docs/OPEN-FIXES-FEATURES.csv: an admin manually assigning a
    /// provider to a Confirmed booking walks Confirmed -> AwaitingFulfilment
    /// -> Assigned and saves once
    /// (BookingProviderAssignmentService.AssignInternalAsync), which means
    /// this handler can receive an AwaitingFulfilment notification for a
    /// booking whose real, persisted status has already moved on to Assigned
    /// by the time it runs (DomainEventDispatchInterceptor publishes as soon
    /// as that single SaveChangesAsync completes, not once the caller's own
    /// still-open explicit transaction commits). Handling it anyway used to
    /// call AssignBySystemAsync, which tries to open a second Serializable
    /// transaction on that same connection - forbidden, and what actually
    /// produced row 81's generic 500 (reproduced locally and confirmed via
    /// this exact stack trace before this guard was added). A real eligible
    /// candidate is seeded so there would be something to (wrongly) assign
    /// if the guard were missing or ever regresses.
    /// </summary>
    [Fact]
    public async Task Handle_does_nothing_when_the_booking_has_already_moved_past_AwaitingFulfilment()
    {
        Fixture f;
        Provider eligibleProvider, alreadyAssignedProvider;
        using (var context = _db.CreateContext())
        {
            f = Seed(context);
            eligibleProvider = AddActiveEligibleProvider(context, f, 12.9352m, 77.6146m);
            alreadyAssignedProvider = AddActiveEligibleProvider(context, f, 13.0827m, 80.2707m);
            context.SaveChanges();
        }

        using (var context = _db.CreateContext())
        {
            var booking = await new BookingRepository(context).GetByIdAsync(f.BookingId);
            booking!.TransitionTo(BookingStatus.Assigned, "Provider assigned by admin.");
            booking.AssignProvider(alreadyAssignedProvider.Id);
            await new BookingRepository(context).UpdateAsync(booking);
        }

        using (var context = _db.CreateContext())
        {
            await BuildHandler(context).Handle(AwaitingFulfilmentEvent(f.BookingId), CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        var finalBooking = await new BookingRepository(readContext).GetByIdAsync(f.BookingId);
        finalBooking!.Status.Should().Be(BookingStatus.Assigned);
        finalBooking.AssignedProviderId.Should().Be(
            alreadyAssignedProvider.Id, "the handler must not touch a booking that already moved past AwaitingFulfilment");

        var history = await new BookingProviderAssignmentRepository(readContext).ListByBookingAsync(f.BookingId);
        history.Should().BeEmpty("no system assignment attempt should have been made at all, not even one that would have lost a race");
        _ = eligibleProvider; // present only to prove a real candidate existed and was still correctly ignored
    }

    private static DomainEventNotification<BookingStatusChangedEvent> RescheduledEvent(Guid bookingId) =>
        new(new BookingStatusChangedEvent(bookingId, BookingStatus.Rescheduled, BookingStatus.AwaitingFulfilment));

    /// <summary>
    /// Puts the booking in the state a reschedule leaves it in: assigned to <paramref name="onTheJob"/>, then moved
    /// (<c>Booking.Reschedule</c> walks Assigned -> Rescheduled -> AwaitingFulfilment and leaves the professional and
    /// their live assignment row alone). Returns the id of that original assignment row.
    /// </summary>
    private async Task<Guid> RescheduleAssignedBookingAsync(Fixture f, Guid onTheJob)
    {
        using var context = _db.CreateContext();
        var assignment = new BookingProviderAssignment(Guid.NewGuid(), f.BookingId, onTheJob, BookingAssignedByType.System, null, null);
        context.Add(assignment);

        var booking = await new BookingRepository(context).GetByIdAsync(f.BookingId);
        booking!.TransitionTo(BookingStatus.Assigned, "Provider assigned by admin.");
        booking.AssignProvider(onTheJob);
        booking.Reschedule(f.SlotWindowId, SlotDate, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13), "Customer asked", 0m);
        await new BookingRepository(context).UpdateAsync(booking);
        return assignment.Id;
    }

    /// <summary>
    /// A reschedule re-runs matching, and without a preference it would hand the job to the nearest eligible professional:
    /// a customer who had been told who is coming would find somebody else, and the professional replaced would never be
    /// told. The one already on the job gets first call - and keeps it when the new time works for them.
    /// </summary>
    [Fact]
    public async Task Handle_keeps_the_professional_already_on_a_rescheduled_booking_even_when_another_is_nearer()
    {
        Fixture f;
        Provider onTheJob, nearer;
        using (var context = _db.CreateContext())
        {
            f = Seed(context);
            onTheJob = AddActiveEligibleProvider(context, f, 13.0827m, 80.2707m);
            nearer = AddActiveEligibleProvider(context, f, 12.9352m, 77.6146m);
            context.SaveChanges();
        }

        var originalAssignmentId = await RescheduleAssignedBookingAsync(f, onTheJob.Id);

        using (var context = _db.CreateContext())
        {
            await BuildHandler(context).Handle(RescheduledEvent(f.BookingId), CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(f.BookingId);
        booking!.Status.Should().Be(BookingStatus.Assigned);
        booking.AssignedProviderId.Should().Be(onTheJob.Id, "the professional already on the job is still eligible, so the nearer one must not take it from them");

        var history = await new BookingProviderAssignmentRepository(readContext).ListByBookingAsync(f.BookingId);
        history.Should().OnlyContain(a => a.ProviderId == onTheJob.Id, "nobody else was ever offered the job");
        var live = await new BookingProviderAssignmentRepository(readContext).GetActiveByBookingAsync(f.BookingId);
        live!.Id.Should().NotBe(originalAssignmentId, "they are re-offered the job for the new time, which is how every assignment of a booking works");
        _ = nearer;
    }

    [Fact]
    public async Task Handle_replaces_the_professional_on_a_rescheduled_booking_when_the_new_time_does_not_suit_them()
    {
        Fixture f;
        Provider onTheJob, other;
        using (var context = _db.CreateContext())
        {
            f = Seed(context);
            onTheJob = AddActiveEligibleProvider(context, f, 12.9352m, 77.6146m);
            other = AddActiveEligibleProvider(context, f, 13.0827m, 80.2707m);
            // On leave on the new date: not eligible for it, however close they are.
            context.Add(new ProviderBlackoutDate(Guid.NewGuid(), onTheJob.Id, SlotDate, SlotDate, "On leave"));
            context.SaveChanges();
        }

        await RescheduleAssignedBookingAsync(f, onTheJob.Id);

        using (var context = _db.CreateContext())
        {
            await BuildHandler(context).Handle(RescheduledEvent(f.BookingId), CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(f.BookingId);
        booking!.Status.Should().Be(BookingStatus.Assigned);
        booking.AssignedProviderId.Should().Be(other.Id, "the professional on the job cannot take the new time, so the ranked walk gives it to the next eligible one");

        var history = await new BookingProviderAssignmentRepository(readContext).ListByBookingAsync(f.BookingId);
        history.Single(a => a.ProviderId == onTheJob.Id).Status.Should().Be(BookingProviderAssignmentStatus.Reassigned);
    }

    /// <summary>A hop back to AwaitingFulfilment that is not a reschedule has no professional to prefer: a stale display id must not be mistaken for one.</summary>
    [Fact]
    public async Task Handle_prefers_nobody_when_the_booking_did_not_come_back_from_a_reschedule()
    {
        Fixture f;
        Provider stale, nearest;
        using (var context = _db.CreateContext())
        {
            f = Seed(context);
            stale = AddActiveEligibleProvider(context, f, 13.0827m, 80.2707m);
            nearest = AddActiveEligibleProvider(context, f, 12.9352m, 77.6146m);
            context.SaveChanges();
        }

        using (var context = _db.CreateContext())
        {
            var booking = await new BookingRepository(context).GetByIdAsync(f.BookingId);
            booking!.AssignProvider(stale.Id);
            await new BookingRepository(context).UpdateAsync(booking);
        }

        using (var context = _db.CreateContext())
        {
            await BuildHandler(context).Handle(AwaitingFulfilmentEvent(f.BookingId), CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        var after = await new BookingRepository(readContext).GetByIdAsync(f.BookingId);
        after!.AssignedProviderId.Should().Be(nearest.Id, "outside a reschedule the ranking alone decides");
    }

    /// <summary>Task 248: the kill switch must produce zero behaviour change from before this whole phase existed - not a new error path, just untouched.</summary>
    [Fact]
    public async Task Handle_does_nothing_when_AutoAssignmentOptions_Enabled_is_false()
    {
        Fixture f;
        using (var context = _db.CreateContext())
        {
            f = Seed(context);
            AddActiveEligibleProvider(context, f, 12.9352m, 77.6146m);
            context.SaveChanges();
        }

        using (var context = _db.CreateContext())
        {
            await BuildHandler(context, enabled: false).Handle(AwaitingFulfilmentEvent(f.BookingId), CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(f.BookingId);
        booking!.Status.Should().Be(BookingStatus.AwaitingFulfilment);
        booking.AssignedProviderId.Should().BeNull();
    }
}
