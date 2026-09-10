using Nestly.Application.Payments;
using Nestly.Application.Refunds;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <inheritdoc cref="IAdminPaymentQueryService"/>
public sealed class AdminPaymentQueryService : IAdminPaymentQueryService
{
    /// <summary>Same bounds as AuditLogQueryService/ProviderPayoutService's admin search endpoints (task 251).</summary>
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    private static readonly Error TransactionNotFound = Error.NotFound(
        "AdminPayment.NotFound", "Payment transaction was not found.");

    private readonly IPaymentTransactionRepository _paymentRepository;
    private readonly IRefundTransactionRepository _refundRepository;

    public AdminPaymentQueryService(IPaymentTransactionRepository paymentRepository, IRefundTransactionRepository refundRepository)
    {
        _paymentRepository = paymentRepository;
        _refundRepository = refundRepository;
    }

    public async Task<Result<PagedAdminPaymentTransactionResponse>> SearchAsync(AdminPaymentTransactionFilterRequest filter)
    {
        int page = filter.Page < 1 ? 1 : filter.Page;
        int pageSize = filter.PageSize switch
        {
            <= 0 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => filter.PageSize
        };

        var (rows, totalCount) = await _paymentRepository.SearchAsync(
            filter.BookingId, filter.Status, filter.FromUtc, filter.ToUtc, page, pageSize);

        var items = rows.Select(ToListItem).ToList();
        return new PagedAdminPaymentTransactionResponse(items, totalCount, page, pageSize);
    }

    public async Task<Result<AdminPaymentTransactionDetailResponse>> GetDetailAsync(Guid transactionId)
    {
        var transaction = await _paymentRepository.GetByIdAsync(transactionId);
        if (transaction is null)
        {
            return TransactionNotFound;
        }

        var refunds = await _refundRepository.ListByPaymentTransactionAsync(transactionId);

        return new AdminPaymentTransactionDetailResponse(
            transaction.Id,
            transaction.BookingId,
            transaction.CustomerId,
            transaction.Amount,
            transaction.Currency,
            transaction.Status,
            transaction.Attempts.Select(ToAttemptResponse).ToList(),
            refunds.Select(ToRefundResponse).ToList(),
            transaction.CommissionRatePercentage,
            transaction.CommissionAmount,
            transaction.CreatedAtUtc,
            transaction.UpdatedAtUtc);
    }

    /// <summary>Internal, not private: reused by <see cref="AdminPaymentReconciliationService.VoidAsync"/> so a voided transaction's response mirrors exactly what this list already returns for the same row, without a second copy of the mapping.</summary>
    internal static AdminPaymentTransactionListItemResponse ToListItem(PaymentTransaction transaction)
    {
        var latestAttempt = transaction.LatestAttempt;
        return new AdminPaymentTransactionListItemResponse(
            transaction.Id,
            transaction.BookingId,
            transaction.CustomerId,
            transaction.Amount,
            transaction.Currency,
            transaction.Status,
            latestAttempt?.GatewayOrderId,
            latestAttempt?.GatewayPaymentRef,
            transaction.CreatedAtUtc,
            transaction.UpdatedAtUtc);
    }

    private static PaymentAttemptResponse ToAttemptResponse(PaymentAttempt attempt) => new(
        attempt.Id, attempt.AttemptNumber, attempt.GatewayOrderId, attempt.GatewayPaymentRef,
        attempt.Status, attempt.FailureReason, attempt.CreatedAtUtc, attempt.CompletedAtUtc);

    private static AdminRefundTransactionResponse ToRefundResponse(RefundTransaction refund) => new(
        refund.Id, refund.Type, refund.Method, refund.Amount, refund.Status,
        refund.GatewayRefundRef, refund.Reason, refund.CreatedAtUtc, refund.ProcessedAtUtc);
}
