using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Payments;
using Nestly.Application.ProviderManagement;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <inheritdoc cref="IProviderEarningLedgerService"/>
public class ProviderEarningLedgerService : IProviderEarningLedgerService
{
    /// <summary>Same bounds as <c>ProviderPayoutService.SearchAsync</c> - neither this nor that endpoint validates its own query string.</summary>
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    private readonly IProviderRepository _providerRepository;
    private readonly IProviderEarningLedgerRepository _ledgerRepository;
    private readonly IBookingRepository _bookingRepository;
    private readonly IPaymentTransactionRepository _paymentTransactionRepository;
    private readonly IProviderPayoutRepository _payoutRepository;

    public ProviderEarningLedgerService(
        IProviderRepository providerRepository,
        IProviderEarningLedgerRepository ledgerRepository,
        IBookingRepository bookingRepository,
        IPaymentTransactionRepository paymentTransactionRepository,
        IProviderPayoutRepository payoutRepository)
    {
        _providerRepository = providerRepository;
        _ledgerRepository = ledgerRepository;
        _bookingRepository = bookingRepository;
        _paymentTransactionRepository = paymentTransactionRepository;
        _payoutRepository = payoutRepository;
    }

    public async Task<Result<ProviderEarningLedgerEntryResponse>> RecordAdjustmentAsync(Guid providerId, RecordProviderEarningAdjustmentRequest request)
    {
        if (!await _providerRepository.ExistsAsync(providerId))
        {
            return Error.NotFound("ProviderEarningLedger.ProviderNotFound", "Provider was not found.");
        }

        var latest = await _ledgerRepository.GetLatestAsync(providerId);
        decimal currentBalance = latest?.BalanceAfter ?? 0m;

        decimal newBalance = request.EntryType == ProviderEarningEntryType.Credit
            ? currentBalance + request.Amount
            : currentBalance - request.Amount;

        if (newBalance < 0)
        {
            return Error.Business("ProviderEarningLedger.InsufficientBalance", "This debit would take the provider's earnings balance negative.");
        }

        var entry = new ProviderEarningLedgerEntry(
            Guid.NewGuid(), providerId, request.EntryType, request.Amount, newBalance,
            request.SourceType, request.SourceReferenceId, request.Description);
        await _ledgerRepository.AddAsync(entry);

        return ToResponse(entry);
    }

    public async Task<Result<ProviderEarningsSummaryResponse>> GetSummaryAsync(Guid providerId)
    {
        if (!await _providerRepository.ExistsAsync(providerId))
        {
            return Error.NotFound("ProviderEarningLedger.ProviderNotFound", "Provider was not found.");
        }

        var entries = await _ledgerRepository.ListByProviderAsync(providerId);
        decimal balance = entries.Count > 0 ? entries[0].BalanceAfter : 0m;

        return new ProviderEarningsSummaryResponse(providerId, balance, entries.Select(ToResponse).ToList());
    }

