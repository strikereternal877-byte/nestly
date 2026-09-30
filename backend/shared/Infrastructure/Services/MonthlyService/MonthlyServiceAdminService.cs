using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.MonthlyService;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Domain.MonthlyService;
using Nestly.Infrastructure.Persistence;

namespace Nestly.Infrastructure.Services.MonthlyService;

/// <inheritdoc cref="IMonthlyServiceAdminService"/>
public class MonthlyServiceAdminService : IMonthlyServiceAdminService
{
    private const int MaxEligibleProviders = 100;

    private readonly IMonthlyServicePlanRepository _planRepository;
    private readonly IMonthlyServiceContractRepository _contractRepository;
    private readonly IMonthlyServiceAttendanceRepository _attendanceRepository;
    private readonly IMonthlyServiceInvoiceRepository _invoiceRepository;
    private readonly IMonthlyServiceDailyJob _dailyJob;
    private readonly IAuditLogWriter _auditLogWriter;
    private readonly MonthlyServiceEngine _engine;
    private readonly NestlyDbContext _context;
    private readonly ILogger<MonthlyServiceAdminService> _logger;

    public MonthlyServiceAdminService(
        IMonthlyServicePlanRepository planRepository,
        IMonthlyServiceContractRepository contractRepository,
        IMonthlyServiceAttendanceRepository attendanceRepository,
        IMonthlyServiceInvoiceRepository invoiceRepository,
        IMonthlyServiceDailyJob dailyJob,
        IAuditLogWriter auditLogWriter,
        MonthlyServiceEngine engine,
        NestlyDbContext context,
        ILogger<MonthlyServiceAdminService> logger)
    {
        _planRepository = planRepository;
        _contractRepository = contractRepository;
        _attendanceRepository = attendanceRepository;
        _invoiceRepository = invoiceRepository;
        _dailyJob = dailyJob;
        _auditLogWriter = auditLogWriter;
        _engine = engine;
        _context = context;
        _logger = logger;
    }

    // ---- Plans ----

    public async Task<IReadOnlyList<MonthlyServicePlanAdminResponse>> ListPlansAsync()
    {
        var plans = await _planRepository.ListAsync(activeOnly: false, cityId: null);
        return await ToPlanResponsesAsync(plans);
    }

    public async Task<Result<MonthlyServicePlanAdminResponse>> GetPlanAsync(Guid id)
    {
        var plan = await _planRepository.GetByIdAsync(id);
        return plan is null ? PlanNotFound() : (await ToPlanResponsesAsync([plan]))[0];
    }

    public async Task<Result<MonthlyServicePlanAdminResponse>> CreatePlanAsync(MonthlyServicePlanUpsertRequest request, Guid adminUserId)
    {
        var check = await ValidatePlanReferencesAsync(request, excludingId: null);
        if (check is not null)
        {
            return check;
        }

        MonthlyServicePlan plan;
        try
        {
            plan = new MonthlyServicePlan(
                Guid.NewGuid(), request.ServiceId, request.CityId, request.Name, request.Description,
                request.Basis, request.HoursPerVisit, request.IncludedTasks, request.RatePerVisit, request.CommissionPercent,
                request.Frequency, request.TimesPerPeriod);
        }
        catch (ArgumentException ex)
        {
            return Error.Validation("MonthlyService.InvalidPlan", MonthlyServiceEngine.UserMessage(ex));
        }

        await _auditLogWriter.WriteAsync(new AuditEntry(nameof(MonthlyServicePlan), plan.Id.ToString(), "Created"));
        await _planRepository.AddAsync(plan);
        return (await ToPlanResponsesAsync([plan]))[0];
    }

    public async Task<Result<MonthlyServicePlanAdminResponse>> UpdatePlanAsync(Guid id, MonthlyServicePlanUpsertRequest request, Guid adminUserId)
    {
        var plan = await _planRepository.GetByIdAsync(id);
        if (plan is null)
        {
            return PlanNotFound();
        }

        var check = await ValidatePlanReferencesAsync(request, excludingId: id);
        if (check is not null)
        {
            return check;
        }

        try
        {
            plan.Update(request.ServiceId, request.CityId, request.Name, request.Description, request.Basis,
                request.HoursPerVisit, request.IncludedTasks, request.RatePerVisit, request.CommissionPercent, adminUserId,
                request.Frequency, request.TimesPerPeriod);
        }
        catch (ArgumentException ex)
        {
            return Error.Validation("MonthlyService.InvalidPlan", MonthlyServiceEngine.UserMessage(ex));
        }

        await _auditLogWriter.WriteAsync(new AuditEntry(nameof(MonthlyServicePlan), plan.Id.ToString(), "Updated"));
        await _planRepository.UpdateAsync(plan);
        return (await ToPlanResponsesAsync([plan]))[0];
    }

