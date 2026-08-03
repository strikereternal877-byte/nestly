using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class ProviderPayoutRepository : IProviderPayoutRepository
{
    private readonly NestlyDbContext _context;

    public ProviderPayoutRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(ProviderPayout entity)
    {
        await _context.ProviderPayouts.AddAsync(entity);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(ProviderPayout entity)
    {
        if (_context.Entry(entity).State == EntityState.Detached)
        {
            _context.ProviderPayouts.Update(entity);
        }

        await _context.SaveChangesAsync();
    }

    public Task<ProviderPayout?> GetByIdAsync(Guid id) =>
        _context.ProviderPayouts.FirstOrDefaultAsync(p => p.Id == id);

    public async Task<IReadOnlyList<ProviderPayout>> ListByProviderAsync(Guid providerId) =>
        await _context.ProviderPayouts
            .Where(p => p.ProviderId == providerId)
            .OrderByDescending(p => p.PeriodStart)
            .ToListAsync();

    public async Task<(IReadOnlyList<ProviderPayout> Rows, int TotalCount)> SearchAsync(Guid? providerId, ProviderPayoutStatus? status, int page, int pageSize)
    {
        var query = _context.ProviderPayouts.AsQueryable();

        if (providerId.HasValue)
        {
            query = query.Where(p => p.ProviderId == providerId.Value);
        }

        if (status.HasValue)
        {
            query = query.Where(p => p.Status == status.Value);
        }

        int totalCount = await query.CountAsync();

        var rows = await query
            .OrderByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (rows, totalCount);
    }
}
