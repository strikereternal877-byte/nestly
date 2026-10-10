namespace Nestly.Domain;

/// <summary>
/// Lifecycle of a <see cref="PaymentGroup"/>: one gateway order that settles
/// several bookings at once. Stored as a string (max length 20), so a new
/// member is safe anywhere; kept append-only anyway to match the other
/// payment enums.
/// </summary>
public enum PaymentGroupStatus
{
    /// <summary>The gateway order exists and no outcome has been applied yet.</summary>
    Pending,

    /// <summary>The gateway reported success; every member booking was confirmed in the same database transaction.</summary>
    Success,

    /// <summary>The gateway reported failure; every member booking moved to PaymentFailed and can be retried as a group.</summary>
    Failed
}
