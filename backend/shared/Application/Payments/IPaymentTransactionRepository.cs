using Nestly.Domain;

namespace Nestly.Application.Payments;

/// <summary>
/// The commission fields recorded on one booking's payment transaction (task
/// 157), projected without its <see cref="PaymentTransaction.Attempts"/> -
/// used by <see cref="IPaymentTransactionRepository.ListCommissionSnapshotsByBookingIdsAsync"/>
/// so a page of provider jobs can compute each row's payout without paying
/// for a fully-loaded transaction per booking (task 255's batching pattern;
/// see <c>IBookingRepository.ListSummariesByIdsAsync</c>).
/// <see cref="CommissionRatePercentage"/>/<see cref="CommissionAmount"/> are
/// null when the transaction has not (yet) had <see cref="PaymentTransaction.RecordCommission"/>
/// applied - only possible for a booking a provider should never be able to
/// see yet, since jobs are only assignable once a booking is Confirmed,
/// which is exactly when commission is recorded.
/// </summary>
public sealed record PaymentCommissionSnapshot(Guid BookingId, decimal Amount, decimal? CommissionRatePercentage, decimal? CommissionAmount);

public interface IPaymentTransactionRepository
{
    Task AddAsync(PaymentTransaction transaction);

    /// <summary>
    /// Inserts a brand-new transaction, but returns <c>false</c> instead of
    /// throwing if another concurrent request already created one for the
    /// same booking (task 135b - BookingId's unique index is the guard).
    /// Callers must not retry with the same instance; on a <c>false</c>
    /// return, re-read via <see cref="GetByBookingIdAsync"/> to get the
    /// transaction that actually won.
    /// </summary>
    Task<bool> TryAddAsync(PaymentTransaction transaction);

    Task UpdateAsync(PaymentTransaction transaction);

    /// <summary>
    /// Atomically flips one attempt from <see cref="PaymentAttemptStatus.Created"/>
    /// to <paramref name="newStatus"/> via a single conditional UPDATE - never a
    /// read-then-write (NESTLY-006). Mirrors
    /// <see cref="Nestly.Infrastructure.Persistence.Repositories.SlotCapacityRepository.TryReserveAsync"/>:
    /// two concurrent/duplicate gateway webhook deliveries for the same order can
    /// both read <c>Created</c>, but only one of them can win this UPDATE (its
    /// WHERE clause re-checks the still-committed status). Returns <c>false</c>
    /// when the attempt was no longer <c>Created</c> - the caller must treat that
    /// as an already-processed duplicate and must not re-apply any side effect
    /// (escrow hold, booking transition) a second time.
    /// </summary>
    Task<bool> TryMarkAttemptResolvedAsync(Guid attemptId, PaymentAttemptStatus newStatus);

    /// <summary>Loaded with its attempts - a transaction is never useful partially loaded.</summary>
    Task<PaymentTransaction?> GetByIdAsync(Guid id);

    /// <summary>A booking has at most one payment transaction (task 69c - booking-payment mapping).</summary>
    Task<PaymentTransaction?> GetByBookingIdAsync(Guid bookingId);

    /// <summary>Used by the webhook handler to resolve which attempt a callback belongs to.</summary>
    Task<PaymentTransaction?> GetByGatewayOrderIdAsync(string gatewayOrderId);

    /// <summary>Reconciliation support (SRS 14.3, task 71) - every transaction record, newest first.</summary>
    Task<IReadOnlyList<PaymentTransaction>> ListAsync(DateTime? fromUtc, DateTime? toUtc, PaymentTransactionStatus? status);

    /// <summary>
    /// Paginated variant of <see cref="ListAsync"/> for the admin payment
    /// transaction view (SRS 12.13.1, task 311) - same filters, plus an
    /// exact <paramref name="bookingId"/> match, and a total count for
    /// paging. Mirrors <c>IProviderPayoutRepository.SearchAsync</c>'s
    /// (Rows, TotalCount) shape.
    /// </summary>
    Task<(IReadOnlyList<PaymentTransaction> Rows, int TotalCount)> SearchAsync(
        Guid? bookingId, PaymentTransactionStatus? status, DateTime? fromUtc, DateTime? toUtc, int page, int pageSize);

    /// <summary>Batched, attempts-free read of the commission snapshot for each of the given bookings (task 255 pattern) - see <see cref="PaymentCommissionSnapshot"/>. Bookings with no transaction are simply absent from the result.</summary>
    Task<IReadOnlyList<PaymentCommissionSnapshot>> ListCommissionSnapshotsByBookingIdsAsync(IReadOnlyCollection<Guid> bookingIds);
}
