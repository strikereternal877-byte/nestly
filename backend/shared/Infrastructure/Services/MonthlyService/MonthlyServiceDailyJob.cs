using Microsoft.Extensions.Logging;
using Nestly.Application.MonthlyService;
using Nestly.Domain.MonthlyService;

namespace Nestly.Infrastructure.Services.MonthlyService;

/// <inheritdoc cref="IMonthlyServiceDailyJob"/>
public class MonthlyServiceDailyJob : IMonthlyServiceDailyJob
{
    /// <summary>How many past months are re-checked for invoices still owed (e.g. one held back by a dispute resolved late).</summary>
    private const int InvoiceLookbackMonths = 3;

    private readonly IMonthlyServiceContractRepository _contractRepository;
    private readonly IMonthlyServiceAttendanceRepository _attendanceRepository;
    private readonly IMonthlyServiceInvoiceRepository _invoiceRepository;
    private readonly MonthlyServiceEngine _engine;
    private readonly ILogger<MonthlyServiceDailyJob> _logger;

    public MonthlyServiceDailyJob(
        IMonthlyServiceContractRepository contractRepository,
        IMonthlyServiceAttendanceRepository attendanceRepository,
        IMonthlyServiceInvoiceRepository invoiceRepository,
        MonthlyServiceEngine engine,
        ILogger<MonthlyServiceDailyJob> logger)
    {
        _contractRepository = contractRepository;
        _attendanceRepository = attendanceRepository;
        _invoiceRepository = invoiceRepository;
        _engine = engine;
        _logger = logger;
    }

    public async Task<MonthlyServiceDailyRunResult> RunAsync(CancellationToken cancellationToken)
    {
        var closed = await CloseStaleDaysAsync(cancellationToken);
        // Daily wallet: today's visits are paid for before the schedule moves on.
        var (charged, notBooked) = await ChargeTodayAsync(cancellationToken);
        var scheduled = await ScheduleAheadAsync(cancellationToken);
        var (issued, held) = await IssueInvoicesAsync(cancellationToken);
        // Before overdue handling, so an invoice the wallet covers today is
        // never counted overdue or allowed to pause its contract.
        var paidFromWallet = await PayFromWalletAsync(cancellationToken);
        var (overdue, paused) = await HandleOverdueAsync(cancellationToken);
        var lowBalanceWarnings = await WarnLowWalletAsync(cancellationToken);

        var result = new MonthlyServiceDailyRunResult(
            closed, scheduled, issued, held, overdue, paused, paidFromWallet, charged, notBooked, lowBalanceWarnings);
        _logger.LogInformation(
            "Monthly service daily run: {Closed} day(s) closed as absent, {Charged} visit(s) paid from wallet, {NotBooked} not booked for low balance, {Scheduled} scheduled, {Issued} invoice(s) issued, {Held} held for disputes, {PaidFromWallet} invoice(s) paid from wallet, {Overdue} marked overdue, {Paused} contract(s) paused, {Warnings} low-balance warning(s).",
            closed, charged, notBooked, scheduled, issued, held, paidFromWallet, overdue, paused, lowBalanceWarnings);
        return result;
    }

    /// <summary>Any day before today still <see cref="MonthlyServiceAttendanceStatus.Scheduled"/> had nothing recorded - it becomes Absent (not charged).</summary>
    private async Task<int> CloseStaleDaysAsync(CancellationToken cancellationToken)
    {
        var stale = await _attendanceRepository.ListStaleScheduledAsync(_engine.Today);
        foreach (var row in stale)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Through the engine: a paid-for day nobody came on is refunded.
            var closed = await _engine.ChangeAttendanceAsync(row, r => r.CloseAsAbsent(_engine.NowUtc));
            if (closed.IsFailure)
            {
                _logger.LogWarning("Closing monthly visit {AttendanceId} failed: {Code}.", row.Id, closed.Error.Code);
            }
        }

