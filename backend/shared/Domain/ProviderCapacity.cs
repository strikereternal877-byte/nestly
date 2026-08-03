using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>
/// A provider's dispatch capacity limits (PROVIDER.md "provider_capacity: Max
/// jobs per day/slot, if capacity-based dispatch is used"). One row per
/// provider. Both limits are optional/null = unlimited, mirroring
/// <c>SlotWindow.MaxBookingsPerSlot</c>'s null-is-unlimited convention.
/// Advisory only in v1 - OPEN DECISIONS #1 keeps assignment manual, so
/// nothing enforces these limits automatically yet; an admin can consult
/// them when hand-assigning a booking.
/// </summary>
public class ProviderCapacity : Entity<Guid>
{
    public Guid ProviderId { get; private set; }
    public int? MaxJobsPerDay { get; private set; }
    public int? MaxJobsPerSlot { get; private set; }

    protected ProviderCapacity() { }

    public ProviderCapacity(Guid id, Guid providerId, int? maxJobsPerDay = null, int? maxJobsPerSlot = null) : base(id)
    {
        ProviderId = providerId;
        SetLimits(maxJobsPerDay, maxJobsPerSlot);
    }

    public void SetLimits(int? maxJobsPerDay, int? maxJobsPerSlot)
    {
        if (maxJobsPerDay is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxJobsPerDay), "Capacity must be positive when set.");
        }

        if (maxJobsPerSlot is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxJobsPerSlot), "Capacity must be positive when set.");
        }

        MaxJobsPerDay = maxJobsPerDay;
        MaxJobsPerSlot = maxJobsPerSlot;
    }
}
