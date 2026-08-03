using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Application.ProviderManagement;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class ProviderRepository : IProviderRepository
{
    private readonly NestlyDbContext _context;

    public ProviderRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(Provider entity)
    {
        await _context.Set<Provider>().AddAsync(entity);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(Provider entity)
    {
        _context.Set<Provider>().Update(entity);
        await _context.SaveChangesAsync();
    }

    public Task<Provider?> GetByIdAsync(Guid id) =>
        _context.Set<Provider>().FirstOrDefaultAsync(p => p.Id == id);

    public Task<bool> ExistsAsync(Guid id) =>
        _context.Set<Provider>().AnyAsync(p => p.Id == id);

    public Task<bool> ExistsByPhoneAsync(string phone) =>
        _context.Set<Provider>().AnyAsync(p => p.Phone == phone);

    public Task<Provider?> GetByPhoneAsync(string phone) =>
        _context.Set<Provider>().FirstOrDefaultAsync(p => p.Phone == phone);

    /// <summary>
    /// Search/filter with pagination (task 150a). String filters use
    /// ToLower()+Contains rather than Npgsql's ILike so the same LINQ
    /// translates on both the production Postgres provider and the SQLite
    /// provider the test suite runs against - matching CustomerRepository.SearchAsync.
    /// </summary>
    public async Task<ProviderSearchResult> SearchAsync(ProviderSearchFilter filter)
    {
        var query = _context.Set<Provider>().AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Name))
        {
            string term = filter.Name.ToLower();
            query = query.Where(p => p.LegalName.ToLower().Contains(term) || p.DisplayName.ToLower().Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(filter.Phone))
        {
            string term = filter.Phone.ToLower();
            query = query.Where(p => p.Phone.ToLower().Contains(term));
        }

        if (filter.Status.HasValue)
        {
            query = query.Where(p => p.Status == filter.Status.Value);
        }

        if (filter.OnboardingStatus.HasValue)
        {
            query = query.Where(p => p.OnboardingStatus == filter.OnboardingStatus.Value);
        }

        int totalCount = await query.CountAsync();

        var rows = await query
            .OrderByDescending(p => p.CreatedAt)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync();

        return new ProviderSearchResult(rows, totalCount);
    }
}
