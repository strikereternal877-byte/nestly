using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Abstractions.Time;
using Nestly.Application.MonthlyService;
using Nestly.Application.ProviderManagement;
using Nestly.Domain;
using Nestly.Domain.MonthlyService;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;

namespace Nestly.Infrastructure.Services.MonthlyService;

/// <summary>Whose view an attendance row is being rendered for - decides the allowed actions and whether the day code is shown.</summary>
public enum MonthlyServiceViewer
{
    Customer,
    Provider,
    Admin
}

/// <summary>
/// The pieces of docs/MONTHLY-SERVICE.md that the customer, professional,
/// admin and job services all need, kept in one place so none of them
/// re-implements scheduling, payment completion or the "what may this viewer
/// do with this day" rules: clock and policy, attendance materialization,
/// schedule removal, invoice payment (ledger credit + resuming a contract
/// paused for non-payment), name lookups and response mapping.
/// </summary>
public class MonthlyServiceEngine
{
    public static class Actions
    {
        public const string Skip = "skip";
        public const string Unskip = "unskip";
        public const string Confirm = "confirm";
        public const string Dispute = "dispute";
        public const string CheckIn = "check-in";
        public const string CheckOut = "check-out";
        public const string Leave = "leave";
        public const string CancelLeave = "cancel-leave";
        public const string CustomerUnavailable = "customer-unavailable";
        public const string Correct = "correct";
        public const string ResolveDispute = "resolve-dispute";
    }

    private readonly IMonthlyServiceContractRepository _contractRepository;
    private readonly IMonthlyServiceAttendanceRepository _attendanceRepository;
    private readonly IMonthlyServiceInvoiceRepository _invoiceRepository;
    private readonly IProviderEarningLedgerService _earningLedgerService;
    private readonly IProviderEarningLedgerRepository _earningLedgerRepository;
    private readonly NestlyDbContext _context;
    private readonly IBusinessClock _clock;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MonthlyServiceEngine> _logger;

    public MonthlyServiceEngine(
        IMonthlyServiceContractRepository contractRepository,
        IMonthlyServiceAttendanceRepository attendanceRepository,
        IMonthlyServiceInvoiceRepository invoiceRepository,
        IProviderEarningLedgerService earningLedgerService,
        IProviderEarningLedgerRepository earningLedgerRepository,
        NestlyDbContext context,
        IBusinessClock clock,
        TimeProvider timeProvider,
        IOptions<MonthlyServiceOptions> options,
        ILogger<MonthlyServiceEngine> logger)
    {
        _contractRepository = contractRepository;
        _attendanceRepository = attendanceRepository;
        _invoiceRepository = invoiceRepository;
        _earningLedgerService = earningLedgerService;
        _earningLedgerRepository = earningLedgerRepository;
        _context = context;
        _clock = clock;
        _timeProvider = timeProvider;
        _logger = logger;
        Options = options.Value;
        Policy = Options.ToPolicy();
    }

    public MonthlyServiceOptions Options { get; }

    public MonthlyServiceAttendancePolicy Policy { get; }

    public DateTime NowUtc => _timeProvider.GetUtcNow().UtcDateTime;

    public DateTime NowLocal => _clock.Now;

    public DateOnly Today => _clock.Today;

    public static (DateOnly Start, DateOnly End) MonthRange(int year, int month)
    {
        var start = new DateOnly(year, month, 1);
        return (start, start.AddMonths(1).AddDays(-1));
    }

    // ---- Scheduling ----

    /// <summary>
    /// Creates the missing <see cref="MonthlyServiceAttendanceStatus.Scheduled"/>
    /// rows for the next <see cref="MonthlyServiceOptions.ScheduleHorizonDays"/>
    /// days. Idempotent: existing dates are skipped (and the unique
    /// (contract, date) index backs that up). Today is included only while
    /// its check-in window is still open.
    /// </summary>
    public async Task<int> MaterializeUpcomingAsync(MonthlyServiceContract contract)
    {
        if (contract.Status != MonthlyServiceContractStatus.Active || contract.ProviderId is not { } providerId)
        {
            return 0;
        }

        var nowLocal = NowLocal;
        var from = Today;
        if (nowLocal > from.ToDateTime(contract.VisitStartTime) + Policy.CheckInClosesAfterVisit)
        {
            from = from.AddDays(1);
        }

        var to = Today.AddDays(Options.ScheduleHorizonDays - 1);
        if (to < from)
        {
            return 0;
        }

        var existing = await _attendanceRepository.ListDatesAsync(contract.Id, from, to);
        var nowUtc = NowUtc;
        var rows = new List<MonthlyServiceAttendance>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (contract.IsScheduledOn(date) && !existing.Contains(date))
            {
                rows.Add(new MonthlyServiceAttendance(Guid.NewGuid(), contract.Id, contract.CustomerId, providerId, date, contract.VisitStartTime, nowUtc));
            }
        }

