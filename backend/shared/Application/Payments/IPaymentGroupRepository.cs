using Nestly.Domain;

namespace Nestly.Application.Payments;

/// <summary>Persistence for <see cref="PaymentGroup"/> - one gateway order that settles several bookings (see its doc comment).</summary>
public interface IPaymentGroupRepository
{
    /// <summary>
    /// Persists a new group together with every member's transaction in a single
    /// SaveChanges, so a crash can never leave a gateway order with only some of
    /// its members recorded. <paramref name="newTransactions"/> are inserted;
    /// <paramref name="retriedTransactions"/> are existing transactions (tracked
    /// by this context) that just had a new attempt started.
    /// </summary>
    Task CreateAsync(
        PaymentGroup group,
        IReadOnlyCollection<PaymentTransaction> newTransactions,
        IReadOnlyCollection<PaymentTransaction> retriedTransactions);

    /// <summary>Used by the webhook handler: a callback's order id is the group's, not any member's.</summary>
    Task<PaymentGroup?> GetByGatewayOrderIdAsync(string gatewayOrderId);

    Task<PaymentGroup?> GetByIdAsync(Guid id);

    /// <summary>The most recently created group led by this booking, if any.</summary>
    Task<PaymentGroup?> GetLatestByLeadBookingIdAsync(Guid leadBookingId);

    /// <summary>Every member transaction of a group, with attempts loaded.</summary>
    Task<IReadOnlyList<PaymentTransaction>> ListMemberTransactionsAsync(Guid groupId);

    /// <summary>
    /// Atomically flips the group from Pending to <paramref name="newStatus"/> with a
    /// single conditional UPDATE (same race guard as
    /// <see cref="IPaymentTransactionRepository.TryMarkAttemptResolvedAsync"/>). False
    /// means another delivery already resolved it and nothing must be re-applied.
    /// </summary>
    Task<bool> TryMarkResolvedAsync(Guid groupId, PaymentGroupStatus newStatus);

    Task UpdateAsync(PaymentGroup group);
}