    public async Task<Result<ProviderEarningJobSearchResponse>> GetJobEarningsAsync(
        Guid providerId, DateOnly? fromDate, DateOnly? toDate, int page, int pageSize)
    {
        if (!await _providerRepository.ExistsAsync(providerId))
        {
            return Error.NotFound("ProviderEarningLedger.ProviderNotFound", "Provider was not found.");
        }

        // Clamp before the query, same as ProviderPayoutService.SearchAsync -
        // neither this nor that endpoint validates its own query string.
        page = page < 1 ? 1 : page;
        pageSize = pageSize switch
        {
            <= 0 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => pageSize
        };

        // Task 148's ledger has no per-source-type/date-range query, and a
        // provider's own ledger is never large enough to justify adding one -
        // filtered in memory, same as LedgerSection's client-side period tabs.
        var jobCredits = (await _ledgerRepository.ListByProviderAsync(providerId))
            .Where(e => e.SourceType == ProviderEarningSourceType.JobCompletion && e.SourceReferenceId is not null)
            .ToList();

        if (jobCredits.Count == 0)
        {
            return new ProviderEarningJobSearchResponse([], 0, page, pageSize, 0m, 0);
        }

        var bookingIds = jobCredits.Select(e => e.SourceReferenceId!.Value).Distinct().ToList();

        // Batched the same way ProviderJobService.ListAsync batches its own
        // booking + commission reads (task 255 pattern) rather than one
        // round trip per ledger entry.
        var bookingsById = (await _bookingRepository.ListSummariesByIdsAsync(bookingIds)).ToDictionary(b => b.Id);
        var serviceNamesByBookingId = await _bookingRepository.ListServiceNamesByIdsAsync(bookingIds);
        var commissionByBookingId = (await _paymentTransactionRepository.ListCommissionSnapshotsByBookingIdsAsync(bookingIds))
            .ToDictionary(s => s.BookingId, s => s.CommissionAmount ?? 0m);
        var payouts = await _payoutRepository.ListByProviderAsync(providerId);

        var rows = new List<ProviderEarningJobResponse>();
        foreach (var entry in jobCredits)
        {
            if (!bookingsById.TryGetValue(entry.SourceReferenceId!.Value, out var booking))
            {
                // Data-integrity gap (a booking deleted after crediting the
                // ledger, which nothing in this codebase actually does) -
                // skip rather than surface a row with no service/date/gross
                // to show, mirroring ProviderJobService.ListAsync's own
                // skip-if-missing handling for the same lookup.
                continue;
            }

            if (fromDate is not null && booking.SlotDate < fromDate)
            {
                continue;
            }

            if (toDate is not null && booking.SlotDate > toDate)
            {
                continue;
            }

            decimal commissionAmount = commissionByBookingId.GetValueOrDefault(booking.Id, 0m);

            // Gross is derived as commission + net (the amount actually
            // credited to the ledger) rather than read separately off
            // booking.TotalPayableSnapshot, so this row's three figures
            // always reconcile with each other by construction - the exact
            // gap the CSV row calls out ("a provider cannot tell what they
            // will actually be paid").
            decimal netAmountToProvider = entry.Amount;
            decimal grossAmount = commissionAmount + netAmountToProvider;

            rows.Add(new ProviderEarningJobResponse(
                booking.Id,
                booking.BookingReference,
                serviceNamesByBookingId.GetValueOrDefault(booking.Id, "(service unavailable)"),
                booking.SlotDate,
                grossAmount,
                commissionAmount,
                netAmountToProvider,
                ResolveJobPayoutStatus(entry.CreatedAtUtc, payouts),
                entry.CreatedAtUtc));
        }

        var ordered = rows.OrderByDescending(r => r.CreditedAtUtc).ToList();
        decimal totalNetAmount = ordered.Sum(r => r.NetAmountToProvider);

        var page1Based = (long)(page - 1) * pageSize;
        var pageItems = ordered.Skip((int)Math.Min(page1Based, ordered.Count)).Take(pageSize).ToList();

        return new ProviderEarningJobSearchResponse(pageItems, ordered.Count, page, pageSize, totalNetAmount, ordered.Count);
    }

    /// <summary>
    /// Maps a job's credit date onto the provider's payout batches (task
    /// 148/150c). v1 payouts are manual, admin-run batches over a period
    /// (OPEN DECISIONS #3) with no FK back to the ledger entries they summed -
    /// so a covering batch is found by period, mirroring the same
    /// period-membership check <see cref="ProviderPayoutService.CreateBatchAsync"/>
    /// uses to select entries in the first place. No covering batch at all is
    /// the common case for a recently completed job, not a gap.
    /// </summary>
    private static ProviderJobPayoutStatus ResolveJobPayoutStatus(DateTime creditedAtUtc, IReadOnlyList<ProviderPayout> payouts)
    {
        var creditDate = DateOnly.FromDateTime(creditedAtUtc);
        var coveringBatch = payouts
            .Where(p => creditDate >= p.PeriodStart && creditDate <= p.PeriodEnd)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefault();

        return coveringBatch?.Status switch
        {
            ProviderPayoutStatus.Pending => ProviderJobPayoutStatus.PendingSettlement,
            ProviderPayoutStatus.Processing => ProviderJobPayoutStatus.Processing,
            ProviderPayoutStatus.Paid => ProviderJobPayoutStatus.Paid,
            ProviderPayoutStatus.Failed => ProviderJobPayoutStatus.Failed,
            _ => ProviderJobPayoutStatus.AwaitingBatch
        };
    }

    private static ProviderEarningLedgerEntryResponse ToResponse(ProviderEarningLedgerEntry entry) => new(
        entry.Id, entry.ProviderId, entry.EntryType, entry.Amount, entry.BalanceAfter,
        entry.SourceType, entry.SourceReferenceId, entry.Description, entry.CreatedAtUtc);
}
