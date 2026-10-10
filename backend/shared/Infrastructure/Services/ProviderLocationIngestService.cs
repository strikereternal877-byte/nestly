using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.ProviderJobs;
using Nestly.Application.Tracking;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.Services;

/// <inheritdoc cref="IProviderLocationIngestService"/>
public class ProviderLocationIngestService : IProviderLocationIngestService
{
    private const decimal MinimumLatitude = -90m;
    private const decimal MaximumLatitude = 90m;
    private const decimal MinimumLongitude = -180m;
    private const decimal MaximumLongitude = 180m;

    private readonly IBookingRepository _bookingRepository;
    private readonly IBookingProviderAssignmentRepository _assignmentRepository;
    private readonly IProviderRepository _providerRepository;
    private readonly IProviderLocationPingRepository _pingRepository;
    private readonly IBookingEtaService _etaService;
    private readonly ProviderLocationIngestOptions _options;

    public ProviderLocationIngestService(
        IBookingRepository bookingRepository,
        IBookingProviderAssignmentRepository assignmentRepository,
        IProviderRepository providerRepository,
        IProviderLocationPingRepository pingRepository,
        IBookingEtaService etaService,
        IOptions<ProviderLocationIngestOptions> options)
    {
        _bookingRepository = bookingRepository;
        _assignmentRepository = assignmentRepository;
        _providerRepository = providerRepository;
        _pingRepository = pingRepository;
        _etaService = etaService;
        _options = options.Value;
    }

    public async Task<Result<RecordProviderLocationResponse>> RecordAsync(
        Guid providerId,
        Guid bookingId,
        RecordProviderLocationRequest request)
    {
        var receivedAtUtc = DateTime.UtcNow;
        var recordedAtUtc = ToUtc(request.RecordedAtUtc);

        var invalid = Validate(request, recordedAtUtc, receivedAtUtc);
        if (invalid is not null)
        {
            return invalid;
        }

        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null)
        {
            return Error.NotFound("ProviderLocation.NotFound", "No job was found for this booking.");
        }

        // PRIVACY BOUNDARY, not merely a validation rule. Everything from here
        // to the trackable-state check decides whether the platform is allowed
        // to know where a person is at all, so every branch fails closed: a
        // missing assignment, a stale one, a booking that has already finished
        // and an outright wrong caller are all refused, and nothing is written
        // until all of them have passed. Widening any of these conditions
        // widens surveillance of the provider - treat a change here as a
        // privacy decision, not a bug fix.
        // GetActiveByBookingAsync deliberately excludes a Completed assignment
        // (it means "still outstanding", the set CancellationService/
        // RescheduleService withdraw from) - reading it here would make a just-
        // finished job report ProviderLocation.NotAssigned instead of the
        // booking-trackability check below refusing it as NotTrackable, which is
        // the one source of truth for "is this job still trackable" (see the
        // BookingLifecycle.IsTrackable comment). Assigned/Accepted/Completed
        // together are this endpoint's own "the assignment I should check
        // belongs to this provider" set - a Rejected/Reassigned/Withdrawn row
        // is superseded and never authorises anything.
        var assignment = (await _assignmentRepository.ListByBookingAsync(bookingId))
            .Where(a => a.Status is BookingProviderAssignmentStatus.Assigned
                or BookingProviderAssignmentStatus.Accepted
                or BookingProviderAssignmentStatus.Completed)
            .MaxBy(a => a.AssignedAt);
        if (assignment is null || assignment.ProviderId != providerId)
        {
            return Error.Forbidden(
                "ProviderLocation.NotAssigned",
                "Only the provider on this booking's current assignment may report location for it.");
        }

        // The booking sits in Assigned both before and after the provider
        // responds, so the trackable-state list alone would let the platform
        // start tracking someone who has merely been offered the job and may
        // yet decline it. Consent to be located begins at accept.
        if (assignment.Status is not (BookingProviderAssignmentStatus.Accepted or BookingProviderAssignmentStatus.Completed))
        {
            return Error.Conflict(
                "ProviderLocation.NotAccepted",
                "Location may not be reported for a job that has not been accepted yet.");
        }

        // Completion no longer implies BookingLifecycle.IsTrackable(booking.Status)
        // is false: the booking now stays InProgress until admin approves the
        // completion proof (see BookingManagementService.ApproveCompletionProofAsync),
        // so a provider who finished and moved on could otherwise keep reporting
        // location against this booking for the entire admin-review window. The
        // assignment - not the booking - is this endpoint's actual source of
        // truth for "is the provider still on this job", so it is checked
        // directly rather than only through BookingLifecycle.IsTrackable.
        if (assignment.Status == BookingProviderAssignmentStatus.Completed)
        {
            return Error.Conflict(
                "ProviderLocation.NotTrackable",
                "This job is not in a state where location may be reported.");
        }

        // BookingLifecycle.IsTrackable, not a local copy of the same four
        // statuses: task 273 put that set in the domain precisely so the
        // tracking hub, the read model and this endpoint cannot drift into
        // three different answers about when tracking is allowed.
        if (!BookingLifecycle.IsTrackable(booking.Status))
        {
            return Error.Conflict(
                "ProviderLocation.NotTrackable",
                "This job is not in a state where location may be reported.");
        }

        var provider = await _providerRepository.GetByIdAsync(providerId);
        if (provider is null)
        {
            return Error.NotFound("ProviderLocation.ProviderNotFound", "The provider on this assignment no longer exists.");
        }

