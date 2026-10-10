using Nestly.Domain;

namespace Nestly.Application.Referral;

public interface IReferralRepository
{
    Task<Domain.Referral?> GetByIdAsync(Guid id);

    /// <summary>Task 163's self-referral/duplicate-referee guard, and task 164's qualifying-booking lookup key off the referee.</summary>
    Task<Domain.Referral?> GetByRefereeCustomerIdAsync(Guid refereeCustomerId);

    /// <summary>Task 166's post-reward cancellation signal: is this booking a Rewarded referral's qualifying booking?</summary>
    Task<Domain.Referral?> GetByQualifyingBookingIdAsync(Guid bookingId);

    Task<IReadOnlyList<Domain.Referral>> ListByReferrerCustomerIdAsync(Guid referrerCustomerId);

    /// <summary>
    /// Task 166/165's per-customer reward cap (REFERRAL.md "FRAUD / ABUSE
    /// PREVENTION") and task 174's milestone-threshold count - the same
    /// figure gates both, so both share this one method rather than each
    /// keeping their own copy of what "counts". Excludes a Rewarded
    /// referral whose qualifying booking has since been fully refunded: the
    /// order that made it a genuine referral never really happened, so it
    /// must not still occupy a cap slot or continue counting toward a
    /// milestone the referrer has not actually earned. A booking still only
    /// partially refunded (BookingStatus.RefundPending) is left counting -
    /// the order substantially still happened.
    /// </summary>
    Task<int> CountRewardedByReferrerAsync(Guid referrerCustomerId);

    /// <summary>Task 175's expiry sweep: Registered rows whose ExpiresAtUtc has passed.</summary>
    Task<IReadOnlyList<Domain.Referral>> ListExpiredAsync(DateTime asOfUtc);

    /// <summary>
    /// Task 170's admin list: optional status/fraud-flag filters, optional
    /// restriction to a set of customer ids (either side) for the admin's
    /// "search by customer" box - resolved to ids by the caller via
    /// <c>ICustomerRepository.SearchAsync</c> first, since Referral itself
    /// carries no denormalized customer name/mobile to search against
    /// directly.
    /// </summary>
    Task<(IReadOnlyList<Domain.Referral> Items, int TotalCount)> SearchAsync(
        ReferralStatus? status, bool? isFraudFlagged, IReadOnlyList<Guid>? customerIds, int page, int pageSize);

    /// <summary>Task 171's funnel report: referrals registered within the range (cohort basis - "of those who registered in this window, how many progressed").</summary>
    Task<IReadOnlyList<Domain.Referral>> ListRegisteredInRangeAsync(DateTime? fromUtc, DateTime? toUtc);

    /// <summary>Task 171's cost report: referrals rewarded within the range.</summary>
    Task<IReadOnlyList<Domain.Referral>> ListRewardedInRangeAsync(DateTime? fromUtc, DateTime? toUtc);

    Task AddAsync(Domain.Referral referral);

    Task UpdateAsync(Domain.Referral referral);

    /// <summary>
    /// Atomically transitions a referral from Registered to Qualified - a
    /// single conditional UPDATE re-checking "still Registered" in the same
    /// statement that flips it, mirroring <c>ICouponRepository.TryReserveRedemptionAsync</c>'s
    /// proven concurrency-safe shape. Two of a referee's bookings completing
    /// near-simultaneously both call <c>ReferralQualifyingBookingHandler.Handle</c>
    /// with their own independently-loaded (and therefore equally stale)
    /// in-memory <see cref="Domain.Referral"/> instance; without this, both
    /// could see Registered, both call <see cref="Domain.Referral.MarkQualified"/>,
    /// and both go on to disburse a real wallet credit/coupon reward twice
    /// for what is meant to be a one-time payout. Returns false (no state
    /// change) if the referral was no longer Registered when this ran - the
    /// caller must treat that as "lost the race, do nothing further" rather
    /// than an error.
    /// </summary>
    Task<bool> TryMarkQualifiedAsync(Guid referralId, Guid qualifyingBookingId);
}
