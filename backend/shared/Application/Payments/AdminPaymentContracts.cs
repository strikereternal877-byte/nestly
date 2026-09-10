using Nestly.Domain;

namespace Nestly.Application.Payments;

/// <summary>
/// Admin payment transaction view (SRS 12.13.1, task 311) - a reconciliation
/// list/detail surface over the same <see cref="PaymentTransaction"/>/
/// <see cref="PaymentAttempt"/>/<see cref="RefundTransaction"/> data
/// <see cref="IPaymentService.GetByBookingIdAsync"/> already exposes to the
/// owning customer and <c>BookingManagementService</c> already embeds inside
/// a booking's admin detail view. This is the missing standalone surface:
/// list every transaction (filterable by status and booking), and a detail
/// view that adds refund history alongside the attempts
/// <see cref="PaymentTransactionResponse"/> already carries. Read-only by
/// design for the transaction list/detail themselves - see
/// <see cref="AdminModules.Payments"/>'s doc comment for why - though the
/// module has since gained one genuinely payments-scoped write action (see
/// <see cref="AdminVoidPaymentTransactionRequest"/> below): voiding a stuck
/// pending order is a payment-record state transition, not the refund-
/// initiation action SRS 12.13.2-3 describes (which remains
/// <c>BookingsController</c>'s "bookings.write"-gated territory).
/// </summary>
public sealed record AdminPaymentTransactionFilterRequest(
    Guid? BookingId = null,
    PaymentTransactionStatus? Status = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Page = 1,
    int PageSize = 20);

/// <summary>One row of the admin payment transaction list (SRS 12.13.1's field list, minus the per-attempt detail a list row has no room for).</summary>
public sealed record AdminPaymentTransactionListItemResponse(
    Guid Id,
    Guid BookingId,
    Guid CustomerId,
    decimal Amount,
    string Currency,
    PaymentTransactionStatus Status,
    string? LatestGatewayOrderId,
    string? LatestGatewayPaymentRef,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record PagedAdminPaymentTransactionResponse(
    IReadOnlyList<AdminPaymentTransactionListItemResponse> Items, int TotalCount, int Page, int PageSize);

/// <summary>One refund raised against the transaction, for the detail view's reconciliation trail.</summary>
public sealed record AdminRefundTransactionResponse(
    Guid Id,
    RefundType Type,
    RefundMethod Method,
    decimal Amount,
    RefundStatus Status,
    string? GatewayRefundRef,
    string Reason,
    DateTime CreatedAtUtc,
    DateTime? ProcessedAtUtc);

/// <summary>
/// Full transaction detail for the admin view (SRS 12.13.1) - every gateway
/// round-trip (<see cref="PaymentAttemptResponse"/>, same shape the customer
/// side already returns) plus every refund raised against it, for
/// reconciliation (SRS 14.3).
/// </summary>
public sealed record AdminPaymentTransactionDetailResponse(
    Guid Id,
    Guid BookingId,
    Guid CustomerId,
    decimal Amount,
    string Currency,
    PaymentTransactionStatus Status,
    IReadOnlyList<PaymentAttemptResponse> Attempts,
    IReadOnlyList<AdminRefundTransactionResponse> Refunds,
    decimal? CommissionRatePercentage,
    decimal? CommissionAmount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

// ---- Reconciliation (docs/OPEN-FIXES-FEATURES.csv "Payment reconciliation") ----

/// <summary>
/// Which "needs attention" bucket a reconciliation row falls into - see
/// <see cref="IAdminPaymentReconciliationService.GetReconciliationAsync"/>
/// for exactly how each is classified.
/// </summary>
public enum PaymentReconciliationCategory
{
    /// <summary>A gateway order was created but never resolved to Success/Failed, open longer than the stuck threshold.</summary>
    StuckPending,

    /// <summary>The most recent attempt failed and the booking is still Awaiting Payment/Payment Failed - the customer may retry, or may be stuck.</summary>
    Failed,

    /// <summary>A booking Awaiting Payment/Payment Failed with no successful transaction AND no active (pending) one either - no transaction row at all, or one already voided/cancelled.</summary>
    Orphaned
}

/// <summary>
/// One row of the reconciliation queue: a booking Awaiting Payment/Payment
/// Failed, joined against its payment transaction (if any) and classified
/// into <see cref="PaymentReconciliationCategory"/>. <see cref="PaymentTransactionId"/>/
/// <see cref="TransactionStatus"/> are null only for the "no transaction at
/// all" flavour of <see cref="PaymentReconciliationCategory.Orphaned"/> -
/// an abandoned checkout that never even reached "create order".
/// <see cref="OpenSinceUtc"/> is the latest attempt's start for StuckPending,
/// the failed attempt's completion for Failed, or the booking's creation
/// (no transaction) / the void (a cancelled transaction) for Orphaned -
/// whatever <see cref="AgeMinutes"/> is measured from.
/// </summary>
public sealed record AdminPaymentReconciliationItemResponse(
    PaymentReconciliationCategory Category,
    Guid BookingId,
    string BookingReference,
    string CustomerName,
    BookingStatus BookingStatus,
    string BookingStatusLabel,
    Guid? PaymentTransactionId,
    PaymentTransactionStatus? TransactionStatus,
    decimal Amount,
    string Currency,
    DateTime OpenSinceUtc,
    int AgeMinutes);

/// <summary>Paged, oldest (most stuck) first - see <see cref="AdminPaymentReconciliationItemResponse"/>. The three counts total every matching row, not just the current page, for a summary strip the UI can render without walking every page.</summary>
public sealed record AdminPaymentReconciliationResponse(
    IReadOnlyList<AdminPaymentReconciliationItemResponse> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int StuckPendingCount,
    int FailedCount,
    int OrphanedCount);

/// <summary>Body of the void action - an optional admin-supplied reason, recorded as the voided attempt's <see cref="PaymentAttemptResponse.FailureReason"/>. A default is substituted server-side when omitted.</summary>
public sealed record AdminVoidPaymentTransactionRequest(string? Reason = null);