        await _attendanceRepository.AddRangeAsync(rows);
        return rows.Count;
    }

    /// <summary>Removes every not-yet-happened scheduled day from today on - used on pause, cancel and professional replacement.</summary>
    public Task<int> RemoveUpcomingAsync(MonthlyServiceContract contract) =>
        _attendanceRepository.DeleteScheduledFromAsync(contract.Id, Today);

    /// <summary>
    /// Another non-cancelled contract of <paramref name="providerId"/> whose
    /// days and visit window overlap <paramref name="contract"/>'s, if any -
    /// the one professional cannot be in two houses at once.
    /// </summary>
    public static MonthlyServiceContract? FindConflict(MonthlyServiceContract contract, IEnumerable<MonthlyServiceContract> providerContracts)
    {
        // Two months of dates covers every weekday pattern and every
        // date-of-month pattern at least once, whichever schedule type
        // either side uses (maid weekdays vs car wash dates).
        const int WindowDays = 62;
        var start = contract.VisitStartTime.ToTimeSpan();
        var end = start + contract.VisitDuration;
        foreach (var other in providerContracts)
        {
            if (other.Id == contract.Id || other.Status == MonthlyServiceContractStatus.Cancelled)
            {
                continue;
            }

            var otherStart = other.VisitStartTime.ToTimeSpan();
            var otherEnd = otherStart + other.VisitDuration;
            if (!(start < otherEnd && otherStart < end))
            {
                continue;
            }

            var from = contract.StartDate > other.StartDate ? contract.StartDate : other.StartDate;
            for (var date = from; date < from.AddDays(WindowDays); date = date.AddDays(1))
            {
                if (contract.IsScheduledOn(date) && other.IsScheduledOn(date))
                {
                    return other;
                }
            }
        }

        return null;
    }

    // ---- Payment ----

    /// <summary>
    /// Marks the invoice paid, credits the professional's earnings (net of
    /// commission, once per invoice) and resumes the contract if it was only
    /// paused for non-payment and nothing else is still overdue past grace.
    /// </summary>
    public async Task CompletePaymentAsync(MonthlyServiceInvoice invoice, MonthlyServicePaymentMethod method, string? reference, Guid? adminUserId)
    {
        invoice.MarkPaid(method, reference, adminUserId, NowUtc);
        await _invoiceRepository.UpdateAsync(invoice);

        if (invoice.ProviderNetAmount > 0
            && await _earningLedgerRepository.FindBySourceAsync(ProviderEarningSourceType.MonthlyServiceInvoice, invoice.Id) is null)
        {
            var credit = await _earningLedgerService.RecordAdjustmentAsync(
                invoice.ProviderId,
                new RecordProviderEarningAdjustmentRequest(
                    ProviderEarningEntryType.Credit,
                    invoice.ProviderNetAmount,
                    ProviderEarningSourceType.MonthlyServiceInvoice,
                    invoice.Id,
                    $"Monthly service {invoice.PeriodStart:MMM yyyy} - {invoice.BillableVisits} visit(s)."));
            if (credit.IsFailure)
            {
                // The customer's payment is already recorded; a failed credit is
                // an admin reconciliation item, not a reason to fail the payment.
                _logger.LogWarning(
                    "Monthly service invoice {InvoiceId} paid but crediting provider {ProviderId} failed: {ErrorCode}.",
                    invoice.Id, invoice.ProviderId, credit.Error.Code);
            }
        }

        var contract = await _contractRepository.GetByIdAsync(invoice.ContractId);
        if (contract is { Status: MonthlyServiceContractStatus.Paused, PauseReason: MonthlyServicePauseReason.OverdueInvoice })
        {
            var stillBlocking = (await _invoiceRepository.ListByContractAsync(contract.Id))
                .Any(i => !i.IsPaid && Today > i.DueDate.AddDays(Options.OverdueGraceDays));
            if (!stillBlocking)
            {
                contract.Resume(NowUtc);
                await _contractRepository.UpdateAsync(contract);
                await MaterializeUpcomingAsync(contract);
            }
        }
    }

    // ---- Lookups ----

    public async Task<Dictionary<Guid, Customer>> CustomersAsync(IEnumerable<Guid> ids)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0
            ? new Dictionary<Guid, Customer>()
            : await _context.Set<Customer>().AsNoTracking().Where(c => list.Contains(c.Id)).ToDictionaryAsync(c => c.Id);
    }

    public async Task<Dictionary<Guid, Provider>> ProvidersAsync(IEnumerable<Guid> ids)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0
            ? new Dictionary<Guid, Provider>()
            : await _context.Set<Provider>().AsNoTracking().Where(p => list.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
    }

    public async Task<Dictionary<Guid, string>> ServiceNamesAsync(IEnumerable<Guid> ids)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.Set<Service>().AsNoTracking().Where(s => list.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name);
    }

    public async Task<Dictionary<Guid, string>> CityNamesAsync(IEnumerable<Guid> ids)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.Set<City>().AsNoTracking().Where(c => list.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name);
    }

    public async Task<Dictionary<Guid, CustomerAddress>> AddressesAsync(IEnumerable<Guid> ids)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0
            ? new Dictionary<Guid, CustomerAddress>()
            : await _context.Set<CustomerAddress>().AsNoTracking().Where(a => list.Contains(a.Id)).ToDictionaryAsync(a => a.Id);
    }

    /// <summary>The address's city: via its pincode when it has one, else by city name.</summary>
    public async Task<bool> AddressIsInCityAsync(CustomerAddress address, Guid cityId)
    {
        if (address.PincodeId is { } pincodeId)
        {
            var pincodeCityId = await _context.Set<Pincode>().AsNoTracking()
                .Where(p => p.Id == pincodeId).Select(p => (Guid?)p.CityId).FirstOrDefaultAsync();
            if (pincodeCityId is { } resolved)
            {
                return resolved == cityId;
            }
        }

        var cityName = await _context.Set<City>().AsNoTracking()
            .Where(c => c.Id == cityId).Select(c => c.Name).FirstOrDefaultAsync();
        return cityName is not null && string.Equals(cityName.Trim(), address.City.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    // ---- Mapping ----

    public static IReadOnlyList<DayOfWeek> Days(MonthlyServiceWeekdays weekdays) =>
        new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }
            .Where(d => weekdays.Includes(d))
            .ToList();

    public static IReadOnlyList<int> MonthDates(MonthlyServiceContract contract) => MonthDays.FromMask(contract.MonthDaysMask);

    public static MonthlyServiceWeekdays ToWeekdays(IEnumerable<DayOfWeek> days) =>
        days.Aggregate(MonthlyServiceWeekdays.None, (acc, d) => acc | d.ToWeekdayFlag());

    public static MonthlyServiceAddressSummary? ToAddress(CustomerAddress? address) =>
        address is null
            ? null
            : new MonthlyServiceAddressSummary(address.Id, address.Label, address.Line1, address.Line2, address.Landmark, address.City, address.Pincode, address.ContactName, address.ContactMobile);

    public static MonthlyServiceProviderSummary? ToProviderSummary(Provider? provider) =>
        provider is null ? null : new MonthlyServiceProviderSummary(provider.Id, provider.DisplayName, provider.PublicPhotoUrl);

    public MonthlyServiceAttendanceSummary Summarize(IEnumerable<MonthlyServiceAttendance> rows, decimal ratePerVisit)
    {
        var list = rows.ToList();
        var billable = list.Count(r => r.IsBillable(Policy.BillCustomerUnavailable));
        return new MonthlyServiceAttendanceSummary(
            list.Count(r => r.Status == MonthlyServiceAttendanceStatus.Present),
            list.Count(r => r.Status == MonthlyServiceAttendanceStatus.CustomerUnavailable),
            list.Count(r => r.Status == MonthlyServiceAttendanceStatus.CustomerSkipped),
            list.Count(r => r.Status == MonthlyServiceAttendanceStatus.ProviderLeave),
            list.Count(r => r.Status == MonthlyServiceAttendanceStatus.Absent),
            list.Count(r => r.Status == MonthlyServiceAttendanceStatus.Scheduled),
            list.Count(r => r.DisputeStatus == MonthlyServiceDisputeStatus.Open),
            billable,
            billable * ratePerVisit);
    }

    public MonthlyServiceAttendanceItem ToItem(MonthlyServiceAttendance row, MonthlyServiceViewer viewer)
    {
        var nowLocal = NowLocal;
        var today = DateOnly.FromDateTime(nowLocal);
        var start = row.VisitStartLocal;
        var open = !row.IsInvoiced;
        var billable = row.IsBillable(Policy.BillCustomerUnavailable);
        var scheduled = row.Status == MonthlyServiceAttendanceStatus.Scheduled;
        var actions = new List<string>();

        switch (viewer)
        {
            case MonthlyServiceViewer.Customer:
                if (open && scheduled && nowLocal <= start - Policy.SkipCutoffBeforeVisit) actions.Add(Actions.Skip);
                if (open && row.Status == MonthlyServiceAttendanceStatus.CustomerSkipped && nowLocal <= start - Policy.SkipCutoffBeforeVisit) actions.Add(Actions.Unskip);
                if (open && scheduled && row.Date == today && nowLocal >= start - Policy.CheckInOpensBeforeVisit) actions.Add(Actions.Confirm);
                if (open && billable && row.DisputeStatus == MonthlyServiceDisputeStatus.None) actions.Add(Actions.Dispute);
                break;
            case MonthlyServiceViewer.Provider:
                if (open && scheduled && nowLocal >= start - Policy.CheckInOpensBeforeVisit && nowLocal <= start + Policy.CheckInClosesAfterVisit) actions.Add(Actions.CheckIn);
                if (open && row.Status == MonthlyServiceAttendanceStatus.Present && row.CheckedInAtUtc is not null && row.CheckedOutAtUtc is null) actions.Add(Actions.CheckOut);
                if (open && scheduled && nowLocal < start) actions.Add(Actions.Leave);
                if (open && row.Status == MonthlyServiceAttendanceStatus.ProviderLeave && nowLocal < start) actions.Add(Actions.CancelLeave);
                if (open && scheduled && row.Date == today && nowLocal >= start) actions.Add(Actions.CustomerUnavailable);
                break;
            case MonthlyServiceViewer.Admin:
                if (open && (row.Date <= today || !scheduled)) actions.Add(Actions.Correct);
                if (open && row.DisputeStatus == MonthlyServiceDisputeStatus.Open) actions.Add(Actions.ResolveDispute);
                break;
        }

        var showCode = viewer == MonthlyServiceViewer.Customer && scheduled && row.Date == today;

        return new MonthlyServiceAttendanceItem(
            row.Id,
            row.ContractId,
            row.Date,
            MonthlyServiceTimeFormat.Format(row.VisitStartTime),
            row.Status,
            row.MarkedBy,
            row.MarkedAtUtc,
            row.Note,
            row.CheckedInAtUtc,
            row.CheckedOutAtUtc,
            row.DisputeStatus,
            row.DisputeReason,
            row.DisputeResolutionNote,
            billable,
            row.IsInvoiced,
            showCode ? row.DayCode : null,
            actions);
    }

    public async Task<MonthlyServiceAttendanceMonthResponse> MonthAsync(MonthlyServiceContract contract, int year, int month, MonthlyServiceViewer viewer)
    {
        var (start, end) = MonthRange(year, month);
        var rows = await _attendanceRepository.ListByContractAsync(contract.Id, start, end);
        return new MonthlyServiceAttendanceMonthResponse(
            contract.Id, year, month,
            Summarize(rows, contract.RatePerVisitSnapshot),
            rows.Select(r => ToItem(r, viewer)).ToList());
    }

    public async Task<IReadOnlyList<MonthlyServiceInvoiceResponse>> ToInvoiceResponsesAsync(IReadOnlyList<MonthlyServiceInvoice> invoices, bool includeCommission)
    {
        if (invoices.Count == 0)
        {
            return Array.Empty<MonthlyServiceInvoiceResponse>();
        }

        var contracts = (await _contractRepository.ListByIdsAsync(invoices.Select(i => i.ContractId).Distinct().ToList()))
            .ToDictionary(c => c.Id);
        var customers = await CustomersAsync(invoices.Select(i => i.CustomerId));
        var providers = await ProvidersAsync(invoices.Select(i => i.ProviderId));

        return invoices.Select(i => new MonthlyServiceInvoiceResponse(
            i.Id,
            i.ContractId,
            contracts.TryGetValue(i.ContractId, out var c) ? c.PlanNameSnapshot : string.Empty,
            i.CustomerId,
            customers.TryGetValue(i.CustomerId, out var cu) ? cu.Name : string.Empty,
            i.ProviderId,
            providers.TryGetValue(i.ProviderId, out var p) ? p.DisplayName : string.Empty,
            i.PeriodStart,
            i.PeriodEnd,
            i.PresentCount,
            i.CustomerUnavailableCount,
            i.CustomerSkippedCount,
            i.ProviderLeaveCount,
            i.AbsentCount,
            i.BillableVisits,
            i.RatePerVisit,
            i.Amount,
            includeCommission ? i.CommissionAmount : 0m,
            includeCommission ? i.ProviderNetAmount : 0m,
            i.Status,
            i.IssuedAtUtc,
            i.DueDate,
            i.PaidAtUtc,
            i.PaymentMethod,
            i.PaymentReference)).ToList();
    }
}
