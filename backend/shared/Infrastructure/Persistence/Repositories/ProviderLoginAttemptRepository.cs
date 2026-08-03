using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class ProviderLoginAttemptRepository : IProviderLoginAttemptRepository
{
    private readonly NestlyDbContext _context;

    public ProviderLoginAttemptRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(ProviderLoginAttempt entity)
    {
        await _context.Set<ProviderLoginAttempt>().AddAsync(entity);
        await _context.SaveChangesAsync();
    }

    public Task<int> CountFailuresSinceAsync(string identifier, DateTime sinceUtc) =>
        _context.Set<ProviderLoginAttempt>()
            .CountAsync(a => a.Identifier == identifier && !a.Succeeded && a.OccurredAtUtc >= sinceUtc);
}
