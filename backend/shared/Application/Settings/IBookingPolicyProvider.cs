namespace Nestly.Application.Settings;

/// <summary>
/// The cancellation and reschedule policy the engines enforce <em>right now</em>. Every engine that applies one of these
/// rules (<c>CancellationService</c>, <c>RescheduleService</c>, the recurring-plan card that quotes them) asks this rather
/// than reading configuration itself, so an admin's change in Settings takes effect for the next booking action with no
/// restart or deploy.
///
/// <para>
/// Where a value comes from: once an admin has saved a group in Settings, that saved value is the policy. Until then the
/// group is still the row seeded with the system, which says nothing about what the deployment is configured to enforce
/// (<c>CancellationPolicy</c> / <c>ReschedulePolicy</c> in configuration), so configuration stays in force. That makes the
/// switch safe to ship: no live booking is treated differently until an admin deliberately changes a rule.
/// </para>
///
/// <para>Read on every call, not cached: a single-row lookup on a unique key, and a stale cache across API instances would
/// let two customers on the same booking see different rules for a while after an admin's change.</para>
/// </summary>
public interface IBookingPolicyProvider
{
    Task<CancellationSettings> GetCancellationAsync(CancellationToken cancellationToken = default);

    Task<RescheduleSettings> GetRescheduleAsync(CancellationToken cancellationToken = default);
}
