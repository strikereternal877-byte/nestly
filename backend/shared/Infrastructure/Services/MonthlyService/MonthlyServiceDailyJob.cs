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
        var scheduled = await ScheduleAheadAsync(cancellationToken);
        var (issued, held) = await IssueInvoicesAsync(cancellationToken);
        var (overdue, paused) = await HandleOverdueAsync(cancellationToken);

        var result = new MonthlyServiceDailyRunResult(closed, scheduled, issued, held, overdue, paused);
        _logger.LogInformation(
            "Monthly service daily run: {Closed} day(s) closed as absent, {Scheduled} scheduled, {Issued} invoice(s) issued, {Held} held for disputes, {Overdue} marked overdue, {Paused} contract(s) paused.",
            closed, scheduled, issued, held, overdue, paused);
        return result;
    }

    /// <summary>Any day before today still <see cref="MonthlyServiceAttendanceStatus.Scheduled"/> had nothing recorded - it becomes Absent (not charged).</summary>
    private async Task<int> CloseStaleDaysAsync(CancellationToken cancellationToken)
    {
        var stale = await _attendanceRepository.ListStaleScheduledAsync(_engine.Today);
        foreach (var row in stale)
        {
            cancellationToken.ThrowIfCancellationRequested();
            row.CloseAsAbsent(_engine.NowUtc);
            await _attendanceRepository.UpdateAsync(row);
        }

        return stale.Count;
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

            var invoice = new MonthlyServiceInvoice(
                Guid.NewGuid(), contract, group.Key, periodStart, periodEnd, counts,
                billUnavailable, today, _engine.Options.InvoiceDueDays, _engine.NowUtc);
            foreach (var row in group)
            {
                row.AttachToInvoice(invoice.Id);
            }

            // One SaveChanges: the invoice and the attendance rows it locks.
            await _invoiceRepository.AddAsync(invoice);
            issued++;
        }

        return (issued, false);
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
