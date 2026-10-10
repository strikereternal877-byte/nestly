using Nestly.Application.ProviderManagement;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// The eligibility gate every automatic path goes through (auto-assignment, the booking-creation and payment
/// provider-availability gates, a plan visit's regular-professional check), with one more rule in front: a provider
/// who is the regular professional of a recurring plan is spoken for at that plan's visit times
/// (<see cref="IProviderPlanReservationService"/>).
///
/// <para>
/// A decorator rather than another branch inside <see cref="ProviderAssignmentEligibilityService"/>: the existing gate
/// is unchanged, and the reservation check - database reads only - runs <i>before</i> it, so a provider who is
/// reserved never costs the billed route lookup the inner gate may make at its end. Manual assignment by an admin does
/// not pass through here (it uses the double-booking guard directly), so an admin can still override a reservation.
/// </para>
/// </summary>
public class PlanReservationAwareEligibilityService : IProviderAssignmentEligibilityService
{
    private readonly IProviderAssignmentEligibilityService _inner;
    private readonly IProviderPlanReservationService _reservations;

    public PlanReservationAwareEligibilityService(
        IProviderAssignmentEligibilityService inner,
        IProviderPlanReservationService reservations)
    {
        _inner = inner;
        _reservations = reservations;
    }

    public async Task<bool> IsEligibleAsync(Guid providerId, Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (await _reservations.IsReservedByAnotherPlanAsync(providerId, bookingId, cancellationToken))
        {
            return false;
        }

        return await _inner.IsEligibleAsync(providerId, bookingId, cancellationToken);
    }
}
