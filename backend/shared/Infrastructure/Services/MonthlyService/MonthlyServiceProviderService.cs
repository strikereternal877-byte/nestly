using Microsoft.Extensions.Logging;
using Nestly.Application.MonthlyService;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain.MonthlyService;

namespace Nestly.Infrastructure.Services.MonthlyService;

/// <inheritdoc cref="IMonthlyServiceProviderService"/>
public class MonthlyServiceProviderService : IMonthlyServiceProviderService
{
    private readonly IMonthlyServiceContractRepository _contractRepository;
    private readonly IMonthlyServiceAttendanceRepository _attendanceRepository;
    private readonly IMonthlyServiceInvoiceRepository _invoiceRepository;
    private readonly MonthlyServiceEngine _engine;
    private readonly ILogger<MonthlyServiceProviderService> _logger;

    public MonthlyServiceProviderService(
        IMonthlyServiceContractRepository contractRepository,
        IMonthlyServiceAttendanceRepository attendanceRepository,
        IMonthlyServiceInvoiceRepository invoiceRepository,
        MonthlyServiceEngine engine,
        ILogger<MonthlyServiceProviderService> logger)
    {
        _contractRepository = contractRepository;
        _attendanceRepository = attendanceRepository;
        _invoiceRepository = invoiceRepository;
        _engine = engine;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MonthlyServiceProviderContractResponse>> ListContractsAsync(Guid providerId)
    {
        var contracts = await _contractRepository.ListByProviderAsync(providerId);
        if (contracts.Count == 0)
        {
            return Array.Empty<MonthlyServiceProviderContractResponse>();
        }

        var services = await _engine.ServiceNamesAsync(contracts.Select(c => c.ServiceIdSnapshot));
        var customers = await _engine.CustomersAsync(contracts.Select(c => c.CustomerId));
        var addresses = await _engine.AddressesAsync(contracts.Select(c => c.AddressId));
        var today = _engine.Today;
        var (monthStart, monthEnd) = MonthlyServiceEngine.MonthRange(today.Year, today.Month);
        var monthRows = (await _attendanceRepository.ListByContractsAsync(contracts.Select(c => c.Id).ToList(), monthStart, monthEnd))
            .Where(r => r.ProviderId == providerId)
            .ToLookup(r => r.ContractId);

        return contracts.Select(c => new MonthlyServiceProviderContractResponse(
            c.Id,
            c.PlanNameSnapshot,
            services.GetValueOrDefault(c.ServiceIdSnapshot, string.Empty),
            c.BasisSnapshot,
            c.HoursPerVisitSnapshot,
            c.IncludedTasksSnapshot,
            MonthlyServiceEngine.Days(c.Weekdays),
            MonthlyServiceTimeFormat.Format(c.VisitStartTime),
            c.StartDate,
            c.EndDate,
            c.Status,
            customers.TryGetValue(c.CustomerId, out var cu) ? cu.Name : string.Empty,
            c.CustomerNote,
            MonthlyServiceEngine.ToAddress(addresses.GetValueOrDefault(c.AddressId)),
            c.RatePerVisitSnapshot,
            NetPerVisit(c),
            _engine.Summarize(monthRows[c.Id], NetPerVisit(c)),
            c.FrequencySnapshot,
            c.TimesPerPeriodSnapshot,
            MonthlyServiceEngine.MonthDates(c))).ToList();
    }

    public async Task<Result<MonthlyServiceAttendanceMonthResponse>> GetAttendanceAsync(Guid providerId, Guid contractId, int year, int month)
    {
        var contract = await _contractRepository.GetByIdAsync(contractId);
        if (contract is null || contract.ProviderId != providerId)
        {
            return Error.NotFound("MonthlyService.ContractNotFound", "Monthly service not found.");
        }

        // Only the days this professional was the expected one for - a replaced
        // predecessor's days are not theirs to see or act on. Amounts are
        // their net per visit, not the customer's rate.
        var (start, end) = MonthlyServiceEngine.MonthRange(year, month);
        var rows = (await _attendanceRepository.ListByContractAsync(contract.Id, start, end))
            .Where(r => r.ProviderId == providerId)
            .ToList();
        return new MonthlyServiceAttendanceMonthResponse(
            contract.Id, year, month,
            _engine.Summarize(rows, NetPerVisit(contract)),
            rows.Select(r => _engine.ToItem(r, MonthlyServiceViewer.Provider)).ToList());
    }

    public async Task<IReadOnlyList<MonthlyServiceProviderVisitResponse>> ListVisitsAsync(Guid providerId, DateOnly? date)
    {
        var day = date ?? _engine.Today;
        var rows = await _attendanceRepository.ListByProviderAndDateAsync(providerId, day);
        if (rows.Count == 0)
        {
            return Array.Empty<MonthlyServiceProviderVisitResponse>();
        }

        var contracts = (await _contractRepository.ListByIdsAsync(rows.Select(r => r.ContractId).Distinct().ToList())).ToDictionary(c => c.Id);
        var customers = await _engine.CustomersAsync(rows.Select(r => r.CustomerId));
        var addresses = await _engine.AddressesAsync(contracts.Values.Select(c => c.AddressId));

        return rows
            .Where(r => contracts.ContainsKey(r.ContractId))
            .Select(r =>
            {
                var c = contracts[r.ContractId];
                return new MonthlyServiceProviderVisitResponse(
                    _engine.ToItem(r, MonthlyServiceViewer.Provider),
                    c.PlanNameSnapshot,
                    c.BasisSnapshot,
                    c.HoursPerVisitSnapshot,
                    c.IncludedTasksSnapshot,
                    customers.TryGetValue(r.CustomerId, out var cu) ? cu.Name : string.Empty,
                    MonthlyServiceEngine.ToAddress(addresses.GetValueOrDefault(c.AddressId)));
            })
            .ToList();
    }

    public async Task<IReadOnlyList<MonthlyServiceInvoiceResponse>> ListInvoicesAsync(Guid providerId) =>
        await _engine.ToInvoiceResponsesAsync(await _invoiceRepository.ListByProviderAsync(providerId), includeCommission: true);

    public Task<Result<MonthlyServiceAttendanceItem>> CheckInAsync(Guid providerId, Guid attendanceId, MonthlyServiceCheckInRequest request) =>
        MutateAsync(providerId, attendanceId, "check-in",
            row => row.CheckIn(request.Code, _engine.NowLocal, _engine.NowUtc, _engine.Policy, request.Latitude, request.Longitude));

    public Task<Result<MonthlyServiceAttendanceItem>> CheckOutAsync(Guid providerId, Guid attendanceId) =>
        MutateAsync(providerId, attendanceId, "check-out", row => row.CheckOut(_engine.NowUtc));

    public Task<Result<MonthlyServiceAttendanceItem>> MarkCustomerUnavailableAsync(Guid providerId, Guid attendanceId, string? note) =>
        MutateAsync(providerId, attendanceId, "customer-unavailable", row => row.MarkCustomerUnavailable(note, _engine.NowLocal, _engine.NowUtc));

    public Task<Result<MonthlyServiceAttendanceItem>> MarkLeaveAsync(Guid providerId, Guid attendanceId, string? note) =>
        MutateAsync(providerId, attendanceId, "leave", row => row.MarkLeave(note, _engine.NowLocal, _engine.NowUtc));

    public Task<Result<MonthlyServiceAttendanceItem>> CancelLeaveAsync(Guid providerId, Guid attendanceId) =>
        MutateAsync(providerId, attendanceId, "cancel-leave", row => row.CancelLeave(_engine.NowLocal));

    private static decimal NetPerVisit(MonthlyServiceContract c) =>
        c.RatePerVisitSnapshot - Math.Round(c.RatePerVisitSnapshot * c.CommissionPercentSnapshot / 100m, 2, MidpointRounding.AwayFromZero);

    private async Task<Result<MonthlyServiceAttendanceItem>> MutateAsync(Guid providerId, Guid attendanceId, string action, Action<MonthlyServiceAttendance> change)
    {
        var row = await _attendanceRepository.GetByIdAsync(attendanceId);
        if (row is null || row.ProviderId != providerId)
        {
            return Error.NotFound("MonthlyService.AttendanceNotFound", "Visit not found.");
        }

        try
        {
            change(row);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Error.Business("MonthlyService.AttendanceActionNotAllowed", MonthlyServiceEngine.UserMessage(ex));
        }

        await _attendanceRepository.UpdateAsync(row);
        _logger.LogInformation("Monthly service attendance {AttendanceId}: provider {Action}.", row.Id, action);
        return _engine.ToItem(row, MonthlyServiceViewer.Provider);
    }
}