        // THROTTLE. The state is the ping table itself - the newest stored
        // fix for this booking - not an in-process counter, so every API
        // instance reads the same answer and the limit does not multiply by
        // replica count the way an IMemoryCache-backed one would. Two caveats,
        // stated rather than implied:
        //
        //  * It is a read-then-write, not an atomic reservation. Genuinely
        //    simultaneous requests can both see the same "newest" row and both
        //    be accepted. That bounds the overshoot by concurrency, not by how
        //    chatty one client is, which is the abuse this guards against.
        //  * It compares device clocks, because that is the column
        //    GetLatestForBookingAsync orders by. A client cannot cheat by
        //    back-dating (an older fix loses to the stored one) nor by
        //    forward-dating (Validate refuses the future), so the only way to
        //    get a second row accepted is for real time to pass - except for
        //    one bounded burst through the MaximumAgeMinutes window, which
        //    ProviderLocationIngestOptions documents.
        var minimumInterval = TimeSpan.FromSeconds(_options.MinimumIntervalSeconds);
        var latest = await _pingRepository.GetLatestForBookingAsync(bookingId);
        if (latest is not null && recordedAtUtc - latest.RecordedAtUtc < minimumInterval)
        {
            return new RecordProviderLocationResponse(
                Accepted: false,
                PingId: null,
                NextAcceptedAfterUtc: latest.RecordedAtUtc + minimumInterval);
        }

        // Constructing the ping raises ProviderLocationUpdatedEvent (task 272);
        // appending it IS the event, so nothing here raises it a second time.
        var ping = new ProviderLocationPing(
            Guid.NewGuid(),
            providerId,
            bookingId,
            request.Latitude,
            request.Longitude,
            request.AccuracyMetres,
            recordedAtUtc,
            receivedAtUtc);

        // The trail is written first, on purpose. These are two saves, so a
        // failure can land between them; losing the denormalized last-known
        // coordinate leaves the append-only trail (and the event derived from
        // it) intact and self-healing on the next fix, whereas the reverse
        // order would advance the provider's position with no ping and no
        // event to explain it.
        await _pingRepository.AddAsync(ping);

        // observedAtUtc is the device's clock, never the arrival time - a fix
        // that spent four minutes in an offline queue must not read as fresh.
        provider.UpdateLocation(request.Latitude, request.Longitude, recordedAtUtc);
        await _providerRepository.UpdateAsync(provider);

        // Task 271. Called on every accepted ping and on no rejected one, but
        // the ETA does NOT recompute on every accepted ping: the service
        // applies its own time/distance throttle so that a route lookup - which
        // is billed - is not bought at whatever rate this endpoint happens to
        // accept fixes. The two throttles are independent on purpose; tightening
        // MinimumIntervalSeconds here does not reduce the maps bill and
        // loosening it does not raise it.
        //
        // Last, and unguarded, because it cannot fail this request: the ping is
        // already durable, the response below is already earned, and
        // IBookingEtaService swallows and logs its own faults precisely so a
        // routing hiccup cannot turn an accepted fix into an error the
        // provider's app will retry.
        await _etaService.RefreshAsync(bookingId);

        return new RecordProviderLocationResponse(
            Accepted: true,
            PingId: ping.Id,
            NextAcceptedAfterUtc: recordedAtUtc + minimumInterval);
    }

    /// <summary>
    /// Every bound in one place, in the service rather than split between a
    /// FluentValidation validator and here: the coordinate bounds and the
    /// clock-skew window are one acceptance policy, and the skew window is
    /// driven by <see cref="ProviderLocationIngestOptions"/>, which the
    /// Application assembly (where validators live) cannot see. The domain
    /// entity re-checks the coordinate bounds as a backstop; this exists so a
    /// bad request gets a 400 instead of an unhandled exception.
    /// </summary>
    private Error? Validate(RecordProviderLocationRequest request, DateTime recordedAtUtc, DateTime receivedAtUtc)
    {
        if (request.Latitude is < MinimumLatitude or > MaximumLatitude)
        {
            return Error.Validation("ProviderLocation.InvalidLatitude", "Latitude must be between -90 and 90.");
        }

        if (request.Longitude is < MinimumLongitude or > MaximumLongitude)
        {
            return Error.Validation("ProviderLocation.InvalidLongitude", "Longitude must be between -180 and 180.");
        }

        if (request.AccuracyMetres is < 0)
        {
            return Error.Validation("ProviderLocation.InvalidAccuracy", "Accuracy cannot be negative.");
        }

        if (recordedAtUtc > receivedAtUtc.AddSeconds(_options.FutureSkewToleranceSeconds))
        {
            return Error.Validation("ProviderLocation.RecordedInFuture", "A location fix cannot be recorded in the future.");
        }

        if (recordedAtUtc < receivedAtUtc.AddMinutes(-_options.MaximumAgeMinutes))
        {
            return Error.Validation("ProviderLocation.RecordedTooLongAgo", "This location fix is too old to be reported.");
        }

        return null;
    }

    /// <summary>
    /// Normalizes the device timestamp to UTC. A client that sends an offset
    /// ("+05:30") binds as <see cref="DateTimeKind.Local"/> and would
    /// otherwise be compared against a UTC server clock and read as hours in
    /// the future; a client that sends no offset is taken at its word, since
    /// the field is documented as UTC.
    /// </summary>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
