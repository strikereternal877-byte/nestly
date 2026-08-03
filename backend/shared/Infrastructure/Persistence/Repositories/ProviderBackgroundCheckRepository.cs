using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class ProviderBackgroundCheckRepository : IProviderBackgroundCheckRepository
{
    private readonly NestlyDbContext _context;

    public ProviderBackgroundCheckRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(ProviderBackgroundCheck entity)
    {
        await _context.ProviderBackgroundChecks.AddAsync(entity);
        await _context.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<ProviderBackgroundCheck>> ListByProviderAsync(Guid providerId) =>
        await _context.ProviderBackgroundChecks
            .Where(c => c.ProviderId == providerId)
            .OrderByDescending(c => c.CheckedAt)
            .ToListAsync();

    public Task<ProviderBackgroundCheck?> GetLatestByProviderAsync(Guid providerId) =>
        _context.ProviderBackgroundChecks
            .Where(c => c.ProviderId == providerId)
            .OrderByDescending(c => c.CheckedAt)
            .FirstOrDefaultAsync();
}
