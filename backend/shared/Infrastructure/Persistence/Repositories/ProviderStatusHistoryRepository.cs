using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class ProviderStatusHistoryRepository : IProviderStatusHistoryRepository
{
    private readonly NestlyDbContext _context;

    public ProviderStatusHistoryRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<ProviderStatusHistory>> ListByProviderAsync(Guid providerId) =>
        await _context.Set<ProviderStatusHistory>()
            .AsNoTracking()
            .Where(x => x.ProviderId == providerId)
            .OrderByDescending(x => x.ChangedAtUtc)
            .ToListAsync();
}
