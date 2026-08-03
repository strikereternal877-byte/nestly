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

    /// <summary>Full assignment history for a booking, newest first (task 159 - lets the admin UI show why a booking needs reassignment).</summary>
    Task<Result<IReadOnlyList<BookingProviderAssignmentResponse>>> GetHistoryAsync(Guid bookingId);
}
