using System.ComponentModel.DataAnnotations;

namespace Nestly.Infrastructure.Options;

/// <summary>
/// Strongly typed binding of the "ReschedulePolicy" configuration section
/// (SRS 11.15.1, task 82a-d). Like <see cref="CancellationPolicyOptions"/>, this is the
/// policy only until an admin saves the reschedule group in Settings - see
/// <c>IBookingPolicyProvider</c>, which every engine asks instead of reading this directly.
/// </summary>
public class ReschedulePolicyOptions
{
    public const string SectionName = "ReschedulePolicy";

    /// <summary>Rescheduling with less than this many hours to the current slot is blocked entirely (SRS 11.15.1 "window has not expired").</summary>
    [Range(0, 720)]
    public decimal MinHoursBeforeSlot { get; set; } = 2m;

    /// <summary>How many times a single booking may be rescheduled before further reschedules are blocked (SRS 11.15.1 "count limit").</summary>
    [Range(0, 50)]
    public int MaxReschedulesPerBooking { get; set; } = 2;

    /// <summary>Rescheduling with less than this many hours to go (but still above <see cref="MinHoursBeforeSlot"/>) incurs a fee.</summary>
    [Range(0, 720)]
    public decimal LateFeeThresholdHours { get; set; } = 6m;

    /// <summary>Percentage of the booking's payable amount reported as a late-reschedule fee (0-100).</summary>
    [Range(0, 100)]
    public decimal LateRescheduleFeePercentage { get; set; } = 10m;

    /// <summary>
    /// Whether a customer's late reschedule actually takes its fee from their wallet. <b>Off by default</b>: the fee is then
    /// only recorded on the booking (what happened before collection existed) and nothing is charged or refused. Collection
    /// refuses a late reschedule when the wallet cannot cover the fee, so it should be switched on once customers can top up
    /// their wallet (<c>WalletTopUp:Enabled</c>). Admins switch it in Settings -&gt; Reschedule, which wins over this once saved.
    /// </summary>
    public bool CollectLateFeeFromWallet { get; set; }
}
