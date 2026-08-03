using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>
/// A recurring day-of-week working window for a provider (PROVIDER.md
/// "provider_availability: Day-of-week windows, blackout dates - feeds the
/// existing Slot Engine"). Mirrors <c>SlotWindow</c>'s shape (a time-of-day
/// range), but the day-of-week is inline here rather than expressed via a
/// separate rule child - a provider's own weekly schedule does not need
/// <c>SlotWindow</c>/<c>SlotWindowRule</c>'s two-table split, which exists
/// there to let one window be reused across several days. This entity is
/// what will eventually be intersected with the Slot Engine's city-level
/// <c>SlotWindow</c>s to compute a provider's actual bookable slots - not
/// built in this pass.
/// </summary>
public class ProviderAvailabilityWindow : Entity<Guid>
{
    public Guid ProviderId { get; private set; }
    public DayOfWeek DayOfWeek { get; private set; }
    public TimeSpan StartTime { get; private set; }
    public TimeSpan EndTime { get; private set; }
    public bool IsActive { get; private set; }

    protected ProviderAvailabilityWindow() { }

    public ProviderAvailabilityWindow(Guid id, Guid providerId, DayOfWeek dayOfWeek, TimeSpan startTime, TimeSpan endTime)
        : base(id)
    {
        ProviderId = providerId;
        DayOfWeek = dayOfWeek;
        SetTimes(startTime, endTime);
        IsActive = true;
    }

    public void SetTimes(TimeSpan startTime, TimeSpan endTime)
    {
        if (startTime >= endTime)
        {
            throw new ArgumentException("The start time must be before the end time.");
        }

        StartTime = startTime;
        EndTime = endTime;
    }

    public void Activate() => IsActive = true;
    public void Deactivate() => IsActive = false;
}
