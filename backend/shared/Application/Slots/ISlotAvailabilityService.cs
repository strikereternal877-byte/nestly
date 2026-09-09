using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.Slots;

/// <summary>Slot availability calculation (tasks 45a-d, SRS 12.10, 24.4).</summary>
public interface ISlotAvailabilityService
{
    /// <summary>Available slots for a service, at an address (locality), on a date.</summary>
    Task<Result<SlotAvailabilityResponse>> GetAvailableSlotsAsync(Guid serviceId, Guid localityId, DateOnly date);

    /// <summary>
    /// The same answer as <see cref="GetAvailableSlotsAsync"/> for every date
    /// from <paramref name="from"/> to <paramref name="to"/> inclusive, in
    /// date order - one call for the whole date strip instead of one per day.
    /// </summary>
    Task<Result<SlotRangeResponse>> GetAvailableSlotsRangeAsync(Guid serviceId, Guid localityId, DateOnly from, DateOnly to);

    /// <summary>
    /// Re-checks a previously offered slot right before booking confirmation
    /// (task 45d) - cutoff, blackout, or serviceability may have changed
    /// since the customer picked it.
    /// </summary>
    Task<Result<SlotRevalidationResponse>> RevalidateSlotAsync(Guid serviceId, Guid localityId, Guid slotWindowId, DateOnly date);

    /// <summary>
    /// Atomically reserves one seat against the window's per-day capacity
    /// (SlotWindow.MaxBookingsPerSlot, SRS 12.10.1, task 135c). Must be
    /// called once, immediately before a booking is persisted for that
    /// window+date - never treat a prior <see cref="RevalidateSlotAsync"/>
    /// success as proof capacity is still available, since another booking
    /// may have taken the last seat in between. A window with no configured
    /// capacity (null) always succeeds without reserving anything.
    /// </summary>
    Task<Result> ReserveSlotAsync(Guid slotWindowId, DateOnly date);

    /// <summary>
    /// Hands back the seat a booking was holding on this window+date - the
    /// other half of <see cref="ReserveSlotAsync"/>. Must be called whenever a
    /// booking stops occupying a slot: cancelled by either actor, or moved off
    /// it by a reschedule. A no-op for windows with no configured capacity,
    /// and safe to call more than once.
    /// </summary>
    Task ReleaseSlotAsync(Guid slotWindowId, DateOnly date);
}
