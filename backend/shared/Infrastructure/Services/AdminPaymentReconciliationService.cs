using Nestly.Application.Bookings;
using Nestly.Application.Payments;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <inheritdoc cref="IAdminPaymentReconciliationService"/>
public sealed class AdminPaymentReconciliationService : IAdminPaymentReconciliationService
{
    /// <summary>Same bounds as AdminPaymentQueryService/AuditLogQueryService's admin search endpoints (task 251).</summary>
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    /// <summary>How long a Pending transaction's latest attempt may sit un-resolved before it counts as "stuck" rather than an ordinary in-flight checkout.</summary>
    private static readonly TimeSpan StuckPendingThreshold = TimeSpan.FromMinutes(30);

    /// <summary>PaymentService.CreateOrderAsync's own hardcoded currency (this platform has no per-booking currency field) - used only for the "no transaction at all" Orphaned row, where there is no <see cref="PaymentTransaction.Currency"/> to read.</summary>
    private const string DefaultCurrency = "INR";

    private static readonly Error TransactionNotFound = Error.NotFound(
        "AdminPayment.NotFound", "Payment transaction was not found.");

    private static readonly Error NotVoidable = Error.Business(
        "AdminPayment.NotVoidable", "Only a pending payment transaction can be voided.");

    private readonly IPaymentTransactionRepository _paymentRepository;
    private readonly IBookingRepository _bookingRepository;
    private readonly TimeProvider _timeProvider;

    public AdminPaymentReconciliationService(
        IPaymentTransactionRepository paymentRepository, IBookingRepository bookingRepository, TimeProvider timeProvider)
    {
        _paymentRepository = paymentRepository;
        _bookingRepository = bookingRepository;
        _timeProvider = timeProvider;
    }

