using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.ProviderManagement;

/// <summary>
/// Admin-driven booking-to-provider assignment and rejection handling
/// (PROVIDER.md "Assignment Bridge", tasks 147 and 159). Lives apart from
/// <c>IBookingManagementService</c> so that service's existing,
/// already-tested cancel/reschedule/refund flows are untouched - this
/// service only ever touches <see cref="Nestly.Domain.Booking.AssignedProviderId"/>
/// (the one field PROVIDER.md's SCOPE BOUNDARY allows) and the
/// <see cref="Nestly.Domain.BookingProviderAssignment"/> bridge table.
/// </summary>
public interface IBookingProviderAssignmentService
{
    /// <summary>
    /// Assigns (or re-assigns) a provider to a booking (task 147). Valid only
    /// while the booking is <see cref="Nestly.Domain.BookingStatus.AwaitingFulfilment"/>
    /// or already <see cref="Nestly.Domain.BookingStatus.Assigned"/> (a
    /// straight reassignment, which supersedes the current outstanding row
    /// via <see cref="Nestly.Domain.BookingProviderAssignment.MarkReassigned"/>
    /// rather than leaving two live rows - PROVIDER.md OPEN DECISIONS #5).
    /// </summary>
    Task<Result<BookingProviderAssignmentResponse>> AssignAsync(Guid bookingId, Guid adminUserId, AssignProviderRequest request);

    /// <summary>
    /// Task 246: the automatic-assignment engine's counterpart to
    /// <see cref="AssignAsync"/> - same validation and supersede behaviour,
    /// recorded as <see cref="Nestly.Domain.BookingAssignedByType.System"/>
    /// with no acting admin, no response deadline.
    /// </summary>
    Task<Result<BookingProviderAssignmentResponse>> AssignBySystemAsync(Guid bookingId, Guid providerId);

    /// <summary>
    /// Rejects the booking's current outstanding assignment (task 159):
    /// clears <see cref="Nestly.Domain.Booking.AssignedProviderId"/> and moves
    /// the booking back to <see cref="Nestly.Domain.BookingStatus.AwaitingFulfilment"/>
    /// so it re-enters the assignable pool - no automatic reassignment
    /// (PROVIDER.md OPEN DECISIONS #1), an admin must call
    /// <see cref="AssignAsync"/> again.
    /// </summary>
    Task<Result<BookingProviderAssignmentResponse>> RejectAsync(Guid bookingId, RejectAssignmentRequest request);

    /// <summary>
    /// The provider's own self-service acceptance of an outstanding
    /// assignment (task 149a, PROVIDER.md API surface "accept job"). Verifies
    /// <paramref name="providerId"/> actually owns the booking's currently
    /// outstanding assignment before calling <see cref="Nestly.Domain.BookingProviderAssignment.Accept"/>
    /// (SRS 28.3 IDOR) - the caller's provider id must come from the JWT, not
    /// a route/body value.
    /// </summary>
    Task<Result<BookingProviderAssignmentResponse>> AcceptAsync(Guid bookingId, Guid providerId);

    /// <summary>
    /// The provider's own self-service rejection of an outstanding assignment
    /// (task 149a/159, PROVIDER.md API surface "reject job") - the
    /// provider-authenticated counterpart to <see cref="RejectAsync"/>, which
    /// verifies <paramref name="providerId"/> actually owns the outstanding
    /// assignment (SRS 28.3 IDOR) before applying the same reassignment-pool
    /// handling.
    /// </summary>
    Task<Result<BookingProviderAssignmentResponse>> RejectByProviderAsync(Guid bookingId, Guid providerId, RejectAssignmentRequest request);

    /// <summary>
    /// Row 38, docs/OPEN-FIXES-FEATURES.csv: pushes an outstanding
    /// assignment's response deadline out from now by
    /// <c>AutoAssignmentOptions.ResponseWindowMinutes</c> - a provider whose
    /// own <see cref="AcceptAsync"/> call failed for a reason that was not
    /// their fault (a transient 5xx, not the 401-session-expiry case the
    /// client already retries transparently) calls this so the response
    /// window does not keep counting down while they retry, rather than
    /// silently losing the job to the expiry sweep. Verifies
    /// <paramref name="providerId"/> actually owns the outstanding
    /// assignment first (SRS 28.3 IDOR), same as <see cref="AcceptAsync"/>.
    /// </summary>
    Task<Result<BookingProviderAssignmentResponse>> ExtendResponseDeadlineAsync(Guid bookingId, Guid providerId);

    /// <summary>
    /// The assignment-response-expiry sweep's action on one unanswered,
    /// past-deadline assignment: marks it <see cref="Nestly.Domain.BookingProviderAssignmentStatus.Expired"/>
    /// and returns the booking to <see cref="Nestly.Domain.BookingStatus.AwaitingFulfilment"/>
    /// exactly like <see cref="RejectAsync"/> does for an explicit decline -
    /// the same reassignment pool, just triggered by silence instead of a
    /// choice. A no-op (not an error) if the assignment already moved on by
    /// the time the sweep reaches it (the provider responded in the interim).
    /// </summary>
    Task<Result<BookingProviderAssignmentResponse>> ExpireAsync(Guid assignmentId);

    /// <summary>Full assignment history for a booking, newest first (task 159 - lets the admin UI show why a booking needs reassignment).</summary>
    Task<Result<IReadOnlyList<BookingProviderAssignmentResponse>>> GetHistoryAsync(Guid bookingId);

    /// <summary>
    /// Candidate providers for manually assigning this booking, matched by
    /// declared service area (pincode, falling back to city-wide coverage)
    /// and skill mapping (service, falling back to category-wide coverage),
    /// ranked most-specific-and-least-loaded first. Read-only: still requires
    /// an explicit <see cref="AssignAsync"/> call to actually assign anyone -
    /// PROVIDER.md OPEN DECISIONS #1 keeps assignment manual/admin-driven, so
    /// this exists to inform that decision, not make it.
    /// </summary>
    Task<Result<IReadOnlyList<EligibleProviderResponse>>> GetEligibleProvidersAsync(Guid bookingId);
}