    public async Task<Result> SetPlanActiveAsync(Guid id, bool active, Guid adminUserId)
    {
        var plan = await _planRepository.GetByIdAsync(id);
        if (plan is null)
        {
            return Result.Failure(PlanNotFound());
        }

        if (active)
        {
            plan.Activate(adminUserId);
        }
        else
        {
            plan.Deactivate(adminUserId);
        }

        await _auditLogWriter.WriteAsync(new AuditEntry(nameof(MonthlyServicePlan), plan.Id.ToString(), active ? "Activated" : "Deactivated"));
        await _planRepository.UpdateAsync(plan);
        return Result.Success();
    }

    // ---- Contracts ----

    public async Task<MonthlyServiceContractAdminSearchResponse> SearchContractsAsync(MonthlyServiceContractStatus? status, string? customerSearch, int page, int pageSize)
    {
        var (safePage, safePageSize) = PagedQueryExtensions.Normalize(page, pageSize);
        var (items, total) = await _contractRepository.SearchAsync(status, customerSearch, safePage, safePageSize);
        return new MonthlyServiceContractAdminSearchResponse(await ToListItemsAsync(items), total, safePage, safePageSize);
    }

    public async Task<Result<MonthlyServiceContractAdminDetailResponse>> GetContractAsync(Guid id)
    {
        var contract = await _contractRepository.GetByIdAsync(id);
        return contract is null ? ContractNotFound() : await ToDetailAsync(contract);
    }

    public async Task<Result<MonthlyServiceAttendanceMonthResponse>> GetAttendanceAsync(Guid contractId, int year, int month)
    {
        var contract = await _contractRepository.GetByIdAsync(contractId);
        return contract is null ? ContractNotFound() : await _engine.MonthAsync(contract, year, month, MonthlyServiceViewer.Admin);
    }

    public async Task<Result<IReadOnlyList<MonthlyServiceEligibleProvider>>> ListEligibleProvidersAsync(Guid contractId)
    {
        var contract = await _contractRepository.GetByIdAsync(contractId);
        if (contract is null)
        {
            return ContractNotFound();
        }

        var categoryId = await _context.Set<Service>().AsNoTracking()
            .Where(s => s.Id == contract.ServiceIdSnapshot).Select(s => (Guid?)s.CategoryId).FirstOrDefaultAsync();

        var skilled = await _context.Set<ProviderSkillMapping>().AsNoTracking()
            .Where(m => m.IsActive && (m.ServiceId == contract.ServiceIdSnapshot || (m.ServiceId == null && m.CategoryId == categoryId)))
            .Select(m => m.ProviderId).Distinct().ToListAsync();
        var inCity = await _context.Set<ProviderServiceArea>().AsNoTracking()
            .Where(a => a.IsActive && a.CityId == contract.CityIdSnapshot)
            .Select(a => a.ProviderId).Distinct().ToListAsync();

        var skilledSet = skilled.ToHashSet();
        var citySet = inCity.ToHashSet();
        var candidateIds = skilledSet.Union(citySet).ToList();

        var providers = await _context.Set<Provider>().AsNoTracking()
            .Where(p => candidateIds.Contains(p.Id) && p.Status == ProviderStatus.Active)
            .ToListAsync();

        var ids = providers.Select(p => p.Id).ToList();
        var theirContracts = await _context.MonthlyServiceContracts.AsNoTracking()
            .Where(c => c.ProviderId != null && ids.Contains(c.ProviderId.Value) && c.Status != MonthlyServiceContractStatus.Cancelled)
            .ToListAsync();
        var byProvider = theirContracts.ToLookup(c => c.ProviderId!.Value);

        IReadOnlyList<MonthlyServiceEligibleProvider> result = providers
            .Select(p =>
            {
                var conflict = MonthlyServiceEngine.FindConflict(contract, byProvider[p.Id]);
                return new MonthlyServiceEligibleProvider(
                    p.Id, p.DisplayName, p.Phone,
                    skilledSet.Contains(p.Id), citySet.Contains(p.Id),
                    byProvider[p.Id].Count(c => c.Id != contract.Id),
                    conflict is null ? null : $"Already visits another customer at {MonthlyServiceTimeFormat.Format(conflict.VisitStartTime)} on overlapping days.");
            })
            .OrderByDescending(p => p.HasSkill && p.ServesCity)
            .ThenBy(p => p.Conflict is not null)
            .ThenBy(p => p.ActiveContractCount)
            .ThenBy(p => p.DisplayName)
            .Take(MaxEligibleProviders)
            .ToList();
        return Result<IReadOnlyList<MonthlyServiceEligibleProvider>>.Success(result);
    }

