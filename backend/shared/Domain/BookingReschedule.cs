using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>
/// Immutable history record of a single booking reschedule (SRS 11.15.2,
/// 32.3, tasks 82a-d, 83). A booking may accumulate several of these up to
/// <c>ReschedulePolicyOptions.MaxReschedulesPerBooking</c> - unlike
/// <see cref="BookingCancellation"/>, there is no uniqueness constraint on
/// <see cref="BookingId"/>.
/// </summary>
public class BookingReschedule : Entity<Guid>
{
    public Guid BookingId { get; private set; }
    public RescheduleActor Actor { get; private set; }
    public string? Reason { get; private set; }

    public Guid FromSlotWindowId { get; private set; }
    public DateOnly FromSlotDate { get; private set; }
    public TimeSpan FromSlotStartTime { get; private set; }
    public TimeSpan FromSlotEndTime { get; private set; }

    public Guid ToSlotWindowId { get; private set; }
    public DateOnly ToSlotDate { get; private set; }
    public TimeSpan ToSlotStartTime { get; private set; }
    public TimeSpan ToSlotEndTime { get; private set; }

    public bool IsLate { get; private set; }

    /// <summary>The late fee this reschedule carried under the policy of the day - what it <em>would</em> cost, whoever ends up paying it.</summary>
    public decimal FeeAmount { get; private set; }

    /// <summary>
    /// The part of <see cref="FeeAmount"/> actually taken from the customer (their wallet), at the moment of the
    /// reschedule. Zero when nothing was taken: an admin's reschedule, a reschedule that was not late, and every one made
    /// before late fees were collected. A later cancellation of the booking counts this amount against its own fee, so it
    /// must only ever hold money that really moved.
    /// </summary>
    public decimal FeeCollectedAmount { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    protected BookingReschedule() { }

    public BookingReschedule(
        Guid id,
        Guid bookingId,
        RescheduleActor actor,
        string? reason,
        Guid fromSlotWindowId,
        DateOnly fromSlotDate,
        TimeSpan fromSlotStartTime,
        TimeSpan fromSlotEndTime,
        Guid toSlotWindowId,
        DateOnly toSlotDate,
        TimeSpan toSlotStartTime,
        TimeSpan toSlotEndTime,
        bool isLate,
        decimal feeAmount,
        decimal feeCollectedAmount = 0m)
        : base(id)
    {
        if (feeAmount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(feeAmount), "Reschedule fee cannot be negative.");
        }

        if (feeCollectedAmount < 0 || feeCollectedAmount > feeAmount)
        {
            throw new ArgumentOutOfRangeException(nameof(feeCollectedAmount), "The collected fee must be between zero and the fee.");
        }

        BookingId = bookingId;
        Actor = actor;
        Reason = reason;
        FromSlotWindowId = fromSlotWindowId;
        FromSlotDate = fromSlotDate;
        FromSlotStartTime = fromSlotStartTime;
        FromSlotEndTime = fromSlotEndTime;
        ToSlotWindowId = toSlotWindowId;
        ToSlotDate = toSlotDate;
        ToSlotStartTime = toSlotStartTime;
        ToSlotEndTime = toSlotEndTime;
        IsLate = isLate;
        FeeAmount = feeAmount;
        FeeCollectedAmount = feeCollectedAmount;
        CreatedAtUtc = DateTime.UtcNow;
    }
}