        return stale.Count;
    }

    private async Task<(int Charged, int NotBooked)> ChargeTodayAsync(CancellationToken cancellationToken)
    {
        var charged = 0;
        var notBooked = 0;
        foreach (var contract in (await _contractRepository.ListActiveAsync()).Where(c => c.IsDailyWallet))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (c, n) = await _engine.ChargeDueTodayAsync(contract);
                charged += c;
                notBooked += n;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Charging today's visit for monthly service contract {ContractId} failed.", contract.Id);
            }
        }

        return (charged, notBooked);
    }

    /// <summary>
    /// One warning a day per customer whose wallet won't cover their daily-
    /// wallet visits over the next two days - the day before it matters, so
    /// there is time to add money before a visit is turned off.
    /// </summary>
    private async Task<int> WarnLowWalletAsync(CancellationToken cancellationToken)
    {
        var tomorrow = _engine.Today.AddDays(1);
        var dayAfter = tomorrow.AddDays(1);
        var warned = 0;
        var byCustomer = (await _contractRepository.ListActiveAsync()).Where(c => c.IsDailyWallet).GroupBy(c => c.CustomerId);
        foreach (var customer in byCustomer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contracts = customer.ToDictionary(c => c.Id);
            var upcoming = (await _attendanceRepository.ListByContractsAsync(contracts.Keys.ToList(), tomorrow, dayAfter))
                .Where(r => r.Status == MonthlyServiceAttendanceStatus.Scheduled && !r.IsPrepaid)
                .OrderBy(r => r.Date).ThenBy(r => r.VisitStartTime)
                .Select(r => contracts[r.ContractId].RatePerVisitSnapshot)
                .ToList();
            if (upcoming.Count == 0)
            {
                continue;
            }

            var balance = await _engine.WalletBalanceAsync(customer.Key);
            var needed = upcoming.Sum();
            if (balance >= needed)
            {
                continue;
            }

            var covered = 0;
            var running = 0m;
            foreach (var rate in upcoming)
            {
                running += rate;
                if (running > balance)
                {
                    break;
                }

                covered++;
            }

            var first = customer.First();
            first.WarnWalletLow(balance, needed, covered);
            await _contractRepository.UpdateAsync(first);
            warned++;
        }

        return warned;
    }

    private async Task<int> ScheduleAheadAsync(CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var contract in await _contractRepository.ListActiveAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                total += await _engine.MaterializeUpcomingAsync(contract);
            }
            catch (Exception ex)
            {
                // One contract's failure must not stop everyone else's schedule.
                _logger.LogError(ex, "Scheduling monthly service contract {ContractId} failed.", contract.Id);
            }
        }

        return total;
    }

    private async Task<(int Issued, int Held)> IssueInvoicesAsync(CancellationToken cancellationToken)
    {
        var today = _engine.Today;
        var currentMonthStart = new DateOnly(today.Year, today.Month, 1);
        var issued = 0;
        var held = 0;

        for (var back = InvoiceLookbackMonths; back >= 1; back--)
        {
            var periodStart = currentMonthStart.AddMonths(-back);
            // The just-finished month waits for the invoice day so disputes have a
            // few days; older months are overdue for billing already.
            if (back == 1 && today.Day < _engine.Options.InvoiceDayOfMonth)
            {
                continue;
            }

            var periodEnd = periodStart.AddMonths(1).AddDays(-1);
            var contractIds = await _attendanceRepository.ListContractIdsWithUninvoicedRowsAsync(periodStart, periodEnd);
            if (contractIds.Count == 0)
            {
                continue;
            }

            var contracts = await _contractRepository.ListByIdsAsync(contractIds);
            foreach (var contract in contracts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var outcome = await InvoiceContractMonthAsync(contract, periodStart, periodEnd, today);
                    issued += outcome.Issued;
                    held += outcome.Held ? 1 : 0;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Invoicing monthly service contract {ContractId} for {Period} failed.", contract.Id, periodStart);
                }
            }
        }

        return (issued, held);
    }

    private async Task<(int Issued, bool Held)> InvoiceContractMonthAsync(MonthlyServiceContract contract, DateOnly periodStart, DateOnly periodEnd, DateOnly today)
    {
        var rows = (await _attendanceRepository.ListByContractAsync(contract.Id, periodStart, periodEnd))
            .Where(r => !r.IsInvoiced)
            .ToList();

        // An open dispute holds the whole month for this contract until an admin resolves it.
        if (rows.Any(r => r.DisputeStatus == MonthlyServiceDisputeStatus.Open))
        {
            return (0, true);
        }

        var issued = 0;
        var billUnavailable = _engine.Policy.BillCustomerUnavailable;
        foreach (var group in rows.Where(r => r.Status != MonthlyServiceAttendanceStatus.Scheduled).GroupBy(r => r.ProviderId))
        {
            var counts = new MonthlyServiceVisitCounts(
                group.Count(r => r.Status == MonthlyServiceAttendanceStatus.Present),
                group.Count(r => r.Status == MonthlyServiceAttendanceStatus.CustomerUnavailable),
                group.Count(r => r.Status == MonthlyServiceAttendanceStatus.CustomerSkipped),
                group.Count(r => r.Status == MonthlyServiceAttendanceStatus.ProviderLeave),
                group.Count(r => r.Status == MonthlyServiceAttendanceStatus.Absent));

            if (counts.Present + (billUnavailable ? counts.CustomerUnavailable : 0) == 0)
            {
                // Nothing to charge. The days stay open, so an admin correcting
                // one to Present later still gets it billed on a following run.
                continue;
            }

            // Daily wallet: every charged day was paid before it happened, so
            // the month's invoice is a paid statement, not a bill.
            var prepaid = contract.IsDailyWallet
                && group.Where(r => r.IsBillable(billUnavailable)).All(r => r.IsPrepaid);
            var invoice = new MonthlyServiceInvoice(
                Guid.NewGuid(), contract, group.Key, periodStart, periodEnd, counts,
                billUnavailable, today, _engine.Options.InvoiceDueDays, _engine.NowUtc, prepaidDaily: prepaid);
            foreach (var row in group)
            {
                row.AttachToInvoice(invoice.Id);
            }

            // One SaveChanges: the invoice and the attendance rows it locks.
            await _invoiceRepository.AddAsync(invoice);
            if (prepaid)
            {
                // Credits the professional exactly as any other payment does.
                await _engine.CompletePaymentAsync(invoice, MonthlyServicePaymentMethod.Wallet, "Paid day by day from wallet", adminUserId: null);
            }

            issued++;
        }

        return (issued, false);
    }

    /// <summary>
    /// Every unpaid invoice of a contract that pays from wallet, tried again
    /// each day: one the balance could not cover yesterday is paid the day
    /// after the customer tops up, with no action from them.
    /// </summary>
    private async Task<int> PayFromWalletAsync(CancellationToken cancellationToken)
    {
        var unpaid = await _invoiceRepository.ListUnpaidAsync();
        if (unpaid.Count == 0)
        {
            return 0;
        }

        var walletContracts = (await _contractRepository.ListByIdsAsync(unpaid.Select(i => i.ContractId).Distinct().ToList()))
            .Where(c => c.PayFromWallet)
            .Select(c => c.Id)
            .ToHashSet();

        var paid = 0;
        foreach (var invoice in unpaid.Where(i => walletContracts.Contains(i.ContractId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await _engine.PayFromWalletAsync(invoice);
                if (result.IsSuccess)
                {
                    paid++;
                }
                else
                {
                    _logger.LogInformation("Monthly service invoice {InvoiceId} not paid from wallet: {Reason}.", invoice.Id, result.Error.Code);

                    // Told once - on the day the bill is issued; later daily
                    // retries stay quiet until the balance covers it.
                    if (result.Error.Code == "Wallet.InsufficientBalance"
                        && DateOnly.FromDateTime(_engine.NowLocal) == invoice.DueDate.AddDays(-_engine.Options.InvoiceDueDays))
                    {
                        invoice.ReportAutopayFailed(await _engine.WalletBalanceAsync(invoice.CustomerId));
                        await _invoiceRepository.UpdateAsync(invoice);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Paying monthly service invoice {InvoiceId} from wallet failed.", invoice.Id);
            }
        }

        return paid;
    }

    private async Task<(int Overdue, int Paused)> HandleOverdueAsync(CancellationToken cancellationToken)
    {
        var today = _engine.Today;
        var overdue = 0;
        var paused = 0;
        var unpaid = await _invoiceRepository.ListUnpaidAsync();

        foreach (var invoice in unpaid)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (invoice.Status == MonthlyServiceInvoiceStatus.Issued && today > invoice.DueDate)
            {
                invoice.MarkOverdueIfDue(today);
                await _invoiceRepository.UpdateAsync(invoice);
                overdue++;
            }
        }

        var pastGrace = unpaid
            .Where(i => today > i.DueDate.AddDays(_engine.Options.OverdueGraceDays))
            .Select(i => i.ContractId)
            .Distinct()
            .ToList();
        foreach (var contract in await _contractRepository.ListByIdsAsync(pastGrace))
        {
            if (contract.Status != MonthlyServiceContractStatus.Active)
            {
                continue;
            }

            contract.Pause(MonthlyServicePauseReason.OverdueInvoice, _engine.NowUtc);
            await _contractRepository.UpdateAsync(contract);
            await _engine.RemoveUpcomingAsync(contract);
            paused++;
            _logger.LogWarning("Monthly service contract {ContractId} paused: invoice unpaid past the grace period.", contract.Id);
        }

        return (overdue, paused);
    }
}