    public async Task<Result<MonthlyServiceContractAdminDetailResponse>> AssignProviderAsync(Guid contractId, Guid providerId, Guid adminUserId)
    {
        var contract = await _contractRepository.GetByIdAsync(contractId);
        if (contract is null)
        {
            return ContractNotFound();
        }

        var provider = await _context.Set<Provider>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == providerId);
        if (provider is null || provider.Status != ProviderStatus.Active)
        {
            return Error.Business("MonthlyService.ProviderNotActive", "Only an active professional can be assigned.");
        }

        if (contract.ProviderId == providerId)
        {
            return Error.Business("MonthlyService.AlreadyAssigned", "This professional is already assigned.");
        }

        var conflict = MonthlyServiceEngine.FindConflict(contract, await _contractRepository.ListByProviderAsync(providerId));
        if (conflict is not null)
        {
            return Error.Conflict("MonthlyService.ScheduleConflict",
                $"{provider.DisplayName} already visits another customer at {MonthlyServiceTimeFormat.Format(conflict.VisitStartTime)} on overlapping days.");
        }

        var previousProviderId = contract.ProviderId;
        try
        {
            contract.AssignProvider(providerId, _engine.NowUtc);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Error.Business("MonthlyService.CannotAssign", MonthlyServiceEngine.UserMessage(ex));
        }

        await _auditLogWriter.WriteAsync(new AuditEntry(
            nameof(MonthlyServiceContract), contract.Id.ToString(), previousProviderId is null ? "ProviderAssigned" : "ProviderReplaced",
            previousProviderId?.ToString(), providerId.ToString()));
        await _contractRepository.UpdateAsync(contract);

