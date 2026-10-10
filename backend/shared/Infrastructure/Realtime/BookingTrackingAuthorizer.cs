using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Identity;
using Nestly.Domain;

namespace Nestly.Infrastructure.Realtime;

/// <summary>
/// The three-sided access rule for live order tracking (task 273): a booking's
/// tracking group may be watched by the owning customer, by the provider on
/// its live assignment, and by an admin holding the bookings-read permission -
/// nobody else, ever.
///
/// A class of its own rather than a private method on
/// <see cref="BookingTrackingHub"/>: this is the entire security boundary of
/// the feature, and it is worth being able to test it directly against a real
/// database, per actor kind, without standing up a SignalR connection.
///
/// Fails closed at every step. The actor kind defaults to
/// <see cref="RealtimeActorKind.Unknown"/> (denied); an unauthenticated or
/// unparseable principal is denied; a booking that does not exist is denied
/// exactly like one that exists but belongs to someone else, so a caller
/// cannot use the hub to probe for booking ids.
/// </summary>
public sealed class BookingTrackingAuthorizer
{
    /// <summary>
    /// The admin permission live tracking sits under. Read, not write:
    /// watching a booking's progress changes nothing. Built from the catalog
    /// rather than typed as a literal so a module rename cannot leave this
    /// checking a code that no role can ever hold - which, being a
    /// fail-closed check, would silently lock every admin out instead of
    /// failing loudly.
    /// </summary>
    public static readonly string AdminPermissionCode =
        AdminPermissionCatalog.BuildCode(AdminModules.Bookings, AdminPermissionAction.Read);

    private readonly RealtimeActorContext _actorContext;
    private readonly IBookingRepository _bookingRepository;
    private readonly IBookingProviderAssignmentRepository _assignmentRepository;

    public BookingTrackingAuthorizer(
        RealtimeActorContext actorContext,
        IBookingRepository bookingRepository,
        IBookingProviderAssignmentRepository assignmentRepository)
    {
        _actorContext = actorContext;
        _bookingRepository = bookingRepository;
        _assignmentRepository = assignmentRepository;
    }

    /// <summary>Whether <paramref name="user"/> may watch this booking's tracking group.</summary>
    public async Task<bool> CanTrackAsync(ClaimsPrincipal? user, Guid bookingId)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        return _actorContext.Kind switch
        {
            // Admin-api registers one authorization policy per permission code
            // (task 96b), but that registration is process-wide and this hub
            // type is shared with the other two APIs, which never register a
            // "bookings.read" policy - a class/method-level
            // [Authorize(Policy = ...)] would throw for every customer and
            // provider connection. Checking the claim directly (the same claim
            // PermissionAuthorizationHandler itself evaluates) gets the same
            // enforcement without that coupling. Same reasoning as ChatHub.
            RealtimeActorKind.Admin => user.HasClaim(AdminClaimTypes.Permission, AdminPermissionCode),
            RealtimeActorKind.Customer => await CanCustomerTrackAsync(user, bookingId),
            RealtimeActorKind.Provider => await CanProviderTrackAsync(user, bookingId),
            _ => false
        };
    }

    private async Task<bool> CanCustomerTrackAsync(ClaimsPrincipal user, Guid bookingId)
    {
        if (!TryGetSubjectId(user, out Guid customerId))
        {
            return false;
        }

        var booking = await _bookingRepository.GetByIdAsync(bookingId);

        // Ownership AND the fulfilment window: a customer keeps owning their
        // booking forever, but tracking is not a permanent right over it.
        // Once the job is completed, cancelled or refunded there is nothing
        // live to watch, so the group is closed to them again.
        if (booking is null || booking.CustomerId != customerId || !BookingLifecycle.IsTrackable(booking.Status))
        {
            return false;
        }

        // booking.Status alone is no longer enough: it stays InProgress
        // through the admin-review window after the provider completes (see
        // BookingManagementService.ApproveCompletionProofAsync). Current, not
        // Active: a booking that (by data anomaly) never had a real
        // assignment recorded must keep being trackable exactly as before -
        // only an assignment that is itself Completed means this job is
        // actually over.
        var assignment = await _assignmentRepository.GetCurrentByBookingAsync(bookingId);
        return assignment is not { Status: BookingProviderAssignmentStatus.Completed };
    }

    private async Task<bool> CanProviderTrackAsync(ClaimsPrincipal user, Guid bookingId)
    {
        if (!TryGetSubjectId(user, out Guid providerId))
        {
            return false;
        }

        // The LIVE assignment (status Assigned or Accepted - PROVIDER.md OPEN
        // DECISIONS #5), not the assignment history: a provider who rejected
        // this job, or was reassigned off it, keeps their historical row and
        // must not keep the live feed with it.
        var assignment = await _assignmentRepository.GetActiveByBookingAsync(bookingId);
        if (assignment is null || assignment.ProviderId != providerId)
        {
            return false;
        }

        // Held to the same fulfilment window as the customer. An outstanding
        // offer the provider has not accepted yet leaves the booking in
        // AwaitingFulfilment, which is not trackable - matching task 269's
        // rule that no location is ever collected before accept or after
        // completion. There is nothing to broadcast outside that window, so
        // granting the group would be access without a purpose.
        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        return booking is not null && BookingLifecycle.IsTrackable(booking.Status);
    }

    /// <summary>
    /// The actor's own id from the token's <c>sub</c> claim. TryParse rather
    /// than ChatHub's <c>Guid.Parse(...!)</c>: a token without a usable
    /// subject must be denied, not throw - a thrown exception here would
    /// surface to the client as a generic hub error and log noise, and the
    /// answer to "is this an actor we recognise?" is simply no.
    /// </summary>
    private static bool TryGetSubjectId(ClaimsPrincipal user, out Guid subjectId) =>
        Guid.TryParse(user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out subjectId);
}