    public async Task<Result<AdminPaymentReconciliationResponse>> GetReconciliationAsync(
        int page, int pageSize, PaymentReconciliationCategory? category = null, string? search = null)
    {
        int safePage = page < 1 ? 1 : page;
        int safePageSize = pageSize switch
        {
            <= 0 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => pageSize
        };

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        var awaitingBookings = await _bookingRepository.ListAwaitingPaymentAsync();
        if (awaitingBookings.Count == 0)
        {
            return new AdminPaymentReconciliationResponse([], 0, safePage, safePageSize, 0, 0, 0);
        }

        var bookingIds = awaitingBookings.Select(b => b.Id).ToList();
        var transactions = await _paymentRepository.ListByBookingIdsAsync(bookingIds);
        // At most one transaction per booking (SRS 11.11.3's booking-payment
        // mapping) - safe to key a dictionary on it directly.
        var transactionByBookingId = transactions.ToDictionary(t => t.BookingId);

        var items = new List<AdminPaymentReconciliationItemResponse>(awaitingBookings.Count);
        foreach (var booking in awaitingBookings)
        {
            transactionByBookingId.TryGetValue(booking.Id, out var transaction);
            var item = Classify(booking, transaction, nowUtc);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        // Bucket counts are computed from every classified row - before
        // category/search narrows anything - so the summary strip always
        // shows true totals, not a shrinking count for the bucket currently
        // filtered to.
        int stuckPendingCount = items.Count(i => i.Category == PaymentReconciliationCategory.StuckPending);
        int failedCount = items.Count(i => i.Category == PaymentReconciliationCategory.Failed);
        int orphanedCount = items.Count(i => i.Category == PaymentReconciliationCategory.Orphaned);

        IEnumerable<AdminPaymentReconciliationItemResponse> filtered = items;
        if (category is not null)
        {
            filtered = filtered.Where(i => i.Category == category);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search.Trim();
            filtered = filtered.Where(i =>
                i.BookingReference.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                i.CustomerName.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = filtered.OrderByDescending(i => i.AgeMinutes).ThenBy(i => i.BookingId).ToList();

        int offset = (safePage - 1) * safePageSize;
        var pageItems = ordered.Skip(offset).Take(safePageSize).ToList();

        return new AdminPaymentReconciliationResponse(
            pageItems, ordered.Count, safePage, safePageSize, stuckPendingCount, failedCount, orphanedCount);
    }

    /// <summary>
    /// Classifies one Awaiting Payment/Payment Failed booking against its
    /// transaction (if any) - see <see cref="IAdminPaymentReconciliationService.GetReconciliationAsync"/>
    /// for the three buckets. Returns null for a booking that does not (yet)
    /// need admin attention: a fresh Pending transaction still within the
    /// stuck threshold, or the defensive Success case that should never
    /// coexist with an Awaiting Payment booking in the first place (a
    /// booking only reaches Confirmed via a successful payment, in the same
    /// transaction that flips both - see PaymentWebhookService).
    /// </summary>
    private static AdminPaymentReconciliationItemResponse? Classify(Booking booking, PaymentTransaction? transaction, DateTime nowUtc)
    {
        PaymentReconciliationCategory category;
        DateTime openSinceUtc;
        decimal amount;
        string currency;

        switch (transaction?.Status)
        {
            case null:
                category = PaymentReconciliationCategory.Orphaned;
                openSinceUtc = booking.CreatedAtUtc;
                amount = booking.TotalPayableSnapshot;
                currency = DefaultCurrency;
                break;

            case PaymentTransactionStatus.Cancelled:
                category = PaymentReconciliationCategory.Orphaned;
                openSinceUtc = transaction.UpdatedAtUtc;
                amount = transaction.Amount;
                currency = transaction.Currency;
                break;

            case PaymentTransactionStatus.Failed:
                category = PaymentReconciliationCategory.Failed;
                openSinceUtc = transaction.LatestAttempt?.CompletedAtUtc ?? transaction.UpdatedAtUtc;
                amount = transaction.Amount;
                currency = transaction.Currency;
                break;

            case PaymentTransactionStatus.Pending:
                var attemptStartedUtc = transaction.LatestAttempt?.CreatedAtUtc ?? transaction.CreatedAtUtc;
                if (nowUtc - attemptStartedUtc < StuckPendingThreshold)
                {
                    return null;
                }

                category = PaymentReconciliationCategory.StuckPending;
                openSinceUtc = attemptStartedUtc;
                amount = transaction.Amount;
                currency = transaction.Currency;
                break;

            default:
                return null;
        }

        int ageMinutes = (int)Math.Max(0, (nowUtc - openSinceUtc).TotalMinutes);

        return new AdminPaymentReconciliationItemResponse(
            category,
            booking.Id,
            booking.BookingReference,
            booking.CustomerNameSnapshot,
            booking.Status,
            BookingStatusMapper.LabelFor(booking.Status),
            transaction?.Id,
            transaction?.Status,
            amount,
            currency,
            openSinceUtc,
            ageMinutes);
    }

    public async Task<Result<AdminPaymentTransactionListItemResponse>> VoidAsync(Guid transactionId, string? reason)
    {
        var transaction = await _paymentRepository.GetByIdAsync(transactionId);
        if (transaction is null)
        {
            return Result.Failure<AdminPaymentTransactionListItemResponse>(TransactionNotFound);
        }

        if (transaction.Status != PaymentTransactionStatus.Pending)
        {
            return Result.Failure<AdminPaymentTransactionListItemResponse>(NotVoidable);
        }

        // One member of a prepaid checkout cannot be voided on its own: the gateway holds a single
        // order for the whole group, and a payment landing later would settle the other members
        // while this one stayed voided.
        if (transaction.LatestAttempt?.PaymentGroupId is not null)
        {
            return Result.Failure<AdminPaymentTransactionListItemResponse>(Error.Business(
                "Payment.PartOfPrepaidCheckout",
                "This payment is one visit of a prepaid checkout and cannot be voided on its own."));
        }

        transaction.Void(string.IsNullOrWhiteSpace(reason) ? "Voided by admin (payment reconciliation)." : reason);
        await _paymentRepository.UpdateAsync(transaction);

        return AdminPaymentQueryService.ToListItem(transaction);
    }
}
