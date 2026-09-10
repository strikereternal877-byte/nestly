using Nestly.Application.Bookings;
using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.ProviderJobs;

/// <summary>
/// The provider-facing job lifecycle (task 149a, PROVIDER.md API surface
/// "Jobs" - list/detail, accept/reject/start/complete, completion proof).
/// Every method takes the caller's own provider id (resolved from the JWT by
/// the controller, never a route/body value - SRS 28.3 IDOR) and reads/
/// writes through the same <see cref="Nestly.Domain.BookingProviderAssignment"/>
/// bridge entity task 147 introduced, delegating accept/reject to
/// <see cref="Nestly.Application.ProviderManagement.IBookingProviderAssignmentService"/>
/// so the assignment lifecycle has exactly one owner.
/// </summary>
public interface IProviderJobService
{
    Task<Result<ProviderJobSearchResponse>> ListAsync(Guid providerId, ProviderJobStatus? status, DateOnly? date);

    Task<Result<ProviderJobDetailResponse>> GetDetailAsync(Guid providerId, Guid bookingId);

    Task<Result<ProviderJobDetailResponse>> AcceptAsync(Guid providerId, Guid bookingId);

    Task<Result<ProviderJobDetailResponse>> RejectAsync(Guid providerId, Guid bookingId, RejectJobRequest request);

    /// <summary>
    /// Row 38, docs/OPEN-FIXES-FEATURES.csv: extends this job's response
    /// deadline (see <see cref="Nestly.Application.ProviderManagement.IBookingProviderAssignmentService.ExtendResponseDeadlineAsync"/>)
    /// after an accept attempt failed for a reason that was not the
    /// provider's fault.
    /// </summary>
    Task<Result<ProviderJobDetailResponse>> ExtendResponseDeadlineAsync(Guid providerId, Guid bookingId);

    /// <summary>
    /// Marks an accepted job as started - the provider has begun the work
    /// itself, moving the booking to
    /// <see cref="Nestly.Domain.BookingStatus.InProgress"/>. Arrival is no
    /// longer conflated into this: task 264 split
    /// <see cref="Nestly.Domain.BookingStatus.ProviderEnRoute"/> and
    /// <see cref="Nestly.Domain.BookingStatus.ProviderArrived"/> out as their
    /// own states so a customer can be shown "On the way"/"Arrived" before
    /// work begins. Both are optional - Assigned -&gt; InProgress stays a legal
    /// transition, so a provider who never taps en-route/arrived can still
    /// start here.
    /// </summary>
    Task<Result<ProviderJobDetailResponse>> StartAsync(Guid providerId, Guid bookingId);

    /// <summary>
    /// Marks the provider as travelling to the customer's address - moves the
    /// booking to <see cref="Nestly.Domain.BookingStatus.ProviderEnRoute"/>
    /// (task 270). Optional: <see cref="StartAsync"/> remains reachable
    /// straight from <see cref="Nestly.Domain.BookingStatus.Assigned"/>, so a
    /// provider who never taps it is not blocked.
    ///
    /// Idempotent while already en route: a re-tap from a client retrying over
    /// a bad connection succeeds and returns the unchanged job rather than
    /// failing, since the caller's intent (the booking is en route) already
    /// holds. No second transition happens, so no second
    /// <c>ProviderEnRouteEvent</c> and no duplicate status-history row.
    /// </summary>
    Task<Result<ProviderJobDetailResponse>> MarkEnRouteAsync(Guid providerId, Guid bookingId);

    /// <summary>
    /// Marks the provider as having reached the customer's address - moves the
    /// booking to <see cref="Nestly.Domain.BookingStatus.ProviderArrived"/>
    /// (task 270). Reachable from
    /// <see cref="Nestly.Domain.BookingStatus.ProviderEnRoute"/> only, per
    /// <c>BookingLifecycle</c>'s table, and idempotent on a re-tap for the
    /// same reason as <see cref="MarkEnRouteAsync"/>.
    /// </summary>
    Task<Result<ProviderJobDetailResponse>> MarkArrivedAsync(Guid providerId, Guid bookingId);

    /// <summary>
    /// Marks an in-progress job as completed - moves the booking to
    /// <see cref="Nestly.Domain.BookingStatus.Completed"/>. The resulting
    /// <c>BookingStatusChangedEvent</c> is what triggers escrow release and
    /// the provider's earning-ledger credit (task 148) via
    /// <c>EscrowReleaseOnCompletionHandler</c> - this method does not credit
    /// the ledger directly.
    /// </summary>
    Task<Result<ProviderJobDetailResponse>> CompleteAsync(Guid providerId, Guid bookingId);

    Task<Result<ProviderJobDetailResponse>> UploadCompletionProofAsync(Guid providerId, Guid bookingId, UploadJobCompletionProofRequest request);

    /// <summary>
    /// Uploads one camera/gallery photo captured for job-completion
    /// verification and returns the ref to feed into
    /// <see cref="SubmitCompletionProofAsync"/>. Scoped by the same
    /// accepted-assignment ownership check as every other job action - only
    /// the provider currently on this booking's live assignment may attach
    /// evidence to it.
    /// </summary>
    Task<Result<UploadCompletionPhotoResponse>> UploadCompletionPhotoAsync(Guid providerId, Guid bookingId, Stream content, string fileNameHint, string contentType);

    /// <summary>
    /// Submits (or resubmits) the richer completion evidence - photos plus
    /// checklist - that <see cref="CompleteAsync"/> requires to exist before
    /// it will move the booking to Completed (tasks 195-197). Distinct from
    /// <see cref="UploadCompletionProofAsync"/>'s single legacy proof-ref
    /// field: that one is supplementary evidence uploadable any time
    /// (including after completion); this one is the task 196 gate itself
    /// and must be submitted before <see cref="CompleteAsync"/> succeeds.
    /// </summary>
    Task<Result<BookingCompletionProofResponse>> SubmitCompletionProofAsync(Guid providerId, Guid bookingId, SubmitCompletionProofRequest request);

    /// <summary>The completion proof this provider submitted for this job, if any (task 198's provider-side view).</summary>
    Task<Result<BookingCompletionProofResponse?>> GetCompletionProofAsync(Guid providerId, Guid bookingId);
}
