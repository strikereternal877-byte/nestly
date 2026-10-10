using Nestly.Domain;

namespace Nestly.Application.Subscriptions;

public interface ICustomerSubscriptionRepository
{
    Task<CustomerSubscription?> GetByIdAsync(Guid id);

    /// <summary>
    /// The customer's current "live" subscription, if any - Active or
    /// PaymentFailed (still retrying, task 178) only; Cancelled/Expired rows
    /// are history, not a current subscription. Used both to block
    /// double-subscribing (task 181) and to resolve "my subscription"
    /// (also task 181). A customer has at most one live row at a time -
    /// enforced by the unique partial index this maps to (see
    /// <c>CustomerSubscriptionConfiguration</c>).
    /// </summary>
    Task<CustomerSubscription?> GetCurrentByCustomerAsync(Guid customerId);

    /// <summary>
    /// The customer's Active-only subscription, for benefit application at
    /// booking time (task 179) - a PaymentFailed subscriber's benefits are
    /// paused (see <see cref="CustomerSubscriptionStatus"/>'s doc comment),
    /// so this deliberately excludes that status unlike
    /// <see cref="GetCurrentByCustomerAsync"/>.
    /// </summary>
    Task<CustomerSubscription?> GetActiveByCustomerAsync(Guid customerId);

    Task AddAsync(CustomerSubscription subscription);

    Task UpdateAsync(CustomerSubscription subscription);

    /// <summary>
    /// Atomically consumes one free-visit credit (task 179), mirroring
    /// <c>ICouponRepository.TryReserveRedemptionAsync</c>'s proven
    /// concurrency-safe shape: a single conditional UPDATE re-checking
    /// "still Active and still has a credit left" in the same statement that
    /// decrements the counter, so two bookings racing for the same
    /// subscriber's last free visit cannot both succeed. Returns false (no
    /// state change) if the credit was no longer available.
    /// </summary>
    Task<bool> TryConsumeFreeVisitAsync(Guid subscriptionId);

    /// <summary>
    /// Atomically gives back one free-visit credit <see cref="TryConsumeFreeVisitAsync"/>
    /// previously consumed - the booking it funded was cancelled before the
    /// service it was for ever happened, so the credit it would have spent
    /// never really got used. Floor-guarded at <see cref="CustomerSubscription.FreeVisitsIncludedSnapshot"/>
    /// so this can never push the counter above the plan's own per-period
    /// allotment, mirroring <c>ICouponRepository.ReleaseRedemptionAsync</c>'s
    /// equivalent floor at zero. A no-op if the subscription is already back
    /// at its full allotment (e.g. released twice for the same booking).
    /// </summary>
    Task ReleaseFreeVisitAsync(Guid subscriptionId);

    /// <summary>Subscriptions whose next billing attempt is due (task 178's recurring billing job) - Active or PaymentFailed (still retrying) with <see cref="CustomerSubscription.NextBillingDateUtc"/> at or before <paramref name="asOfUtc"/>.</summary>
    Task<IReadOnlyList<CustomerSubscription>> ListDueForBillingAsync(DateTime asOfUtc);

    /// <summary>
    /// Active subscriptions whose current period ends within the reminder
    /// window (task 183's "expiring soon") and haven't already been notified
    /// for this period - see <see cref="CustomerSubscription.ExpiringSoonNotifiedForPeriodEndUtc"/>.
    /// </summary>
    Task<IReadOnlyList<CustomerSubscription>> ListExpiringSoonAsync(DateTime asOfUtc, DateTime windowEndUtc);
}
