using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Reviews;
using Nestly.Application.Tracking;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Builds the live tracking snapshot for the owning customer (task 275).
///
/// The access decision here must give the same answer as
/// <c>BookingTrackingAuthorizer.CanCustomerTrackAsync</c>, which gates the
/// SignalR group for the same screen: own the booking AND be inside the
/// trackable window. Two surfaces feeding one screen that disagreed about
/// who may watch would mean the socket closing while the REST read kept
/// answering, or worse the other way round. Both derive trackability from
/// <see cref="BookingLifecycle.IsTrackable"/> rather than a local status set,
/// so a lifecycle change moves both at once.
/// </summary>
public sealed class BookingTrackingQueryService : IBookingTrackingQueryService
{
    private readonly IBookingRepository _bookingRepository;
    private readonly IBookingProviderAssignmentRepository _assignmentRepository;
    private readonly IProviderRepository _providerRepository;
    private readonly IProviderLocationPingRepository _locationPingRepository;
    private readonly IBookingTrackingRepository _trackingRepository;
    // Task 293: the assigned provider's own rating, for the summary this
    // screen renders. Read-only and only when a provider is actually on the
    // job - an unassigned booking's snapshot costs exactly what it did.
    private readonly IReviewRepository _reviewRepository;

    public BookingTrackingQueryService(
        IBookingRepository bookingRepository,
        IBookingProviderAssignmentRepository assignmentRepository,
        IProviderRepository providerRepository,
        IProviderLocationPingRepository locationPingRepository,
        IBookingTrackingRepository trackingRepository,
        IReviewRepository reviewRepository)
    {
        _bookingRepository = bookingRepository;
        _assignmentRepository = assignmentRepository;
        _providerRepository = providerRepository;
        _locationPingRepository = locationPingRepository;
        _trackingRepository = trackingRepository;
        _reviewRepository = reviewRepository;
    }

    public async Task<Result<BookingTrackingResponse>> GetForCustomerAsync(Guid customerId, Guid bookingId)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId);

        // One branch, one error, for "no such booking" and "not yours" - the
        // same shape IBookingService.GetDetailAsync already returns. Splitting
        // them, or answering 403 for the second, would turn this endpoint into
        // an oracle: a caller could walk booking ids and read off which ones
        // exist from the status code alone. 403 is the wrong answer to "may I
        // see this?" when admitting the question was well-formed is itself the
        // leak.
        if (booking is null || booking.CustomerId != customerId)
        {
            return Error.NotFound("Booking.NotFound", "The specified booking does not exist.");
        }

        // Also a 404, and deliberately not an empty 200. The tracking snapshot
        // is a sub-resource of the booking, and outside the fulfilment window
        // that sub-resource does not exist - there is no location to draw, no
        // ETA to show and no live assignment behind it. A 200 carrying a body
        // of nulls would make every consumer re-derive "is this actually
        // tracking?" from which fields came back null, and would put the
        // trackable-window rule in each client instead of here. It would also
        // diverge from the hub, which refuses the group outright rather than
        // joining it and sending nothing. This codebase has no empty-200
        // precedent for an absent resource; every absent resource is an
        // Error.NotFound through ToProblemResult, so this is one too.
        //
        // A distinct error code from the branch above, which is safe: a caller
        // only ever sees TrackingUnavailable for a booking they have already
        // been proven to own, so it discloses nothing about anyone else's.
        // Probing a stranger's booking id still returns exactly
        // Booking.NotFound. The split is what lets the app say "tracking has
        // ended for this booking" instead of "no such booking" to a customer
        // looking at their own completed job.
        if (!BookingLifecycle.IsTrackable(booking.Status))
        {
            return Error.NotFound(
                "Booking.TrackingUnavailable",
                "Live tracking is not available for this booking.");
        }

        // booking.Status alone no longer captures "is the job still live":
        // it stays InProgress through the admin-review window after the
        // provider completes (BookingManagementService.ApproveCompletionProofAsync),
        // so a finished job needs its own check. GetCurrentByBookingAsync,
        // not GetActiveByBookingAsync: this is a read path, so a booking that
        // (by data anomaly) never had a real assignment recorded must keep
        // reading as "trackable, provider unknown" exactly as before - only
        // an assignment that is itself Completed means this job is actually
        // over. BookingTrackingAuthorizer.CanCustomerTrackAsync applies the
        // same check, so the hub and this REST read keep giving the same
        // answer per this class's own doc comment.
        var assignment = await _assignmentRepository.GetCurrentByBookingAsync(booking.Id);
        if (assignment is { Status: BookingProviderAssignmentStatus.Completed })
        {
            return Error.NotFound(
                "Booking.TrackingUnavailable",
                "Live tracking is not available for this booking.");
        }

        return Result.Success(await BuildSnapshotAsync(booking, assignment));
    }

    public async Task<Result<BookingTrackingResponse>> GetForAdminAsync(Guid bookingId)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null)
        {
            return Error.NotFound("Booking.NotFound", "The specified booking does not exist.");
        }

        if (!BookingLifecycle.IsTrackable(booking.Status))
        {
            return Error.NotFound(
                "Booking.TrackingUnavailable",
                "Live tracking is not available for this booking.");
        }

        // See the matching comment in GetForCustomerAsync.
        var assignment = await _assignmentRepository.GetCurrentByBookingAsync(booking.Id);
        if (assignment is { Status: BookingProviderAssignmentStatus.Completed })
        {
            return Error.NotFound(
                "Booking.TrackingUnavailable",
                "Live tracking is not available for this booking.");
        }

        return Result.Success(await BuildSnapshotAsync(booking, assignment));
    }

    /// <summary>
    /// The part <see cref="GetForCustomerAsync"/> and <see cref="GetForAdminAsync"/>
    /// actually share - everything after the two callers' different access
    /// checks have already passed. Takes the booking and its already-resolved
    /// current assignment (possibly null - see the callers), not just their
    /// ids, so neither caller re-fetches them a second time.
    /// </summary>
    private async Task<BookingTrackingResponse> BuildSnapshotAsync(Booking booking, BookingProviderAssignment? assignment)
    {
        var provider = assignment is null
            ? null
            : await _providerRepository.GetByIdAsync(assignment.ProviderId);

        var providerRating = provider is null
            ? null
            : await _reviewRepository.GetProviderRatingAsync(provider.Id);

        var latestPing = await _locationPingRepository.GetLatestForBookingAsync(booking.Id);
        var tracking = await _trackingRepository.GetByBookingAsync(booking.Id);

        return new BookingTrackingResponse(
            booking.Id,
            booking.Status,
            BookingStatusMapper.LabelFor(booking.Status),
            provider is null ? null : TrackedProviderSummary.From(provider, providerRating),
            latestPing is null
                ? null
                : new TrackedLocation(latestPing.Latitude, latestPing.Longitude, latestPing.RecordedAtUtc),
            // HasEta rather than a null check on the row: a booking can have a
            // tracking row whose ETA was cleared (task 271's suppression on
            // leaving the trackable states, or a route lookup that never
            // succeeded), and a cleared ETA must read as "no estimate yet",
            // not as a stale one. EtaComputedAtUtc is non-null whenever
            // EtaSeconds is - ApplyEta sets both together and ClearEta clears
            // both together - so this is the only place the pair is unpacked.
            tracking is { HasEta: true }
                ? new TrackedEta(tracking.EtaSeconds!.Value, tracking.EtaComputedAtUtc!.Value)
                : null,
            new TrackedDestination(booking.AddressLatitudeSnapshot, booking.AddressLongitudeSnapshot));
    }
}
