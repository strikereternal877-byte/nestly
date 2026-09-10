using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.ProviderJobs;
using Nestly.Application.ProviderManagement;
using Nestly.Application.Tracking;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Identity.Tests;

/// <summary>
/// Task 269's location ingest - the fail-closed rules deciding when the
/// platform may know where a provider is, the per-booking throttle, and the
/// device-clock contract <see cref="Provider.UpdateLocation"/>'s
/// <c>observedAtUtc</c> parameter exists for.
/// </summary>
/// <remarks>
/// Runs against the in-memory SQLite <see cref="TestDatabase"/>
/// (<c>EnsureCreated</c>, never migrations), same as
/// <see cref="ProviderJobServiceTests"/>. One divergence from the PostgreSQL
/// runtime matters here: SQLite has no native timestamp type, so a
/// <c>DateTime</c> round-trips through the provider without its
/// <see cref="DateTimeKind"/>. That is why the assertions below compare
/// <see cref="ProviderLocationPing.RecordedAtUtc"/> as a bare value rather
/// than asserting on its Kind.
/// </remarks>
public class ProviderLocationIngestServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly Guid _providerId;
    private readonly Guid _otherProviderId;
    private readonly Guid _adminUserId = Guid.NewGuid();

    private const decimal Latitude = 12.9716m;
    private const decimal Longitude = 77.5946m;

    public ProviderLocationIngestServiceTests()
    {
        using var context = _database.CreateContext();
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");
        var otherProvider = new Provider(Guid.NewGuid(), "Meena Iyer", "Meena's Services", ProviderType.Individual, "+919876500000");
        provider.ChangeStatus(ProviderStatus.Active);
        otherProvider.ChangeStatus(ProviderStatus.Active);
        _providerId = provider.Id;
        _otherProviderId = otherProvider.Id;
        context.AddRange(provider, otherProvider);
        context.SaveChanges();
    }

    private static ProviderLocationIngestService CreateIngestService(
        NestlyDbContext context,
        ProviderLocationIngestOptions? options = null,
        IBookingEtaService? etaService = null) => new(
        new BookingRepository(context),
        new BookingProviderAssignmentRepository(context),
        new ProviderRepository(context),
        new ProviderLocationPingRepository(context),
        etaService ?? new NoOpBookingEtaService(),
        Microsoft.Extensions.Options.Options.Create(options ?? new ProviderLocationIngestOptions()));

    private static ProviderJobService CreateJobService(NestlyDbContext context) => new(
        new BookingRepository(context),
        new BookingProviderAssignmentRepository(context),
        CreateAssignmentService(context),
        new BookingCompletionProofRepository(context),
        new NoOpBookingEtaService(),
        new RecurringBookingPlanRepository(context), new NoOpFileStorageService(),
        TestServices.ActiveJobLimit(context), TestServices.OverrunReassignment(context),
        new PaymentTransactionRepository(context), TestServices.Clock());

    private static BookingProviderAssignmentService CreateAssignmentService(NestlyDbContext context) => new(
        new BookingRepository(context), new ProviderRepository(context), new ServiceRepository(context),
        new BookingProviderAssignmentRepository(context), new ProviderScheduleConflictService(context, TestServices.Occupancy()),
        Options.Create(new AutoAssignmentOptions()), context);

    private static Booking NewAwaitingFulfilmentBooking(Guid customerId, int slotDayOffset)
    {
        var booking = new Booking(
            Guid.NewGuid(), customerId,
            new CustomerSnapshot("Asha Rao", "9876543210"),
            null,
            new AddressSnapshot("Home", "221B Baker Street", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210"),
            new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(slotDayOffset)), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
            new PriceSnapshot(999m, 1, 999m, 0m, 0m, 999m, 0m, 0m, 0m, 999m));
        booking.AddItem(Guid.NewGuid(), Guid.NewGuid(), "Deep Cleaning", "deep-cleaning", 999m, 1);
        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.TransitionTo(BookingStatus.Confirmed);
        booking.TransitionTo(BookingStatus.AwaitingFulfilment);
        return booking;
    }

    /// <summary>
    /// Seeds a booking Assigned to <see cref="_providerId"/> through the real
    /// admin assignment flow (task 147), offer not yet answered.
    /// <paramref name="slotDayOffset"/> exists because task 288 refuses to
    /// double-book one provider into overlapping slots: a test needing two
    /// live jobs for the same provider has to put them on different days.
    /// </summary>
    private async Task<Guid> SeedAssignedBookingAsync(NestlyDbContext context, int slotDayOffset = 0)
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        await context.AddAsync(customer);

        var booking = NewAwaitingFulfilmentBooking(customer.Id, slotDayOffset);
        await context.AddAsync(booking);
        await context.SaveChangesAsync();

        var assignResult = await CreateAssignmentService(context).AssignAsync(
            booking.Id, _adminUserId, new AssignProviderRequest(_providerId, ResponseDeadline: null));
        assignResult.IsSuccess.Should().BeTrue();

        return booking.Id;
    }

    /// <summary>Seeds a booking the provider has actually accepted - the first state in which location may be reported.</summary>
    private async Task<Guid> SeedAcceptedBookingAsync(NestlyDbContext context, int slotDayOffset = 0)
    {
        var bookingId = await SeedAssignedBookingAsync(context, slotDayOffset);
        (await CreateJobService(context).AcceptAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();
        return bookingId;
    }

    private static RecordProviderLocationRequest Fix(DateTime? recordedAtUtc = null, decimal? accuracyMetres = 8m) =>
        new(Latitude, Longitude, accuracyMetres, recordedAtUtc ?? DateTime.UtcNow);

    // ---------------------------------------------------------------- happy path

    [Fact]
    public async Task RecordAsync_writes_a_ping_and_refreshes_the_providers_last_known_position()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);
        var recordedAtUtc = DateTime.UtcNow.AddSeconds(-5);

        var result = await CreateIngestService(context).RecordAsync(_providerId, bookingId, Fix(recordedAtUtc));

        result.IsSuccess.Should().BeTrue();
        result.Value.Accepted.Should().BeTrue();
        result.Value.PingId.Should().NotBeNull();

        var ping = await new ProviderLocationPingRepository(context).GetLatestForBookingAsync(bookingId);
        ping.Should().NotBeNull();
        ping!.Id.Should().Be(result.Value.PingId!.Value);
        ping.ProviderId.Should().Be(_providerId);
        ping.BookingId.Should().Be(bookingId);
        ping.Latitude.Should().Be(Latitude);
        ping.Longitude.Should().Be(Longitude);
        ping.AccuracyMetres.Should().Be(8m);
        ping.Source.Should().Be(ProviderLocationSource.ProviderApp);

        var provider = await new ProviderRepository(context).GetByIdAsync(_providerId);
        provider!.Latitude.Should().Be(Latitude);
        provider.Longitude.Should().Be(Longitude);
        provider.LocationUpdatedAtUtc.Should().BeCloseTo(recordedAtUtc, TimeSpan.FromMilliseconds(1));
    }

    /// <summary>
    /// The point of task 268's optional <c>observedAtUtc</c> parameter: a fix
    /// that spent time in an offline queue must be stored, and must age, by
    /// the device clock - not by the moment the server happened to receive it.
    /// </summary>
    [Fact]
    public async Task RecordAsync_stores_the_device_clock_not_the_server_receive_time()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);
        var recordedAtUtc = DateTime.UtcNow.AddMinutes(-2);

        var result = await CreateIngestService(context).RecordAsync(_providerId, bookingId, Fix(recordedAtUtc));

        result.IsSuccess.Should().BeTrue();

        var ping = await new ProviderLocationPingRepository(context).GetLatestForBookingAsync(bookingId);
        ping!.RecordedAtUtc.Should().BeCloseTo(recordedAtUtc, TimeSpan.FromMilliseconds(1));
        ping.ReceivedAtUtc.Should().BeAfter(ping.RecordedAtUtc.AddMinutes(1));

        var provider = await new ProviderRepository(context).GetByIdAsync(_providerId);
        provider!.LocationUpdatedAtUtc.Should().BeCloseTo(recordedAtUtc, TimeSpan.FromMilliseconds(1));
        provider.LocationUpdatedAtUtc.Should().BeBefore(DateTime.UtcNow.AddMinutes(-1));
    }

    /// <summary>
    /// Task 272 raises <see cref="ProviderLocationUpdatedEvent"/> from the
    /// ping's constructor, so the ingest path must not raise it again -
    /// exactly one event per accepted ping, and none at all for a dropped one.
    /// </summary>
    [Fact]
    public async Task RecordAsync_raises_exactly_one_ProviderLocationUpdatedEvent_per_accepted_ping()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);

        var accepted = await CreateIngestService(context).RecordAsync(_providerId, bookingId, Fix());
        accepted.Value.Accepted.Should().BeTrue();

        // TestDatabase attaches no DomainEventDispatchInterceptor, so the
        // events stay on the tracked aggregate and can be counted directly.
        var ping = await context.Set<ProviderLocationPing>()
            .SingleAsync(p => p.Id == accepted.Value.PingId!.Value);
        ping.DomainEvents.OfType<ProviderLocationUpdatedEvent>().Should().ContainSingle()
            .Which.Should().Match<ProviderLocationUpdatedEvent>(e =>
                e.PingId == ping.Id &&
                e.ProviderId == _providerId &&
                e.BookingId == bookingId);

        var dropped = await CreateIngestService(context).RecordAsync(_providerId, bookingId, Fix());
        dropped.Value.Accepted.Should().BeFalse();

        context.ChangeTracker.Entries<ProviderLocationPing>()
            .SelectMany(e => e.Entity.DomainEvents.OfType<ProviderLocationUpdatedEvent>())
            .Should().ContainSingle("a dropped ping must raise no event at all");
    }

    // ---------------------------------------------------------------- 403: not the live assignment

    [Fact]
    public async Task RecordAsync_refuses_a_provider_who_is_not_on_the_assignment()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);

        var result = await CreateIngestService(context).RecordAsync(_otherProviderId, bookingId, Fix());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderLocation.NotAssigned");
        result.Error.Type.Should().Be(Nestly.BuildingBlocks.Results.ErrorType.Forbidden);

        (await new ProviderLocationPingRepository(context).GetLatestForBookingAsync(bookingId)).Should().BeNull();
    }

    /// <summary>
    /// "Live" means the current assignment, not any assignment ever held: a
    /// provider who rejected the job keeps no right to keep reporting where
    /// they are.
    /// </summary>
    [Fact]
    public async Task RecordAsync_refuses_a_provider_whose_assignment_is_no_longer_live()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAssignedBookingAsync(context);
        (await CreateJobService(context).RejectAsync(_providerId, bookingId, new RejectJobRequest("Too far away")))
            .IsSuccess.Should().BeTrue();

        var result = await CreateIngestService(context).RecordAsync(_providerId, bookingId, Fix());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderLocation.NotAssigned");
    }

    [Fact]
    public async Task RecordAsync_returns_not_found_for_an_unknown_booking()
    {
        await using var context = _database.CreateContext();

        var result = await CreateIngestService(context).RecordAsync(_providerId, Guid.NewGuid(), Fix());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderLocation.NotFound");
        result.Error.Type.Should().Be(Nestly.BuildingBlocks.Results.ErrorType.NotFound);
    }

    // ---------------------------------------------------------------- 409: untrackable state

    /// <summary>Before accept: the offer is outstanding and the provider may still decline it.</summary>
    [Fact]
    public async Task RecordAsync_refuses_a_job_that_has_only_been_offered()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAssignedBookingAsync(context);

        var result = await CreateIngestService(context).RecordAsync(_providerId, bookingId, Fix());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderLocation.NotAccepted");
        result.Error.Type.Should().Be(Nestly.BuildingBlocks.Results.ErrorType.Conflict);

        (await new ProviderLocationPingRepository(context).GetLatestForBookingAsync(bookingId)).Should().BeNull();
    }

    /// <summary>After completion: tracking must stop the moment the work does.</summary>
    [Fact]
    public async Task RecordAsync_refuses_a_completed_job()
    {
        await using var context = _database.CreateContext();
        var jobService = CreateJobService(context);
        var bookingId = await SeedAcceptedBookingAsync(context);
        (await jobService.StartAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();
        (await jobService.SubmitCompletionProofAsync(
            _providerId, bookingId,
            new SubmitCompletionProofRequest(["s3://proofs/done.jpg"], []))).IsSuccess.Should().BeTrue();
        (await jobService.CompleteAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();

        var result = await CreateIngestService(context).RecordAsync(_providerId, bookingId, Fix());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderLocation.NotTrackable");
        result.Error.Type.Should().Be(Nestly.BuildingBlocks.Results.ErrorType.Conflict);
    }

    [Fact]
    public async Task RecordAsync_refuses_a_cancelled_job()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);

        var bookingRepository = new BookingRepository(context);
        var booking = await bookingRepository.GetByIdAsync(bookingId);
        booking!.TransitionTo(BookingStatus.CancelledByCustomer, "Customer changed their mind.");
        await bookingRepository.UpdateAsync(booking);

        var result = await CreateIngestService(context).RecordAsync(_providerId, bookingId, Fix());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderLocation.NotTrackable");
    }

    /// <summary>
    /// Every state in <c>BookingLifecycle.IsTrackable</c> really does ingest.
    /// The statuses are walked in lifecycle order because Assigned -&gt;
    /// ProviderArrived is not a legal transition - a provider arrives by way
    /// of being en route.
    /// </summary>
    [Theory]
    [InlineData(BookingStatus.Assigned)]
    [InlineData(BookingStatus.ProviderEnRoute)]
    [InlineData(BookingStatus.ProviderArrived)]
    [InlineData(BookingStatus.InProgress)]
    public async Task RecordAsync_accepts_every_trackable_booking_state(BookingStatus status)
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);

        var bookingRepository = new BookingRepository(context);
        var booking = await bookingRepository.GetByIdAsync(bookingId);
        foreach (var step in new[] { BookingStatus.ProviderEnRoute, BookingStatus.ProviderArrived, BookingStatus.InProgress })
        {
            if (booking!.Status == status)
            {
                break;
            }

            booking.TransitionTo(step);
        }

        booking!.Status.Should().Be(status);
        await bookingRepository.UpdateAsync(booking);

        var result = await CreateIngestService(context).RecordAsync(_providerId, bookingId, Fix());

        result.IsSuccess.Should().BeTrue();
        result.Value.Accepted.Should().BeTrue();
    }

    // ---------------------------------------------------------------- 400: validation bounds

    [Theory]
    [InlineData(-90.001, 0, "ProviderLocation.InvalidLatitude")]
    [InlineData(90.001, 0, "ProviderLocation.InvalidLatitude")]
    [InlineData(0, -180.001, "ProviderLocation.InvalidLongitude")]
    [InlineData(0, 180.001, "ProviderLocation.InvalidLongitude")]
    public async Task RecordAsync_refuses_coordinates_outside_their_bounds(double latitude, double longitude, string expectedCode)
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);

        var result = await CreateIngestService(context).RecordAsync(
            _providerId, bookingId,
            new RecordProviderLocationRequest((decimal)latitude, (decimal)longitude, 8m, DateTime.UtcNow));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(expectedCode);
        result.Error.Type.Should().Be(Nestly.BuildingBlocks.Results.ErrorType.Validation);

        (await new ProviderLocationPingRepository(context).GetLatestForBookingAsync(bookingId)).Should().BeNull();
    }

    [Theory]
    [InlineData(-90)]
    [InlineData(90)]
    public async Task RecordAsync_accepts_the_latitude_bounds_themselves(double latitude)
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);

        var result = await CreateIngestService(context).RecordAsync(
            _providerId, bookingId,
            new RecordProviderLocationRequest((decimal)latitude, 180m, 0m, DateTime.UtcNow));

        result.IsSuccess.Should().BeTrue();
        result.Value.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task RecordAsync_refuses_negative_accuracy()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);

        var result = await CreateIngestService(context).RecordAsync(_providerId, bookingId, Fix(accuracyMetres: -0.1m));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderLocation.InvalidAccuracy");
    }

    [Fact]
    public async Task RecordAsync_accepts_a_fix_with_no_accuracy_reported()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);

        var result = await CreateIngestService(context).RecordAsync(_providerId, bookingId, Fix(accuracyMetres: null));

        result.IsSuccess.Should().BeTrue();
        (await new ProviderLocationPingRepository(context).GetLatestForBookingAsync(bookingId))!
            .AccuracyMetres.Should().BeNull();
    }

    [Fact]
    public async Task RecordAsync_refuses_a_fix_recorded_in_the_future()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);
        var options = new ProviderLocationIngestOptions { FutureSkewToleranceSeconds = 30 };

        var result = await CreateIngestService(context, options)
            .RecordAsync(_providerId, bookingId, Fix(DateTime.UtcNow.AddSeconds(31)));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderLocation.RecordedInFuture");

        (await new ProviderLocationPingRepository(context).GetLatestForBookingAsync(bookingId)).Should().BeNull();
    }

    [Fact]
    public async Task RecordAsync_tolerates_a_device_clock_running_slightly_ahead()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);
        var options = new ProviderLocationIngestOptions { FutureSkewToleranceSeconds = 30 };

        var result = await CreateIngestService(context, options)
            .RecordAsync(_providerId, bookingId, Fix(DateTime.UtcNow.AddSeconds(5)));

        result.IsSuccess.Should().BeTrue();
        result.Value.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task RecordAsync_refuses_a_fix_older_than_the_configured_staleness_window()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);
        var options = new ProviderLocationIngestOptions { MaximumAgeMinutes = 5 };

        var result = await CreateIngestService(context, options)
            .RecordAsync(_providerId, bookingId, Fix(DateTime.UtcNow.AddMinutes(-6)));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderLocation.RecordedTooLongAgo");

        (await new ProviderLocationPingRepository(context).GetLatestForBookingAsync(bookingId)).Should().BeNull();
    }

    /// <summary>The staleness window is configuration, not a constant - a deployment that widens it must actually widen it.</summary>
    [Fact]
    public async Task RecordAsync_honours_a_widened_staleness_window()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);
        var options = new ProviderLocationIngestOptions { MaximumAgeMinutes = 30 };

        var result = await CreateIngestService(context, options)
            .RecordAsync(_providerId, bookingId, Fix(DateTime.UtcNow.AddMinutes(-6)));

        result.IsSuccess.Should().BeTrue();
        result.Value.Accepted.Should().BeTrue();
    }

    // ---------------------------------------------------------------- 202: throttle

    [Fact]
    public async Task RecordAsync_drops_a_second_fix_inside_the_throttle_interval()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);
        var service = CreateIngestService(context, new ProviderLocationIngestOptions { MinimumIntervalSeconds = 15 });
        var firstRecordedAtUtc = DateTime.UtcNow.AddSeconds(-10);

        var first = await service.RecordAsync(_providerId, bookingId, Fix(firstRecordedAtUtc));
        first.Value.Accepted.Should().BeTrue();

        var second = await service.RecordAsync(_providerId, bookingId, Fix(firstRecordedAtUtc.AddSeconds(14)));

        second.IsSuccess.Should().BeTrue("a throttled fix is not the client's error");
        second.Value.Accepted.Should().BeFalse();
        second.Value.PingId.Should().BeNull();
        second.Value.NextAcceptedAfterUtc.Should().BeCloseTo(firstRecordedAtUtc.AddSeconds(15), TimeSpan.FromMilliseconds(1));

        (await new ProviderLocationPingRepository(context).GetTrailForBookingAsync(bookingId))
            .Should().ContainSingle("the dropped fix must not reach the trail");
    }

    [Fact]
    public async Task RecordAsync_accepts_a_fix_once_the_throttle_interval_has_elapsed()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);
        var service = CreateIngestService(context, new ProviderLocationIngestOptions { MinimumIntervalSeconds = 15 });
        var firstRecordedAtUtc = DateTime.UtcNow.AddSeconds(-20);

        (await service.RecordAsync(_providerId, bookingId, Fix(firstRecordedAtUtc))).Value.Accepted.Should().BeTrue();

        var second = await service.RecordAsync(_providerId, bookingId, Fix(firstRecordedAtUtc.AddSeconds(15)));

        second.Value.Accepted.Should().BeTrue();
        (await new ProviderLocationPingRepository(context).GetTrailForBookingAsync(bookingId)).Should().HaveCount(2);
    }

    /// <summary>A back-dated fix cannot slip past the throttle by looking like a different moment.</summary>
    [Fact]
    public async Task RecordAsync_drops_a_back_dated_fix_inside_the_throttle_interval()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);
        var service = CreateIngestService(context, new ProviderLocationIngestOptions { MinimumIntervalSeconds = 15 });
        var firstRecordedAtUtc = DateTime.UtcNow.AddSeconds(-5);

        (await service.RecordAsync(_providerId, bookingId, Fix(firstRecordedAtUtc))).Value.Accepted.Should().BeTrue();

        var second = await service.RecordAsync(_providerId, bookingId, Fix(firstRecordedAtUtc.AddSeconds(-8)));

        second.Value.Accepted.Should().BeFalse();
        (await new ProviderLocationPingRepository(context).GetTrailForBookingAsync(bookingId)).Should().ContainSingle();
    }

    /// <summary>A chatty client gets one row per interval, not one row per request.</summary>
    [Fact]
    public async Task RecordAsync_holds_a_chatty_client_to_one_row_per_interval()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAcceptedBookingAsync(context);
        var service = CreateIngestService(context, new ProviderLocationIngestOptions { MinimumIntervalSeconds = 15 });
        var startedAtUtc = DateTime.UtcNow.AddSeconds(-60);

        for (var second = 0; second <= 60; second++)
        {
            await service.RecordAsync(_providerId, bookingId, Fix(startedAtUtc.AddSeconds(second)));
        }

        // 61 requests spanning 60 device-seconds at one accepted fix per 15.
        (await new ProviderLocationPingRepository(context).GetTrailForBookingAsync(bookingId))
            .Should().HaveCount(5);
    }

    /// <summary>The throttle is per booking - a provider on two jobs is not silenced on one by the other.</summary>
    [Fact]
    public async Task RecordAsync_throttles_per_booking_not_per_provider()
    {
        await using var context = _database.CreateContext();
        var service = CreateIngestService(context, new ProviderLocationIngestOptions { MinimumIntervalSeconds = 15 });
        var firstBookingId = await SeedAcceptedBookingAsync(context);
        var secondBookingId = await SeedAcceptedBookingAsync(context, slotDayOffset: 1);

        (await service.RecordAsync(_providerId, firstBookingId, Fix())).Value.Accepted.Should().BeTrue();
        (await service.RecordAsync(_providerId, secondBookingId, Fix())).Value.Accepted.Should().BeTrue();
    }

    public void Dispose() => _database.Dispose();
}
