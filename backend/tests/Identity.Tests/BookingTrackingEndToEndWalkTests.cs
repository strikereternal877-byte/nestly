using FluentAssertions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.ProviderJobs;
using Nestly.Application.ProviderManagement;
using Nestly.Application.Tracking;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Identity.Tests;

/// <summary>
/// Task 285's end-to-end requirement: a single walk from assignment through
/// completion, asserting what <see cref="BookingTrackingQueryService.GetForCustomerAsync"/>
/// - the exact read the customer's tracking screen polls - returns after
/// every step. Every other tracking test in this codebase is unit-level, one
/// component at a time (<c>ProviderJobServiceTests</c>,
/// <c>ProviderLocationIngestServiceTests</c>, <c>BookingEtaServiceTests</c>,
/// <c>BookingTrackingQueryServiceTests</c>); none of them prove the pieces
/// actually agree with each other once wired together the way the real
/// consumer-api process wires them - a customer looking at a screen mid-job,
/// not a component test looking at one service's return value.
/// </summary>
public class BookingTrackingEndToEndWalkTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly Guid _providerId;
    private readonly Guid _customerId = Guid.NewGuid();
    private readonly Guid _adminUserId = Guid.NewGuid();

    private const decimal AddressLatitude = 12.9716m;
    private const decimal AddressLongitude = 77.5946m;
    /// <summary>~2km north of the address - where the provider's first ping places them.</summary>
    private const decimal StartLatitude = 12.9896m;

    public BookingTrackingEndToEndWalkTests()
    {
        using var context = _database.CreateContext();
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");
        provider.ChangeStatus(ProviderStatus.Active);
        _providerId = provider.Id;
        context.Add(provider);
        context.SaveChanges();
    }

    public void Dispose() => _database.Dispose();

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
        NestlyDbContext context, IBookingEtaService etaService) => new(
        new BookingRepository(context),
        new BookingProviderAssignmentRepository(context),
        new ProviderRepository(context),
        new ProviderLocationPingRepository(context),
        etaService,
        Microsoft.Extensions.Options.Options.Create(new ProviderLocationIngestOptions { MinimumIntervalSeconds = 0 }));

    private static BookingTrackingQueryService CreateTrackingReader(NestlyDbContext context) => new(
        new BookingRepository(context),
        new BookingProviderAssignmentRepository(context),
        new ProviderRepository(context),
        new ProviderLocationPingRepository(context),
        new BookingTrackingRepository(context),
        new ReviewRepository(context));

    private async Task<Guid> SeedAwaitingFulfilmentBookingAsync(NestlyDbContext context)
    {
        var customer = new Customer(_customerId, "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        await context.AddAsync(customer);

        var booking = new Booking(
            Guid.NewGuid(), customer.Id,
            new CustomerSnapshot("Asha Rao", customer.Mobile),
            null,
            new AddressSnapshot("Home", "221B Baker Street", null, null, "560001", "Bengaluru", "Karnataka", AddressLatitude, AddressLongitude, "Asha Rao", customer.Mobile),
            new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
            new PriceSnapshot(999m, 1, 999m, 0m, 0m, 999m, 0m, 0m, 0m, 999m));
        booking.AddItem(Guid.NewGuid(), Guid.NewGuid(), "Deep Cleaning", "deep-cleaning", 999m, 1);
        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.TransitionTo(BookingStatus.Confirmed);
        booking.TransitionTo(BookingStatus.AwaitingFulfilment);
        await context.AddAsync(booking);
        await context.SaveChangesAsync();

        return booking.Id;
    }

    /// <summary>
    /// The full walk. One shared <see cref="NestlyDbContext"/> throughout -
    /// exactly like <c>BookingEtaServiceTests</c> - so each step reads back
    /// what the previous one actually persisted rather than what a second
    /// context's stale identity map thinks happened.
    /// </summary>
    [Fact]
    public async Task Assign_accept_en_route_ping_eta_arrived_start_complete_walk_matches_the_customer_facing_read_at_every_step()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAwaitingFulfilmentBookingAsync(context);
        var route = new StubRouteEstimateProvider(durationSeconds: 540, distanceMetres: 2_600);
        var etaService = BookingEtaTestFactory.CreateEtaService(context, route);
        var jobs = CreateJobService(context, etaService);
        var ingest = CreateIngestService(context, etaService);
        var tracking = CreateTrackingReader(context);

        // --- Assign: the customer's booking becomes trackable and gains a provider identity, before that provider has done anything. ---
        var assignResult = await CreateAssignmentService(context).AssignAsync(
            bookingId, _adminUserId, new AssignProviderRequest(_providerId, ResponseDeadline: null));
        assignResult.IsSuccess.Should().BeTrue();

        var afterAssign = await tracking.GetForCustomerAsync(_customerId, bookingId);
        afterAssign.IsSuccess.Should().BeTrue("Assigned is the first trackable status");
        afterAssign.Value.Status.Should().Be(BookingStatus.Assigned);
        afterAssign.Value.Provider.Should().NotBeNull();
        afterAssign.Value.Provider!.MaskedPhone.Should().NotBeNullOrEmpty("the customer must never see the raw number");
        afterAssign.Value.Provider!.MaskedPhone.Should().NotBe("+919876543210", "the number on the wire must be masked, not the raw one");
        afterAssign.Value.ProviderLocation.Should().BeNull("no ping has arrived yet");
        afterAssign.Value.Eta.Should().BeNull("no route lookup has run yet");
        afterAssign.Value.Destination.Latitude.Should().Be(AddressLatitude);

        // --- Accept: BookingProviderAssignmentStatus changes, but the booking's own status does not - the customer still sees "Assigned". ---
        (await jobs.AcceptAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();

        var afterAccept = await tracking.GetForCustomerAsync(_customerId, bookingId);
        afterAccept.Value.Status.Should().Be(BookingStatus.Assigned, "accepting the offer does not itself move the booking's lifecycle status");
        afterAccept.Value.Provider.Should().NotBeNull();

        // --- En route: the booking's status itself now moves. No ping has arrived yet, so RefreshAsync has nothing to route from and no-ops - task 271's own guard against computing an ETA from a stale coordinate off a different job. ---
        (await jobs.MarkEnRouteAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();

        var afterEnRoute = await tracking.GetForCustomerAsync(_customerId, bookingId);
        afterEnRoute.Value.Status.Should().Be(BookingStatus.ProviderEnRoute);
        afterEnRoute.Value.Eta.Should().BeNull("no location fix exists yet for the refresh to route from");
        route.CallCount.Should().Be(0);

        // --- Ping: a location fix lands, and the customer-facing read now carries both a live position and the first ETA. ---
        var pingResponse = await ingest.RecordAsync(
            _providerId, bookingId,
            new RecordProviderLocationRequest(StartLatitude, AddressLongitude, 8m, DateTime.UtcNow));
        pingResponse.IsSuccess.Should().BeTrue();
        pingResponse.Value.Accepted.Should().BeTrue();

        var afterPing = await tracking.GetForCustomerAsync(_customerId, bookingId);
        afterPing.Value.ProviderLocation.Should().NotBeNull();
        afterPing.Value.ProviderLocation!.Latitude.Should().Be(StartLatitude);
        afterPing.Value.Eta.Should().NotBeNull("the first accepted fix is what finally gives the refresh something to route from");
        route.CallCount.Should().Be(1);

        // --- Arrived: no further route lookup (an arrived provider's remaining travel time is zero by definition - ProviderJobService's own doc comment on MarkArrivedAsync). ---
        (await jobs.MarkArrivedAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();

        var afterArrived = await tracking.GetForCustomerAsync(_customerId, bookingId);
        afterArrived.Value.Status.Should().Be(BookingStatus.ProviderArrived);
        route.CallCount.Should().Be(1, "MarkArrivedAsync does not refresh the ETA");

        // --- Start: still trackable, still visible to the customer. ---
        (await jobs.StartAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();

        var afterStart = await tracking.GetForCustomerAsync(_customerId, bookingId);
        afterStart.Value.Status.Should().Be(BookingStatus.InProgress);
        afterStart.IsSuccess.Should().BeTrue("InProgress is still a trackable status");

        // --- Complete: the tracking sub-resource stops existing the moment the job ends - a 404, not an empty 200 (BookingTrackingQueryService's own doc comment). ---
        (await jobs.SubmitCompletionProofAsync(
            _providerId, bookingId, new SubmitCompletionProofRequest(["s3://proofs/job-photo.jpg"], []))).IsSuccess.Should().BeTrue();
        var completeResult = await jobs.CompleteAsync(_providerId, bookingId);
        completeResult.IsSuccess.Should().BeTrue();

        var afterComplete = await tracking.GetForCustomerAsync(_customerId, bookingId);
        afterComplete.IsFailure.Should().BeTrue("a completed booking has nothing left to track");
        afterComplete.Error.Code.Should().Be("Booking.TrackingUnavailable");
    }
}
