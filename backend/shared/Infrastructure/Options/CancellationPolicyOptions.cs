using System.ComponentModel.DataAnnotations;

namespace Nestly.Infrastructure.Options;

/// <summary>
/// Strongly typed binding of the "CancellationPolicy" configuration section
/// (SRS 11.14.1, task 80a-b). Since admins can edit cancellation policy in
/// Settings, this is the policy only until an admin saves that group - see
/// <c>IBookingPolicyProvider</c>, which every engine asks instead of reading this
/// directly. Commission rates (<see cref="CommissionOptions"/>) are still
/// configuration-only.
/// </summary>
public class CancellationPolicyOptions
{
    public const string SectionName = "CancellationPolicy";

    /// <summary>
    /// Cancelling at least this many hours before the booked slot owes no
    /// cancellation fee - a full refund of whatever was paid.
    /// </summary>
    [Range(0, 720)]
    public decimal FreeCancellationWindowHours { get; set; } = 4m;

    /// <summary>
    /// Percentage of the payable amount retained as a cancellation fee when
    /// cancelling inside the free window (0-100).
    /// </summary>
    [Range(0, 100)]
    public decimal LateCancellationFeePercentage { get; set; } = 20m;
}
