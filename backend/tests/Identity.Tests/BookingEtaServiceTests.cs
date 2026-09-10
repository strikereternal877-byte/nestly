using FluentAssertions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.ProviderJobs;
using Nestly.Application.ProviderManagement;
using Nestly.Application.Routing;
using Nestly.Application.Tracking;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Interceptors;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Identity.Tests;

/// <summary>
/// Task 271's ETA computation as it is actually reached: through task 269's
/// accepted-ping path, with the routing seam stubbed
/// (<see cref="StubRouteEstimateProvider"/>) because a real route lookup is a
/// billed network call whose answer changes with the traffic.
/// </summary>
/// <remarks>
/// <para>
/// Runs against the in-memory SQLite <see cref="TestDatabase"/>
/// (<c>EnsureCreated</c>, never migrations), so the <c>booking_tracking</c>
/// table these tests read comes from <c>BookingTrackingConfiguration</c> and
/// not from the migration that ships the PostgreSQL table. The two agree only
/// because the migration was generated from that configuration.
/// </para>
/// <para>
/// The domain events raised on the tracking row survive into the assertions
/// only because <see cref="TestDatabase"/> wires no
/// <c>DomainEventDispatchInterceptor</c> - in the real application that
/// interceptor drains and publishes them on save. The tests that read
/// <c>DomainEvents</c> therefore share one <see cref="NestlyDbContext"/> with
/// the service, so EF's identity map hands back the very instance the service
/// mutated.
/// </para>
/// </remarks>
public class BookingEtaServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly Guid _providerId;
    private readonly Guid _adminUserId = Guid.NewGuid();

    /// <summary>The booking's address snapshot - every ETA below is a journey to this point.</summary>
    private const decimal AddressLatitude = 12.9716m;
    private const decimal AddressLongitude = 77.5946m;

    /// <summary>Roughly 2 km north of the address: where the provider starts.</summary>
    private const decimal StartLatitude = 12.9896m;

    /// <summary>Roughly 100 m north of <see cref="StartLatitude"/> - under any sane movement threshold.</summary>
    private const decimal BarelyMovedLatitude = 12.9905m;

    /// <summary>Roughly 2 km north of <see cref="StartLatitude"/>.</summary>
    private const decimal FarMovedLatitude = 13.0076m;

    /// <summary>Ingest that accepts every fix, so only the ETA throttle is under test.</summary>
    private static readonly ProviderLocationIngestOptions UnthrottledIngest = new() { MinimumIntervalSeconds = 0 };

    public BookingEtaServiceTests()
    {
        using var context = _database.CreateContext();
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");
        provider.ChangeStatus(ProviderStatus.Active);
        _providerId = provider.Id;
        context.Add(provider);
        context.SaveChanges();
    }

    // --- seeding -----------------------------------------------------------

    private static BookingProviderAssignmentService CreateAssignmentService(NestlyDbContext context) => new(
        new BookingRepository(context), new ProviderRepository(context), new ServiceRepository(context),
        new BookingProviderAssignmentRepository(context), new ProviderScheduleConflictService(context, TestServices.Occupancy()),
        Options.Create(new AutoAssignmentOptions()), context);

    private static ProviderJobService CreateJobService(NestlyDbContext context, IBookingEtaService etaService) => new(
        new BookingRepository(context),
        new BookingProviderAssignmentRepository(context),
        CreateAssignmentService(context),
        new BookingCompletionProofRepository(context),
        etaService,
        new RecurringBookingPlanRepository(context), new NoOpFileStorageService(),
        TestServices.ActiveJobLimit(context), TestServices.OverrunReassignment(context),
        new PaymentTransactionRepository(context), TestServices.Clock());

    private static ProviderLocationIngestService CreateIngestService(
        NestlyDbContext context,
        IBookingEtaService etaService,
        ProviderLocationIngestOptions? ingestOptions = null) => new(
        new BookingRepository(context),
        new BookingProviderAssignmentRepository(context),
        new ProviderRepository(context),
        new ProviderLocationPingRepository(context),
        etaService,
        Microsoft.Extensions.Options.Options.Create(ingestOptions ?? UnthrottledIngest));

    private async Task<Guid> SeedAcceptedBookingAsync(NestlyDbContext context)
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        await context.AddAsync(customer);

        var booking = new Booking(
            Guid.NewGuid(), customer.Id,
            new CustomerSnapshot("Asha Rao", "9876543210"),
            null,
            new AddressSnapshot("Home", "221B Baker Street", null, null, "560001", "Bengaluru", "Karnataka", AddressLatitude, AddressLongitude, "Asha Rao", "9876543210"),
            new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
            new PriceSnapshot(999m, 1, 999m, 0m, 0m, 999m, 0m, 0m, 0m, 999m));
        booking.AddItem(Guid.NewGuid(), Guid.NewGuid(), "Deep Cleaning", "deep-cleaning", 999m, 1);
        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.TransitionTo(BookingStatus.Confirmed);
        booking.TransitionTo(BookingStatus.AwaitingFulfilment);
        await context.AddAsync(booking);
        await context.SaveChangesAsync();

        (await CreateAssignmentService(context).AssignAsync(
            booking.Id, _adminUserId, new AssignProviderRequest(_providerId, ResponseDeadline: null))).IsSuccess.Should().BeTrue();
        (await CreateJobService(context, new NoOpBookingEtaService()).AcceptAsync(_providerId, booking.Id)).IsSuccess.Should().BeTrue();

        return booking.Id;
    }

    private async Task PingAsync(
        NestlyDbContext context,
        IBookingEtaService etaService,
        Guid bookingId,
        decimal latitude,
        DateTime recordedAtUtc)
    {
        var response = await CreateIngestService(context, etaService).RecordAsync(
            _providerId, bookingId, new RecordProviderLocationRequest(latitude, AddressLongitude, 8m, recordedAtUtc));

        response.IsSuccess.Should().BeTrue();
        response.Value.Accepted.Should().BeTrue("the ingest throttle is disabled in these tests so the ETA throttle is the only one under test");
    }

    private static Task<BookingTracking?> ReadTrackingAsync(NestlyDbContext context, Guid bookingId) =>
        new BookingTrackingRepository(context).GetByBookingAsync(bookingId);

    // --- computation -------------------------------------------------------

    [Fact]
    public async Task An_accepted_ping_computes_and_persists_the_eta()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);
        var route = new StubRouteEstimateProvider(durationSeconds: 540, distanceMetres: 2_600);
        var etaService = BookingEtaTestFactory.CreateEtaService(context, route);

        await PingAsync(context, etaService, bookingId, StartLatitude, DateTime.UtcNow);

        route.CallCount.Should().Be(1);

        await using var readContext = _database.CreateContext();
        var tracking = await ReadTrackingAsync(readContext, bookingId);
        tracking.Should().NotBeNull();
        tracking!.EtaSeconds.Should().Be(540);
        tracking.EtaDistanceMetres.Should().Be(2_600);
        tracking.EtaSource.Should().Be(BookingEtaSource.GoogleMaps);
        tracking.EtaComputedAtUtc.Should().NotBeNull();
        tracking.ProviderId.Should().Be(_providerId);
        tracking.EtaOriginLatitude.Should().Be(StartLatitude, "the ETA's origin is the fix it was computed from, which is the movement throttle's baseline");
        tracking.EtaOriginLongitude.Should().Be(AddressLongitude);
    }

    [Fact]
    public async Task The_eta_is_computed_from_the_latest_fix_to_the_bookings_address_snapshot()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);

        // The real sandbox estimator rather than a scripted number, so the
        // assertion is on the journey the service actually asked about: ~2 km
        // of straight line, x1.3 winding, at 25 km/h is a bit under 6 minutes.
        var sandbox = new SandboxRouteEstimateProvider(
            Microsoft.Extensions.Options.Options.Create(new SandboxRouteEstimateOptions()));
        var etaService = BookingEtaTestFactory.CreateEtaService(context, sandbox);

        await PingAsync(context, etaService, bookingId, StartLatitude, DateTime.UtcNow);

        await using var readContext = _database.CreateContext();
        var tracking = await ReadTrackingAsync(readContext, bookingId);
        tracking!.EtaDistanceMetres.Should().BeInRange(2_500, 2_700);
        tracking.EtaSeconds.Should().BeInRange(330, 400);
    }

    // --- throttle ----------------------------------------------------------

    [Fact]
    public async Task A_second_ping_inside_the_interval_does_not_pay_for_a_second_lookup()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);
        var route = new StubRouteEstimateProvider(durationSeconds: 540);

        // Movement gate held wide open so only the time gate can suppress.
        var etaService = BookingEtaTestFactory.CreateEtaService(context, route, new BookingEtaOptions
        {
            MinimumRecomputeIntervalSeconds = 3600,
            MinimumMovementMetres = 100_000m
        });

        var recordedAtUtc = DateTime.UtcNow.AddSeconds(-30);
        await PingAsync(context, etaService, bookingId, StartLatitude, recordedAtUtc);
        route.Returns(120);
        await PingAsync(context, etaService, bookingId, FarMovedLatitude, recordedAtUtc.AddSeconds(15));

        route.CallCount.Should().Be(1, "the stored estimate is seconds old, so a fresh one is not worth paying for");

        await using var readContext = _database.CreateContext();
        (await ReadTrackingAsync(readContext, bookingId))!.EtaSeconds.Should().Be(540);
    }

    [Fact]
    public async Task A_ping_after_the_interval_does_pay_for_a_second_lookup()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);
        var route = new StubRouteEstimateProvider(durationSeconds: 540);

        // Time gate wide open, movement gate shut: the mirror of the test
        // above, so the two together pin the gate rather than the outcome.
        var etaService = BookingEtaTestFactory.CreateEtaService(context, route, new BookingEtaOptions
        {
            MinimumRecomputeIntervalSeconds = 0,
            MinimumMovementMetres = 100_000m
        });

        var recordedAtUtc = DateTime.UtcNow.AddSeconds(-30);
        await PingAsync(context, etaService, bookingId, StartLatitude, recordedAtUtc);
        route.Returns(120);
        await PingAsync(context, etaService, bookingId, BarelyMovedLatitude, recordedAtUtc.AddSeconds(15));

        route.CallCount.Should().Be(2);

        await using var readContext = _database.CreateContext();
        (await ReadTrackingAsync(readContext, bookingId))!.EtaSeconds.Should().Be(120);
    }

    [Fact]
    public async Task A_barely_moved_provider_does_not_pay_for_a_second_lookup()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);
        var route = new StubRouteEstimateProvider(durationSeconds: 540);

        // Time gate shut for an hour, so only movement can open it.
        var etaService = BookingEtaTestFactory.CreateEtaService(context, route, new BookingEtaOptions
        {
            MinimumRecomputeIntervalSeconds = 3600,
            MinimumMovementMetres = 250m
        });

        var recordedAtUtc = DateTime.UtcNow.AddSeconds(-30);
        await PingAsync(context, etaService, bookingId, StartLatitude, recordedAtUtc);
        route.Returns(120);
        await PingAsync(context, etaService, bookingId, BarelyMovedLatitude, recordedAtUtc.AddSeconds(15));

        route.CallCount.Should().Be(1, "100 m is GPS scatter, not a journey worth re-pricing");

        await using var readContext = _database.CreateContext();
        (await ReadTrackingAsync(readContext, bookingId))!.EtaSeconds.Should().Be(540);
    }

    [Fact]
    public async Task A_provider_who_has_covered_ground_does_pay_for_a_second_lookup()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);
        var route = new StubRouteEstimateProvider(durationSeconds: 540);

        var etaService = BookingEtaTestFactory.CreateEtaService(context, route, new BookingEtaOptions
        {
            MinimumRecomputeIntervalSeconds = 3600,
            MinimumMovementMetres = 250m
        });

        var recordedAtUtc = DateTime.UtcNow.AddSeconds(-30);
        await PingAsync(context, etaService, bookingId, StartLatitude, recordedAtUtc);
        route.Returns(120);
        await PingAsync(context, etaService, bookingId, FarMovedLatitude, recordedAtUtc.AddSeconds(15));

        route.CallCount.Should().Be(2, "2 km makes the stored estimate wrong however recently it was computed");

        await using var readContext = _database.CreateContext();
        var tracking = await ReadTrackingAsync(readContext, bookingId);
        tracking!.EtaSeconds.Should().Be(120);
        tracking.EtaOriginLatitude.Should().Be(FarMovedLatitude);
    }

    [Fact]
    public async Task Chatty_pinging_does_not_multiply_the_route_lookups()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);
        var route = new StubRouteEstimateProvider(durationSeconds: 540);
        var etaService = BookingEtaTestFactory.CreateEtaService(context, route);

        // The whole point of the feature: ETA cost is decoupled from ping
        // frequency. Twelve accepted fixes, none of them far enough apart in
        // time or space to justify a recompute, cost exactly one lookup.
        var recordedAtUtc = DateTime.UtcNow.AddSeconds(-60);
        for (int index = 0; index < 12; index++)
        {
            await PingAsync(context, etaService, bookingId, StartLatitude + (index * 0.00002m), recordedAtUtc.AddSeconds(index));
        }

        route.CallCount.Should().Be(1);
    }

    // --- trackable states --------------------------------------------------

    [Fact]
    public async Task An_eta_is_cleared_once_the_booking_leaves_the_trackable_states()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);
        var route = new StubRouteEstimateProvider(durationSeconds: 540);
        var etaService = BookingEtaTestFactory.CreateEtaService(context, route);

        await PingAsync(context, etaService, bookingId, StartLatitude, DateTime.UtcNow);
        (await ReadTrackingAsync(context, bookingId))!.HasEta.Should().BeTrue();

        var bookingRepository = new BookingRepository(context);
        var booking = await bookingRepository.GetByIdAsync(bookingId);
        booking!.TransitionTo(BookingStatus.InProgress, "Provider started the job.");
        booking.TransitionTo(BookingStatus.Completed, "Job finished.");
        await bookingRepository.UpdateAsync(booking);

        await etaService.RefreshAsync(bookingId);

        await using var readContext = _database.CreateContext();
        var tracking = await ReadTrackingAsync(readContext, bookingId);
        tracking!.HasEta.Should().BeFalse("a stale 'arriving in four minutes' on a completed job is worse than no ETA");
        tracking.EtaComputedAtUtc.Should().BeNull();
        tracking.EtaSource.Should().BeNull();
        route.CallCount.Should().Be(1, "a finished job must not buy a route lookup to be told the ETA it is about to discard");
    }

    [Fact]
    public async Task No_eta_is_computed_for_a_booking_that_is_not_trackable()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);
        var route = new StubRouteEstimateProvider();
        var etaService = BookingEtaTestFactory.CreateEtaService(context, route);

        // A fix exists, so nothing but the trackable-state check can stop the
        // computation - without it this test would pass on the missing-ping
        // early return instead and prove nothing.
        await PingAsync(context, new NoOpBookingEtaService(), bookingId, StartLatitude, DateTime.UtcNow);

        var bookingRepository = new BookingRepository(context);
        var booking = await bookingRepository.GetByIdAsync(bookingId);
        booking!.TransitionTo(BookingStatus.CancelledByCustomer, "Customer changed their mind.");
        await bookingRepository.UpdateAsync(booking);

        await etaService.RefreshAsync(bookingId);

        route.CallCount.Should().Be(0);
        (await ReadTrackingAsync(context, bookingId)).Should().BeNull(
            "recording the absence of an estimate would put a row on every booking that ever reached a terminal status");
    }

    [Fact]
    public async Task Leaving_the_trackable_states_is_what_the_suppression_handler_reacts_to()
    {
        var cleared = new List<Guid>();
        var handler = new BookingEtaSuppressionHandler(new RecordingEtaService(cleared));
        var bookingId = Guid.NewGuid();

        async Task HandleAsync(BookingStatus from, BookingStatus to) =>
            await handler.Handle(
                new DomainEventNotification<BookingStatusChangedEvent>(
                    new BookingStatusChangedEvent(bookingId, from, to)),
                CancellationToken.None);

        await HandleAsync(BookingStatus.Assigned, BookingStatus.ProviderEnRoute);
        await HandleAsync(BookingStatus.ProviderEnRoute, BookingStatus.ProviderArrived);
        await HandleAsync(BookingStatus.AwaitingFulfilment, BookingStatus.Assigned);
        cleared.Should().BeEmpty("moves within the trackable window, and into it, leave the ETA alone");

        // Transitions that never touch the trackable window at all. Reading
        // FromStatus is what keeps this handler off the tracking table for the
        // great majority of status changes in the system, which have no ETA to
        // clear and never did.
        await HandleAsync(BookingStatus.PaymentPending, BookingStatus.Confirmed);
        await HandleAsync(BookingStatus.Confirmed, BookingStatus.AwaitingFulfilment);
        await HandleAsync(BookingStatus.Completed, BookingStatus.RefundPending);
        cleared.Should().BeEmpty("a booking that was never trackable has no estimate to take away");

        await HandleAsync(BookingStatus.InProgress, BookingStatus.Completed);
        await HandleAsync(BookingStatus.ProviderEnRoute, BookingStatus.CancelledByCustomer);
        cleared.Should().Equal(bookingId, bookingId);
    }

    private sealed class RecordingEtaService : IBookingEtaService
    {
        private readonly List<Guid> _cleared;

        public RecordingEtaService(List<Guid> cleared) => _cleared = cleared;

        public Task RefreshAsync(Guid bookingId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ClearAsync(Guid bookingId, CancellationToken cancellationToken = default)
        {
            _cleared.Add(bookingId);
            return Task.CompletedTask;
        }
    }

    // --- the event ---------------------------------------------------------

    [Fact]
    public async Task A_material_change_raises_BookingEtaUpdatedEvent()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);
        var route = new StubRouteEstimateProvider(durationSeconds: 540);
        var etaService = BookingEtaTestFactory.CreateEtaService(context, route, new BookingEtaOptions
        {
            MinimumRecomputeIntervalSeconds = 0,
            MinimumMovementMetres = 100_000m
        });

        var recordedAtUtc = DateTime.UtcNow.AddSeconds(-30);
        await PingAsync(context, etaService, bookingId, StartLatitude, recordedAtUtc);
        (await ReadTrackingAsync(context, bookingId))!.ClearDomainEvents();

        route.Returns(300);
        await PingAsync(context, etaService, bookingId, BarelyMovedLatitude, recordedAtUtc.AddSeconds(15));

        var tracking = await ReadTrackingAsync(context, bookingId);
        var raised = tracking!.DomainEvents.OfType<BookingEtaUpdatedEvent>().Should().ContainSingle().Subject;
        raised.BookingId.Should().Be(bookingId);
        raised.ProviderId.Should().Be(_providerId);
        raised.EtaSeconds.Should().Be(300);
        raised.EtaComputedAtUtc.Should().Be(tracking.EtaComputedAtUtc);
    }

    [Fact]
    public async Task Jitter_does_not_raise_BookingEtaUpdatedEvent()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);
        var route = new StubRouteEstimateProvider(durationSeconds: 540);
        var etaService = BookingEtaTestFactory.CreateEtaService(context, route, new BookingEtaOptions
        {
            MinimumRecomputeIntervalSeconds = 0,
            MinimumMovementMetres = 100_000m
        });

        var recordedAtUtc = DateTime.UtcNow.AddSeconds(-30);
        await PingAsync(context, etaService, bookingId, StartLatitude, recordedAtUtc);
        (await ReadTrackingAsync(context, bookingId))!.ClearDomainEvents();

        // A provider stopped at a light: the route is unchanged and the router
        // returns a number wobbling by seconds. None of it is news.
        route.Returns(555);
        await PingAsync(context, etaService, bookingId, BarelyMovedLatitude, recordedAtUtc.AddSeconds(15));

        var tracking = await ReadTrackingAsync(context, bookingId);
        tracking!.DomainEvents.OfType<BookingEtaUpdatedEvent>().Should().BeEmpty();
        tracking.EtaSeconds.Should().Be(555, "the fresher number is still stored; only the announcement is suppressed");
    }

    // --- provenance --------------------------------------------------------

    [Fact]
    public async Task A_degraded_estimate_is_recorded_as_sandbox()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);
        var route = new StubRouteEstimateProvider(durationSeconds: 700, source: RouteEstimateSource.Sandbox);
        var etaService = BookingEtaTestFactory.CreateEtaService(context, route);

        await PingAsync(context, etaService, bookingId, StartLatitude, DateTime.UtcNow);

        await using var readContext = _database.CreateContext();
        var tracking = await ReadTrackingAsync(readContext, bookingId);
        tracking!.EtaSource.Should().Be(BookingEtaSource.Sandbox,
            "a support agent has to be able to tell a real traffic-aware ETA from an approximation");
        tracking.EtaSeconds.Should().Be(700);
    }

    [Fact]
    public async Task An_unusable_response_still_yields_a_sandbox_estimate_rather_than_no_eta()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);
        var route = new StubRouteEstimateProvider { ReturnsNothing = true };
        var etaService = BookingEtaTestFactory.CreateEtaService(context, route);

        await PingAsync(context, etaService, bookingId, StartLatitude, DateTime.UtcNow);

        await using var readContext = _database.CreateContext();
        var tracking = await ReadTrackingAsync(readContext, bookingId);
        tracking.Should().NotBeNull("maps returning nothing degrades to an approximation, it does not remove the ETA");
        tracking!.EtaSource.Should().Be(BookingEtaSource.Sandbox);
        tracking.EtaSeconds.Should().BeGreaterThan(0);
    }

    // --- the en-route trigger ----------------------------------------------

    [Fact]
    public async Task Marking_en_route_refreshes_the_eta_without_waiting_for_a_ping()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);
        var route = new StubRouteEstimateProvider(durationSeconds: 480);
        var etaService = BookingEtaTestFactory.CreateEtaService(context, route);

        // A fix exists (a provider tapping en-route has an app reporting
        // location) but no ETA has been computed from it yet.
        await new ProviderLocationPingRepository(context).AddAsync(new ProviderLocationPing(
            Guid.NewGuid(), _providerId, bookingId, StartLatitude, AddressLongitude, 8m,
            DateTime.UtcNow, DateTime.UtcNow));

        (await CreateJobService(context, etaService).MarkEnRouteAsync(_providerId, bookingId))
            .IsSuccess.Should().BeTrue();

        route.CallCount.Should().Be(1);

        await using var readContext = _database.CreateContext();
        (await ReadTrackingAsync(readContext, bookingId))!.EtaSeconds.Should().Be(480);
    }

    [Fact]
    public async Task No_eta_is_computed_before_any_fix_has_been_reported()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);
        var route = new StubRouteEstimateProvider();
        var etaService = BookingEtaTestFactory.CreateEtaService(context, route);

        await etaService.RefreshAsync(bookingId);

        route.CallCount.Should().Be(0,
            "an ETA off the provider's last-known coordinate from some other job would be a number about the wrong journey");
        (await ReadTrackingAsync(context, bookingId)).Should().BeNull();
    }

    public void Dispose() => _database.Dispose();
}