        // A replacement takes over the upcoming schedule; the predecessor's
        // past days (and their billing) are untouched.
        await _engine.HandOverUpcomingAsync(contract, providerId);
        await _engine.MaterializeUpcomingAsync(contract);
        _logger.LogInformation("Monthly service contract {ContractId} assigned to provider {ProviderId}.", contract.Id, providerId);
        return await ToDetailAsync(contract);
    }

    public Task<Result> PauseContractAsync(Guid contractId, Guid adminUserId) =>
        ChangeContractAsync(contractId, "Paused",
            c => c.Pause(MonthlyServicePauseReason.Admin, _engine.NowUtc),
            c => _engine.RemoveUpcomingAsync(c));

    public Task<Result> ResumeContractAsync(Guid contractId, Guid adminUserId) =>
        ChangeContractAsync(contractId, "Resumed",
            c => c.Resume(_engine.NowUtc),
            c => _engine.MaterializeUpcomingAsync(c));

    public Task<Result> CancelContractAsync(Guid contractId, string? reason, Guid adminUserId) =>
        ChangeContractAsync(contractId, "Cancelled",
            c => c.Cancel(reason, _engine.NowUtc),
            c => _engine.RemoveAllUpcomingAsync(c));

    // ---- Attendance / disputes ----

    public async Task<IReadOnlyList<MonthlyServiceDisputeAdminItem>> ListOpenDisputesAsync()
    {
        var rows = await _attendanceRepository.ListOpenDisputesAsync();
        if (rows.Count == 0)
        {
            return Array.Empty<MonthlyServiceDisputeAdminItem>();
        }

        var contracts = (await _contractRepository.ListByIdsAsync(rows.Select(r => r.ContractId).Distinct().ToList())).ToDictionary(c => c.Id);
        var customers = await _engine.CustomersAsync(rows.Select(r => r.CustomerId));
        var providers = await _engine.ProvidersAsync(rows.Select(r => r.ProviderId));
        return rows.Select(r => new MonthlyServiceDisputeAdminItem(
            _engine.ToItem(r, MonthlyServiceViewer.Admin),
            customers.TryGetValue(r.CustomerId, out var cu) ? cu.Name : string.Empty,
            providers.TryGetValue(r.ProviderId, out var p) ? p.DisplayName : string.Empty,
            contracts.TryGetValue(r.ContractId, out var c) ? c.PlanNameSnapshot : string.Empty)).ToList();
    }

    public Task<Result<MonthlyServiceAttendanceItem>> ResolveDisputeAsync(Guid attendanceId, MonthlyServiceResolveDisputeRequest request, Guid adminUserId) =>
        MutateAttendanceAsync(attendanceId, request.Upheld ? "DisputeUpheld" : "DisputeRejected",
            row => row.ResolveDispute(request.Upheld, request.CorrectedStatus, request.Note, _engine.NowUtc, _engine.Policy));

    public Task<Result<MonthlyServiceAttendanceItem>> CorrectAttendanceAsync(Guid attendanceId, MonthlyServiceCorrectAttendanceRequest request, Guid adminUserId) =>
        MutateAttendanceAsync(attendanceId, $"CorrectedTo{request.Status}",
            row => row.CorrectByAdmin(request.Status, request.Note, _engine.NowUtc));

    // ---- Invoices ----

    public async Task<MonthlyServiceInvoiceAdminSearchResponse> SearchInvoicesAsync(MonthlyServiceInvoiceStatus? status, int page, int pageSize)
    {
        var (safePage, safePageSize) = PagedQueryExtensions.Normalize(page, pageSize);
        var (items, total, outstanding) = await _invoiceRepository.SearchAsync(status, safePage, safePageSize);
        return new MonthlyServiceInvoiceAdminSearchResponse(
            await _engine.ToInvoiceResponsesAsync(items, includeCommission: true), total, safePage, safePageSize, outstanding);
    }

    public async Task<Result<MonthlyServiceInvoiceResponse>> RecordPaymentAsync(Guid invoiceId, MonthlyServiceRecordPaymentRequest request, Guid adminUserId)
    {
        var invoice = await _invoiceRepository.GetByIdAsync(invoiceId);
        if (invoice is null)
        {
            return Error.NotFound("MonthlyService.InvoiceNotFound", "Invoice not found.");
        }

        if (invoice.IsPaid)
        {
            return Error.Business("MonthlyService.AlreadyPaid", "This invoice is already paid.");
        }

        // Joins the unit of work CompletePaymentAsync saves with the invoice.
        await _auditLogWriter.WriteAsync(new AuditEntry(
            nameof(MonthlyServiceInvoice), invoice.Id.ToString(), "PaymentRecorded", null, $"{request.Method} {request.Reference}".Trim()));
        await _engine.CompletePaymentAsync(invoice, request.Method, request.Reference, adminUserId);

        _logger.LogInformation("Monthly service invoice {InvoiceId} marked paid ({Method}) by an admin.", invoice.Id, request.Method);
        return (await _engine.ToInvoiceResponsesAsync([invoice], includeCommission: true))[0];
    }

    public Task<MonthlyServiceDailyRunResult> RunDailyJobNowAsync(CancellationToken cancellationToken) =>
        _dailyJob.RunAsync(cancellationToken);

    // ---- Helpers ----

    private async Task<Error?> ValidatePlanReferencesAsync(MonthlyServicePlanUpsertRequest request, Guid? excludingId)
    {
        if (!await _context.Set<Service>().AnyAsync(s => s.Id == request.ServiceId))
        {
            return Error.Validation("MonthlyService.ServiceNotFound", "The selected service does not exist.");
        }

        if (!await _context.Set<City>().AnyAsync(c => c.Id == request.CityId))
        {
            return Error.Validation("MonthlyService.CityNotFound", "The selected city does not exist.");
        }

        if (await _planRepository.NameExistsAsync(request.Name, excludingId))
        {
            return Error.Conflict("MonthlyService.DuplicatePlanName", "A plan with this name already exists.");
        }

        return null;
    }

    private async Task<IReadOnlyList<MonthlyServicePlanAdminResponse>> ToPlanResponsesAsync(IReadOnlyList<MonthlyServicePlan> plans)
    {
        var services = await _engine.ServiceNamesAsync(plans.Select(p => p.ServiceId));
        var cities = await _engine.CityNamesAsync(plans.Select(p => p.CityId));
        var counts = await _contractRepository.CountActiveByPlanAsync();
        return plans.Select(p => new MonthlyServicePlanAdminResponse(
            p.Id, p.ServiceId, services.GetValueOrDefault(p.ServiceId, string.Empty),
            p.CityId, cities.GetValueOrDefault(p.CityId, string.Empty),
            p.Name, p.Description, p.Basis, p.HoursPerVisit, p.IncludedTasks, p.RatePerVisit, p.CommissionPercent,
            p.IsActive, counts.GetValueOrDefault(p.Id), p.CreatedAtUtc, p.UpdatedAtUtc, p.Frequency, p.TimesPerPeriod)).ToList();
    }

    private async Task<IReadOnlyList<MonthlyServiceContractAdminListItem>> ToListItemsAsync(IReadOnlyList<MonthlyServiceContract> contracts)
    {
        var customers = await _engine.CustomersAsync(contracts.Select(c => c.CustomerId));
        var providers = await _engine.ProvidersAsync(contracts.Where(c => c.ProviderId != null).Select(c => c.ProviderId!.Value));
        var cities = await _engine.CityNamesAsync(contracts.Select(c => c.CityIdSnapshot));
        return contracts.Select(c => new MonthlyServiceContractAdminListItem(
            c.Id,
            c.CustomerId,
            customers.TryGetValue(c.CustomerId, out var cu) ? cu.Name : string.Empty,
            customers.TryGetValue(c.CustomerId, out var cu2) ? cu2.Mobile : string.Empty,
            c.PlanNameSnapshot,
            cities.GetValueOrDefault(c.CityIdSnapshot, string.Empty),
            c.Status,
            c.PauseReason,
            c.ProviderId,
            c.ProviderId is { } pid && providers.TryGetValue(pid, out var p) ? p.DisplayName : null,
            MonthlyServiceEngine.Days(c.Weekdays),
            MonthlyServiceTimeFormat.Format(c.VisitStartTime),
            c.StartDate,
            c.EndDate,
            c.CreatedAtUtc,
            c.FrequencySnapshot,
            c.TimesPerPeriodSnapshot,
            MonthlyServiceEngine.MonthDates(c))).ToList();
    }

    private async Task<MonthlyServiceContractAdminDetailResponse> ToDetailAsync(MonthlyServiceContract contract)
    {
        var item = (await ToListItemsAsync([contract]))[0];
        var addresses = await _engine.AddressesAsync([contract.AddressId]);
        var today = _engine.Today;
        var (start, end) = MonthlyServiceEngine.MonthRange(today.Year, today.Month);
        var rows = await _attendanceRepository.ListByContractAsync(contract.Id, start, end);
        var invoices = await _engine.ToInvoiceResponsesAsync(await _invoiceRepository.ListByContractAsync(contract.Id), includeCommission: true);
        return new MonthlyServiceContractAdminDetailResponse(
            item,
            contract.BasisSnapshot,
            contract.HoursPerVisitSnapshot,
            contract.IncludedTasksSnapshot,
            contract.RatePerVisitSnapshot,
            contract.CommissionPercentSnapshot,
            contract.CustomerNote,
            MonthlyServiceEngine.ToAddress(addresses.GetValueOrDefault(contract.AddressId)),
            _engine.Summarize(rows, contract.RatePerVisitSnapshot),
            invoices,
            contract.CancelledAtUtc,
            contract.CancellationReason);
    }

    private async Task<Result> ChangeContractAsync(
        Guid contractId, string action, Action<MonthlyServiceContract> change, Func<MonthlyServiceContract, Task> afterSave)
    {
        var contract = await _contractRepository.GetByIdAsync(contractId);
        if (contract is null)
        {
            return Result.Failure(ContractNotFound());
        }

        try
        {
            change(contract);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Result.Failure(Error.Business("MonthlyService.ContractActionNotAllowed", MonthlyServiceEngine.UserMessage(ex)));
        }

        // Audit entry joins the same unit of work as the contract change.
        await _auditLogWriter.WriteAsync(new AuditEntry(nameof(MonthlyServiceContract), contract.Id.ToString(), action));
        await _contractRepository.UpdateAsync(contract);
        await afterSave(contract);

        _logger.LogInformation("Monthly service contract {ContractId} {Action} by an admin.", contract.Id, action);
        return Result.Success();
    }

    private async Task<Result<MonthlyServiceAttendanceItem>> MutateAttendanceAsync(Guid attendanceId, string action, Action<MonthlyServiceAttendance> change)
    {
        var row = await _attendanceRepository.GetByIdAsync(attendanceId);
        if (row is null)
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

        await _auditLogWriter.WriteAsync(new AuditEntry(nameof(MonthlyServiceAttendance), row.Id.ToString(), action));
        await _attendanceRepository.UpdateAsync(row);
        return _engine.ToItem(row, MonthlyServiceViewer.Admin);
    }

    private static Error PlanNotFound() => Error.NotFound("MonthlyService.PlanNotFound", "Plan not found.");

    private static Error ContractNotFound() => Error.NotFound("MonthlyService.ContractNotFound", "Monthly service not found.");
}
