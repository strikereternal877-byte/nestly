using Nestly.Domain.MonthlyService;

namespace Nestly.Application.MonthlyService;

public interface IMonthlyServicePlanRepository
{
    Task AddAsync(MonthlyServicePlan plan);

    Task UpdateAsync(MonthlyServicePlan plan);

    Task<MonthlyServicePlan?> GetByIdAsync(Guid id);

    Task<IReadOnlyList<MonthlyServicePlan>> ListAsync(bool activeOnly, Guid? cityId);

    Task<bool> NameExistsAsync(string name, Guid? excludingId);
}

public interface IMonthlyServiceContractRepository
{
    Task AddAsync(MonthlyServiceContract contract);

    Task UpdateAsync(MonthlyServiceContract contract);

    Task<MonthlyServiceContract?> GetByIdAsync(Guid id);

    Task<IReadOnlyList<MonthlyServiceContract>> ListByCustomerAsync(Guid customerId);

    /// <summary>Contracts currently assigned to the professional (any status except cancelled).</summary>
    Task<IReadOnlyList<MonthlyServiceContract>> ListByProviderAsync(Guid providerId);

    /// <summary>Active contracts with a professional - the ones the daily job schedules.</summary>
    Task<IReadOnlyList<MonthlyServiceContract>> ListActiveAsync();

    Task<IReadOnlyList<MonthlyServiceContract>> ListByIdsAsync(IReadOnlyCollection<Guid> ids);

    Task<(IReadOnlyList<MonthlyServiceContract> Items, int TotalCount)> SearchAsync(
        MonthlyServiceContractStatus? status, string? customerSearch, int page, int pageSize);

    Task<Dictionary<Guid, int>> CountActiveByPlanAsync();

    Task<Dictionary<Guid, int>> CountActiveByProviderAsync(IReadOnlyCollection<Guid> providerIds);
}

public interface IMonthlyServiceAttendanceRepository
{
    Task AddRangeAsync(IReadOnlyCollection<MonthlyServiceAttendance> rows);

    Task UpdateAsync(MonthlyServiceAttendance row);

    Task<MonthlyServiceAttendance?> GetByIdAsync(Guid id);

    Task<IReadOnlyList<MonthlyServiceAttendance>> ListByContractAsync(Guid contractId, DateOnly from, DateOnly to);

    Task<IReadOnlyList<MonthlyServiceAttendance>> ListByContractsAsync(IReadOnlyCollection<Guid> contractIds, DateOnly from, DateOnly to);

    Task<IReadOnlyList<MonthlyServiceAttendance>> ListByProviderAndDateAsync(Guid providerId, DateOnly date);

    Task<HashSet<DateOnly>> ListDatesAsync(Guid contractId, DateOnly from, DateOnly to);

    /// <summary>Rows still <see cref="MonthlyServiceAttendanceStatus.Scheduled"/> on a date before <paramref name="beforeDate"/>.</summary>
    Task<IReadOnlyList<MonthlyServiceAttendance>> ListStaleScheduledAsync(DateOnly beforeDate);

    /// <summary>Removes not-yet-happened scheduled rows from <paramref name="fromDate"/> on (pause/cancel/reassignment).</summary>
    Task<int> DeleteScheduledFromAsync(Guid contractId, DateOnly fromDate);

    /// <summary>Contract ids with any not-yet-invoiced, non-scheduled row in the range.</summary>
    Task<IReadOnlyList<Guid>> ListContractIdsWithUninvoicedRowsAsync(DateOnly from, DateOnly to);

    Task<IReadOnlyList<MonthlyServiceAttendance>> ListOpenDisputesAsync();
}

public interface IMonthlyServiceInvoiceRepository
{
    /// <summary>Saves the invoice together with every tracked change on the same unit of work (the attendance rows attached to it).</summary>
    Task AddAsync(MonthlyServiceInvoice invoice);

    Task UpdateAsync(MonthlyServiceInvoice invoice);

    Task<MonthlyServiceInvoice?> GetByIdAsync(Guid id);

    Task<IReadOnlyList<MonthlyServiceInvoice>> ListByCustomerAsync(Guid customerId);

    Task<IReadOnlyList<MonthlyServiceInvoice>> ListByContractAsync(Guid contractId);

    Task<IReadOnlyList<MonthlyServiceInvoice>> ListByProviderAsync(Guid providerId);

    Task<IReadOnlyList<MonthlyServiceInvoice>> ListUnpaidAsync();

    Task<(IReadOnlyList<MonthlyServiceInvoice> Items, int TotalCount, decimal OutstandingAmount)> SearchAsync(
        MonthlyServiceInvoiceStatus? status, int page, int pageSize);
}
