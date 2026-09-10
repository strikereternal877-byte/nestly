using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.Payments;

/// <summary>
/// Payment reconciliation (docs/OPEN-FIXES-FEATURES.csv "Payment
/// reconciliation" row): "gateway orders versus booking status" for bookings
/// still Awaiting Payment/Payment Failed - a read side (<see cref="GetReconciliationAsync"/>)
/// classifying each into <see cref="PaymentReconciliationCategory"/>, and the
/// one write action this surface needs beyond what already exists elsewhere
/// (<see cref="VoidAsync"/>). Deliberately a separate service/interface from
/// <see cref="IAdminPaymentQueryService"/> rather than added to it - that one
/// stays exactly what its own doc comment says it is (the read-only
/// transaction list/detail), and this one owns the one write action the
/// "payments" module has ever needed.
///
/// The "retry" action the CSV row's recommended fix also names is
/// deliberately not here: retrying an existing pending/failed order is
/// already <c>PaymentService.CreateOrderAsync</c>'s job (task 70) - a
/// customer, not an admin, drives that from the checkout flow the same way
/// as any other retry, and an admin-triggered retry would need the same
/// eligibility/gateway round-trip that endpoint already does. "Reconcile"
/// likewise is not a new action: it is a link to the two admin actions that
/// already exist for a booking stuck Awaiting Payment - <c>RecordManualPaymentAsync</c>
/// (an offline payment actually happened) or the admin cancel-booking action
/// (it never will) - both already reachable from a booking's own detail page.
/// </summary>
public interface IAdminPaymentReconciliationService
{
    /// <summary>
    /// Every booking Awaiting Payment/Payment Failed that needs admin
    /// attention right now, oldest (most stuck) first, paged. A booking in
    /// one of those two statuses is included only when it falls into one of
    /// three buckets - a fresh, still-in-flight pending order (just started
    /// checkout) is deliberately excluded, not "needs attention" yet:
    /// <list type="bullet">
    /// <item><see cref="PaymentReconciliationCategory.StuckPending"/> - the
    /// transaction's latest attempt is still Created, open longer than the
    /// stuck threshold.</item>
    /// <item><see cref="PaymentReconciliationCategory.Failed"/> - the
    /// transaction's latest attempt failed.</item>
    /// <item><see cref="PaymentReconciliationCategory.Orphaned"/> - no
    /// transaction row exists for the booking at all, or the only one that
    /// does is already <see cref="PaymentTransactionStatus.Cancelled"/> (a
    /// voided order) - either way, no successful payment and nothing
    /// currently in flight either.</item>
    /// </list>
    /// </summary>
    Task<Result<AdminPaymentReconciliationResponse>> GetReconciliationAsync(int page, int pageSize);

    /// <summary>
    /// Voids a stuck pending payment transaction - marks OUR record
    /// <see cref="PaymentTransactionStatus.Cancelled"/> only, no gateway
    /// call, so it drops out of the reconciliation queue's StuckPending
    /// bucket. Fails with "AdminPayment.NotVoidable" for any transaction
    /// not currently <see cref="PaymentTransactionStatus.Pending"/> - a
    /// Failed one has nothing in flight to void, a Success one has been
    /// paid, and a Cancelled one is already voided.
    /// </summary>
    Task<Result<AdminPaymentTransactionListItemResponse>> VoidAsync(Guid transactionId, string? reason);
}
