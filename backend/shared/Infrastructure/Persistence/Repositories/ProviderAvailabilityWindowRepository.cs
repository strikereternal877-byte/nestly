using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class ProviderAvailabilityWindowRepository : IProviderAvailabilityWindowRepository
{
    private readonly NestlyDbContext _context;

    public ProviderAvailabilityWindowRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<ProviderAvailabilityWindow>> GetByProviderAsync(Guid providerId)
    {
        // Ordered client-side: SQLite (used by the test suite) cannot
        // translate an ORDER BY over a TimeSpan column, and PostgreSQL's
        // "interval" ordering would differ subtly enough from .NET's
        // TimeSpan comparison that doing it once, consistently, in memory is
        // simpler than relying on the provider.
        var windows = await _context.Set<ProviderAvailabilityWindow>()
            .Where(x => x.ProviderId == providerId)
            .ToListAsync();

        return windows.OrderBy(x => x.DayOfWeek).ThenBy(x => x.StartTime).ToList();
    }

    public async Task ReplaceForProviderAsync(Guid providerId, IReadOnlyList<ProviderAvailabilityWindow> windows)
    {
        var existing = await _context.Set<ProviderAvailabilityWindow>()
            .Where(x => x.ProviderId == providerId)
            .ToListAsync();
        _context.Set<ProviderAvailabilityWindow>().RemoveRange(existing);

        await _context.Set<ProviderAvailabilityWindow>().AddRangeAsync(windows);

        await _context.SaveChangesAsync();
    }
}
