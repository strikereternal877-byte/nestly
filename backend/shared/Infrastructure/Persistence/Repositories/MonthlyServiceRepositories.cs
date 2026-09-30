using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Application.MonthlyService;
using Nestly.Domain.MonthlyService;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class MonthlyServicePlanRepository : IMonthlyServicePlanRepository
{
    private readonly NestlyDbContext _context;

    public MonthlyServicePlanRepository(NestlyDbContext context) => _context = context;

    public async Task AddAsync(MonthlyServicePlan plan)
    {
        await _context.MonthlyServicePlans.AddAsync(plan);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(MonthlyServicePlan plan)
    {
        _context.MonthlyServicePlans.Update(plan);
        await _context.SaveChangesAsync();
    }

    public Task<MonthlyServicePlan?> GetByIdAsync(Guid id) =>
        _context.MonthlyServicePlans.FirstOrDefaultAsync(p => p.Id == id);

    public async Task<IReadOnlyList<MonthlyServicePlan>> ListAsync(bool activeOnly, Guid? cityId)
    {
        var query = _context.MonthlyServicePlans.AsNoTracking();
        if (activeOnly)
        {
            query = query.Where(p => p.IsActive);
        }

        if (cityId is { } city)
        {
            query = query.Where(p => p.CityId == city);
        }

        return await query.OrderBy(p => p.RatePerVisit).ThenBy(p => p.Name).ToListAsync();
    }

    public Task<bool> NameExistsAsync(string name, Guid? excludingId)
    {
        var normalized = name.Trim().ToLower();
        return _context.MonthlyServicePlans.AnyAsync(p => p.Name.ToLower() == normalized && (excludingId == null || p.Id != excludingId));
    }
}

public class MonthlyServiceContractRepository : IMonthlyServiceContractRepository
{
    private readonly NestlyDbContext _context;

    public MonthlyServiceContractRepository(NestlyDbContext context) => _context = context;

    public async Task AddAsync(MonthlyServiceContract contract)
    {
        await _context.MonthlyServiceContracts.AddAsync(contract);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(MonthlyServiceContract contract)
    {
        _context.MonthlyServiceContracts.Update(contract);
        await _context.SaveChangesAsync();
    }

    public Task<MonthlyServiceContract?> GetByIdAsync(Guid id) =>
        _context.MonthlyServiceContracts.FirstOrDefaultAsync(c => c.Id == id);

    public async Task<IReadOnlyList<MonthlyServiceContract>> ListByCustomerAsync(Guid customerId) =>
        await _context.MonthlyServiceContracts
            .Where(c => c.CustomerId == customerId)
            .OrderByDescending(c => c.CreatedAtUtc)
            .ToListAsync();

    public async Task<IReadOnlyList<MonthlyServiceContract>> ListByProviderAsync(Guid providerId) =>
        await _context.MonthlyServiceContracts
            .Where(c => c.ProviderId == providerId && c.Status != MonthlyServiceContractStatus.Cancelled)
            .OrderBy(c => c.VisitStartTime)
            .ToListAsync();

    public async Task<IReadOnlyList<MonthlyServiceContract>> ListActiveAsync() =>
        await _context.MonthlyServiceContracts
            .Where(c => c.Status == MonthlyServiceContractStatus.Active && c.ProviderId != null)
            .ToListAsync();

    public async Task<IReadOnlyList<MonthlyServiceContract>> ListByIdsAsync(IReadOnlyCollection<Guid> ids) =>
        ids.Count == 0
            ? Array.Empty<MonthlyServiceContract>()
            : await _context.MonthlyServiceContracts.Where(c => ids.Contains(c.Id)).ToListAsync();

    public async Task<(IReadOnlyList<MonthlyServiceContract> Items, int TotalCount)> SearchAsync(
        MonthlyServiceContractStatus? status, string? customerSearch, int page, int pageSize)
    {
        var query = _context.MonthlyServiceContracts.AsNoTracking();

        if (status is { } s)
        {
            query = query.Where(c => c.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(customerSearch))
        {
            // ToLower()+Contains rather than ILike so the same LINQ runs on
            // the SQLite test provider too - same choice as ProviderRepository.SearchAsync.
            var term = customerSearch.Trim().ToLower();
            query = query.Where(c => _context.Set<Customer>().Any(cu =>
                cu.Id == c.CustomerId && (cu.Name.ToLower().Contains(term) || cu.Mobile.Contains(term))));
        }

        int total = await query.CountAsync();
        var items = await query
            .OrderByDescending(c => c.CreatedAtUtc)
            .ApplyPaging(page, pageSize)
            .ToListAsync();

        return (items, total);
    }

    public Task<bool> HasRunningContractAtAddressAsync(Guid addressId) =>
        _context.MonthlyServiceContracts.AnyAsync(c => c.AddressId == addressId && c.Status != MonthlyServiceContractStatus.Cancelled);

    public async Task<Dictionary<Guid, int>> CountActiveByPlanAsync() =>
        await _context.MonthlyServiceContracts
            .Where(c => c.Status != MonthlyServiceContractStatus.Cancelled)
            .GroupBy(c => c.PlanId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

    public async Task<Dictionary<Guid, int>> CountActiveByProviderAsync(IReadOnlyCollection<Guid> providerIds)
    {
        if (providerIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        return await _context.MonthlyServiceContracts
            .Where(c => c.ProviderId != null && providerIds.Contains(c.ProviderId.Value) && c.Status != MonthlyServiceContractStatus.Cancelled)
            .GroupBy(c => c.ProviderId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
    }
}

public class MonthlyServiceAttendanceRepository : IMonthlyServiceAttendanceRepository
{
    private readonly NestlyDbContext _context;

    public MonthlyServiceAttendanceRepository(NestlyDbContext context) => _context = context;

    public async Task AddRangeAsync(IReadOnlyCollection<MonthlyServiceAttendance> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await _context.MonthlyServiceAttendance.AddRangeAsync(rows);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(MonthlyServiceAttendance row)
    {
        _context.MonthlyServiceAttendance.Update(row);
        await _context.SaveChangesAsync();
    }

    public Task<MonthlyServiceAttendance?> GetByIdAsync(Guid id) =>
        _context.MonthlyServiceAttendance.FirstOrDefaultAsync(a => a.Id == id);

    public async Task<IReadOnlyList<MonthlyServiceAttendance>> ListByContractAsync(Guid contractId, DateOnly from, DateOnly to) =>
        await _context.MonthlyServiceAttendance
            .Where(a => a.ContractId == contractId && a.Date >= from && a.Date <= to)
            .OrderBy(a => a.Date)
            .ToListAsync();

    public async Task<IReadOnlyList<MonthlyServiceAttendance>> ListByContractsAsync(IReadOnlyCollection<Guid> contractIds, DateOnly from, DateOnly to) =>
        contractIds.Count == 0
            ? Array.Empty<MonthlyServiceAttendance>()
            : await _context.MonthlyServiceAttendance
                .AsNoTracking()
                .Where(a => contractIds.Contains(a.ContractId) && a.Date >= from && a.Date <= to)
                .ToListAsync();

    public async Task<IReadOnlyList<MonthlyServiceAttendance>> ListByProviderAndDateAsync(Guid providerId, DateOnly date) =>
        await _context.MonthlyServiceAttendance
            .Where(a => a.ProviderId == providerId && a.Date == date)
            .OrderBy(a => a.VisitStartTime)
            .ToListAsync();

    public async Task<HashSet<DateOnly>> ListDatesAsync(Guid contractId, DateOnly from, DateOnly to)
    {
        var dates = await _context.MonthlyServiceAttendance
            .Where(a => a.ContractId == contractId && a.Date >= from && a.Date <= to)
            .Select(a => a.Date)
            .ToListAsync();
        return dates.ToHashSet();
    }

    public async Task<IReadOnlyList<MonthlyServiceAttendance>> ListStaleScheduledAsync(DateOnly beforeDate) =>
        await _context.MonthlyServiceAttendance
            .Where(a => a.Status == MonthlyServiceAttendanceStatus.Scheduled && a.Date < beforeDate)
            .ToListAsync();

    public async Task<int> DeleteScheduledFromAsync(Guid contractId, DateOnly fromDate)
    {
        var rows = await _context.MonthlyServiceAttendance
            .Where(a => a.ContractId == contractId
                && a.Date >= fromDate
                && a.Status == MonthlyServiceAttendanceStatus.Scheduled
                && a.InvoiceId == null)
            .ToListAsync();
        if (rows.Count == 0)
        {
            return 0;
        }

        _context.MonthlyServiceAttendance.RemoveRange(rows);
        await _context.SaveChangesAsync();
        return rows.Count;
    }

    public async Task<int> ClearFutureMarksAsync(Guid contractId, DateOnly afterDate, Guid? newProviderId)
    {
        var rows = await _context.MonthlyServiceAttendance
            .Where(a => a.ContractId == contractId
                && a.Date > afterDate
                && a.InvoiceId == null
                && (a.Status == MonthlyServiceAttendanceStatus.ProviderLeave || a.Status == MonthlyServiceAttendanceStatus.CustomerSkipped))
            .ToListAsync();
        if (rows.Count == 0)
        {
            return 0;
        }

        foreach (var row in rows)
        {
            if (newProviderId is { } providerId && row.Status == MonthlyServiceAttendanceStatus.CustomerSkipped)
            {
                row.ReassignProvider(providerId);
            }
            else
            {
                _context.MonthlyServiceAttendance.Remove(row);
            }
        }

        await _context.SaveChangesAsync();
        return rows.Count;
    }

    public async Task<IReadOnlyList<Guid>> ListContractIdsWithUninvoicedRowsAsync(DateOnly from, DateOnly to) =>
        await _context.MonthlyServiceAttendance
            .Where(a => a.Date >= from && a.Date <= to
                && a.InvoiceId == null
                && a.Status != MonthlyServiceAttendanceStatus.Scheduled)
            .Select(a => a.ContractId)
            .Distinct()
            .ToListAsync();

    public async Task<IReadOnlyList<MonthlyServiceAttendance>> ListOpenDisputesAsync() =>
        await _context.MonthlyServiceAttendance
            .AsNoTracking()
            .Where(a => a.DisputeStatus == MonthlyServiceDisputeStatus.Open)
            .OrderBy(a => a.Date)
            .ToListAsync();
}

public class MonthlyServiceInvoiceRepository : IMonthlyServiceInvoiceRepository
{
    private readonly NestlyDbContext _context;

    public MonthlyServiceInvoiceRepository(NestlyDbContext context) => _context = context;

    public async Task AddAsync(MonthlyServiceInvoice invoice)
    {
        await _context.MonthlyServiceInvoices.AddAsync(invoice);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(MonthlyServiceInvoice invoice)
    {
        _context.MonthlyServiceInvoices.Update(invoice);
        await _context.SaveChangesAsync();
    }

    public Task<MonthlyServiceInvoice?> GetByIdAsync(Guid id) =>
        _context.MonthlyServiceInvoices.FirstOrDefaultAsync(i => i.Id == id);

    public async Task<IReadOnlyList<MonthlyServiceInvoice>> ListByCustomerAsync(Guid customerId) =>
        await _context.MonthlyServiceInvoices
            .AsNoTracking()
            .Where(i => i.CustomerId == customerId)
            .OrderByDescending(i => i.PeriodStart)
            .ToListAsync();

    public async Task<IReadOnlyList<MonthlyServiceInvoice>> ListByContractAsync(Guid contractId) =>
        await _context.MonthlyServiceInvoices
            .AsNoTracking()
            .Where(i => i.ContractId == contractId)
            .OrderByDescending(i => i.PeriodStart)
            .ToListAsync();

    public async Task<IReadOnlyList<MonthlyServiceInvoice>> ListByProviderAsync(Guid providerId) =>
        await _context.MonthlyServiceInvoices
            .AsNoTracking()
            .Where(i => i.ProviderId == providerId)
            .OrderByDescending(i => i.PeriodStart)
            .ToListAsync();

    public async Task<IReadOnlyList<MonthlyServiceInvoice>> ListUnpaidAsync() =>
        await _context.MonthlyServiceInvoices
            .Where(i => i.Status != MonthlyServiceInvoiceStatus.Paid)
            .ToListAsync();

    public async Task<(IReadOnlyList<MonthlyServiceInvoice> Items, int TotalCount, decimal OutstandingAmount)> SearchAsync(
        MonthlyServiceInvoiceStatus? status, int page, int pageSize)
    {
        var query = _context.MonthlyServiceInvoices.AsNoTracking();
        if (status is { } s)
        {
            query = query.Where(i => i.Status == s);
        }

        int total = await query.CountAsync();
        // Summed client-side: SQLite (the test provider) cannot SUM decimals,
        // and the unpaid set is small (at most one row per contract per month).
        var outstanding = (await _context.MonthlyServiceInvoices
            .AsNoTracking()
            .Where(i => i.Status != MonthlyServiceInvoiceStatus.Paid)
            .Select(i => i.Amount)
            .ToListAsync()).Sum();

        var items = await query
            .OrderByDescending(i => i.PeriodStart)
            .ThenByDescending(i => i.IssuedAtUtc)
            .ApplyPaging(page, pageSize)
            .ToListAsync();

        return (items, total, outstanding);
    }
}
