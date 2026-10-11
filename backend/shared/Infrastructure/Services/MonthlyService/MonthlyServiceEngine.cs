using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Abstractions.Time;
using Nestly.Application.MonthlyService;
using Nestly.Application.ProviderManagement;
using Nestly.Application.Wallet;
using Nestly.BuildingBlocks.Results;
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
        public const string Rebook = "rebook";
    }

    private readonly IMonthlyServiceContractRepository _contractRepository;
    private readonly IMonthlyServiceAttendanceRepository _attendanceRepository;
    private readonly IMonthlyServiceInvoiceRepository _invoiceRepository;
    private readonly IProviderEarningLedgerService _earningLedgerService;
    private readonly IProviderEarningLedgerRepository _earningLedgerRepository;
    private readonly IWalletService _walletService;
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
        IWalletService walletService,
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
        _walletService = walletService;
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

        // Assignment or resume during the day: today's visit is paid for (or
        // turned off) now, not only at the next nightly run.
        await ChargeDueTodayAsync(contract);
        return rows.Count;
    }

    // ---- Daily wallet (docs/MONTHLY-SERVICE.md DAILY WALLET) ----

    /// <summary>
    /// Applies a change to one attendance day and, for a daily-wallet
    /// contract, keeps that day's money right in the same transaction: a day
    /// that is on today (or is now charged) must be paid for - the change is
    /// refused when the wallet can't - and a paid day that ended without a
    /// charged visit goes back to the wallet.
    /// </summary>
    public async Task<Result> ChangeAttendanceAsync(MonthlyServiceAttendance row, Action<MonthlyServiceAttendance> change, Func<Task>? beforeSave = null)
    {
        var contract = await _contractRepository.GetByIdAsync(row.ContractId);
        var dailyWallet = contract?.IsDailyWallet == true;
        var ownTransaction = dailyWallet && _context.Database.CurrentTransaction is null
            ? await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable)
            : null;
        try
        {
            try
            {
                change(row);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                await AbandonAsync(row, ownTransaction);
                return Result.Failure(Error.Business("MonthlyService.AttendanceActionNotAllowed", UserMessage(ex)));
            }

            if (dailyWallet)
            {
                var settled = await SettleDayAsync(row, contract!);
                if (settled.IsFailure)
                {
                    await AbandonAsync(row, ownTransaction);
                    return settled;
                }
            }

            if (beforeSave is not null)
            {
                await beforeSave();
            }

            await _attendanceRepository.UpdateAsync(row);
            if (ownTransaction is not null)
            {
                await ownTransaction.CommitAsync();
            }

            return Result.Success();
        }
        catch
        {
            if (ownTransaction is not null)
            {
                await ownTransaction.RollbackAsync();
            }

            throw;
        }
        finally
        {
            ownTransaction?.Dispose();
        }
    }

    /// <summary>
    /// A refused change leaves nothing behind: the transaction rolls back and
    /// the tracked row is re-read, so a later save in the same unit of work
    /// (or a retry) never persists the half-applied status.
    /// </summary>
    private async Task AbandonAsync(MonthlyServiceAttendance row, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? ownTransaction)
    {
        if (ownTransaction is not null)
        {
            await ownTransaction.RollbackAsync();
        }

        await _context.Entry(row).ReloadAsync();
    }

    /// <summary>
    /// Pays for today's still-unpaid visits of a daily-wallet contract. A day
    /// the wallet can't cover is turned off (WalletShortfall) and both sides
    /// are told; the customer can add money and book it again before the
    /// cutoff. Idempotent: a paid or already-off day is left alone.
    /// </summary>
    public async Task<(int Charged, int NotBooked)> ChargeDueTodayAsync(MonthlyServiceContract contract)
    {
        if (!contract.IsDailyWallet || contract.Status != MonthlyServiceContractStatus.Active)
        {
            return (0, 0);
        }

        var charged = 0;
        var notBooked = 0;
        var due = (await _attendanceRepository.ListByContractAsync(contract.Id, Today, Today))
            .Where(r => r.Status == MonthlyServiceAttendanceStatus.Scheduled && !r.IsPrepaid && !r.IsInvoiced)
            .ToList();
        foreach (var row in due)
        {
            var paid = await ChangeAttendanceAsync(row, _ => { });
            if (paid.IsSuccess)
            {
                charged++;
            }
            else if (paid.Error.Code == WalletBalanceLowCode)
            {
                row.MarkWalletShortfall(contract.RatePerVisitSnapshot, NowUtc);
                await _attendanceRepository.UpdateAsync(row);
                notBooked++;
            }
            else
            {
                _logger.LogWarning("Charging monthly visit {AttendanceId} failed: {Code}.", row.Id, paid.Error.Code);
            }
        }

        return (charged, notBooked);
    }

    public const string WalletBalanceLowCode = "MonthlyService.WalletBalanceLow";

    private async Task<Result> SettleDayAsync(MonthlyServiceAttendance row, MonthlyServiceContract contract)
    {
        var mustBePaid = !row.IsPrepaid
            && !row.IsInvoiced
            && ((row.Status == MonthlyServiceAttendanceStatus.Scheduled && row.Date <= Today)
                || MonthlyServiceAttendance.IsBillableStatus(row.Status, Policy.BillCustomerUnavailable));
        if (mustBePaid)
        {
            var amount = contract.RatePerVisitSnapshot;
            var debit = await _walletService.DebitAsync(
                row.CustomerId, amount, WalletSourceType.MonthlyServiceVisitCharge, row.Id, $"{contract.PlanNameSnapshot} - {DayLabel(row.Date)}");
            if (debit.IsFailure)
            {
                return Result.Failure(debit.Error.Code == "Wallet.InsufficientBalance"
                    ? Error.Business(WalletBalanceLowCode,
                        $"The wallet balance doesn't cover this visit (Rs {amount.ToString("0.##", CultureInfo.InvariantCulture)}). Add money to the wallet first.")
                    : debit.Error);
            }

            row.MarkPrepaid(amount, debit.Value.Id);
        }
        else if (row.NeedsRefund(Policy.BillCustomerUnavailable))
        {
            await RefundDayAsync(row, contract);
        }

        return Result.Success();
    }

    private async Task RefundDayAsync(MonthlyServiceAttendance row, MonthlyServiceContract contract)
    {
        await _walletService.CreditAsync(
            row.CustomerId, row.PrepaidAmount!.Value, WalletSourceType.MonthlyServiceVisitRefund, row.Id,
            $"Refund: {contract.PlanNameSnapshot} - {DayLabel(row.Date)}");
        row.MarkRefunded(NowUtc);
    }

    private static string DayLabel(DateOnly date) => date.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    /// <summary>The customer's wallet balance, for the low-balance check and messages.</summary>
    public async Task<decimal> WalletBalanceAsync(Guid customerId)
    {
        var balance = await _walletService.GetBalanceAsync(customerId);
        return balance.IsSuccess ? balance.Value.Balance : 0m;
    }

    /// <summary>Removes every not-yet-happened scheduled day from today on - used on pause, cancel and professional replacement.</summary>
    public async Task<int> RemoveUpcomingAsync(MonthlyServiceContract contract)
    {
        // A daily-wallet day already paid for goes back to the wallet before
        // its row goes (a replacement re-creates and re-charges it).
        if (contract.IsDailyWallet)
        {
            var paidAhead = (await _attendanceRepository.ListByContractAsync(contract.Id, Today, Today.AddDays(Options.ScheduleHorizonDays)))
                .Where(r => r.Status == MonthlyServiceAttendanceStatus.Scheduled && r.IsPrepaid && !r.IsInvoiced);
            foreach (var row in paidAhead)
            {
                await RefundDayAsync(row, contract);
                await _attendanceRepository.UpdateAsync(row);
            }
        }

        return await _attendanceRepository.DeleteScheduledFromAsync(contract.Id, Today);
    }

    /// <summary>
    /// The next day a visit is still due for an active contract: the first
    /// scheduled row from today on, or - past the materialized horizon (a
    /// monthly date weeks away) - the next date the schedule itself names.
    /// </summary>
    public async Task<DateOnly?> NextVisitDateAsync(MonthlyServiceContract contract)
    {
        if (contract.Status != MonthlyServiceContractStatus.Active)
        {
            return null;
        }

        var today = Today;
        var horizonEnd = today.AddDays(Options.ScheduleHorizonDays - 1);
        var rows = await _attendanceRepository.ListByContractAsync(contract.Id, today, horizonEnd);
        var next = rows
            .Where(r => r.Status == MonthlyServiceAttendanceStatus.Scheduled)
            .OrderBy(r => r.Date)
            .FirstOrDefault();
        if (next is not null)
        {
            return next.Date;
        }

        // Any date inside the horizon already has its row (possibly skipped), so look beyond it.
        for (var date = horizonEnd.AddDays(1); date <= horizonEnd.AddDays(NextVisitSearchDays); date = date.AddDays(1))
        {
            if (contract.IsScheduledOn(date))
            {
                return date;
            }
        }

        return null;
    }

    private const int NextVisitSearchDays = 62;

    /// <summary>
    /// Cancellation: removes the upcoming schedule and any leave/skip marks on
    /// later days, so a stopped service shows nothing ahead.
    /// </summary>
    public async Task RemoveAllUpcomingAsync(MonthlyServiceContract contract)
    {
        await RemoveUpcomingAsync(contract);
        await _attendanceRepository.ClearFutureMarksAsync(contract.Id, Today, newProviderId: null);
    }

    /// <summary>
    /// Replacement: the outgoing professional's upcoming days and leave go;
    /// the customer's skips carry over to the new professional.
    /// </summary>
    public async Task HandOverUpcomingAsync(MonthlyServiceContract contract, Guid newProviderId)
    {
        await RemoveUpcomingAsync(contract);
        await _attendanceRepository.ClearFutureMarksAsync(contract.Id, Today, newProviderId);
    }

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

    /// <summary>
    /// Pays <paramref name="invoice"/> in full from the customer's wallet. The
    /// debit, the invoice turning Paid and the professional's credit commit
    /// together or not at all. A short balance fails with
    /// <c>Wallet.InsufficientBalance</c> and changes nothing: an invoice is
    /// never part-paid from the wallet.
    /// </summary>
    public async Task<Result> PayFromWalletAsync(MonthlyServiceInvoice invoice)
    {
        if (invoice.IsPaid)
        {
            return Result.Failure(Error.Business("MonthlyService.AlreadyPaid", "This invoice is already paid."));
        }

        // Serializable, like WalletService's own debit: two concurrent payers
        // (the daily job and the customer's tap) cannot both pass the balance
        // check. Reuses a caller's ambient transaction rather than nesting.
        var ownTransaction = _context.Database.CurrentTransaction is null
            ? await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable)
            : null;
        try
        {
            var debit = await _walletService.DebitAsync(
                invoice.CustomerId, invoice.Amount, WalletSourceType.MonthlyServiceInvoice, invoice.Id,
                $"Monthly service {invoice.PeriodStart:MMM yyyy} - {invoice.BillableVisits} visit(s)");
            if (debit.IsFailure)
            {
                if (ownTransaction is not null)
                {
                    await ownTransaction.RollbackAsync();
                }

                return Result.Failure(debit.Error);
            }

            await CompletePaymentAsync(invoice, MonthlyServicePaymentMethod.Wallet, debit.Value.Id.ToString("N"), adminUserId: null);
            if (ownTransaction is not null)
            {
                await ownTransaction.CommitAsync();
            }

            _logger.LogInformation("Monthly service invoice {InvoiceId} paid from wallet.", invoice.Id);
            return Result.Success();
        }
        catch
        {
            if (ownTransaction is not null)
            {
                await ownTransaction.RollbackAsync();
            }

            throw;
        }
        finally
        {
            ownTransaction?.Dispose();
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

    public async Task<Dictionary<Guid, string>> ServiceSlugsAsync(IEnumerable<Guid> ids)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.Set<Service>().AsNoTracking().Where(s => list.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Slug);
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

    /// <summary>
    /// A domain guard's message as the user should read it - without the
    /// " (Parameter 'x')" suffix .NET appends to argument exceptions.
    /// </summary>
    public static string UserMessage(Exception ex) =>
        ex is ArgumentException { ParamName: { } param } argument
            ? argument.Message.Replace($" (Parameter '{param}')", string.Empty, StringComparison.Ordinal).Split('\n')[0].Trim()
            : ex.Message;

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
                if (open && row.Status == MonthlyServiceAttendanceStatus.WalletShortfall && nowLocal <= start - Policy.SkipCutoffBeforeVisit) actions.Add(Actions.Rebook);
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
            actions,
            row.IsPrepaid ? row.PrepaidAmount : null);
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
