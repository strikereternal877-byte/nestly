using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class ProviderServiceAreaRepository : IProviderServiceAreaRepository
{
    private readonly NestlyDbContext _context;

    public ProviderServiceAreaRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<ProviderServiceArea>> GetByProviderAsync(Guid providerId) =>
        await _context.Set<ProviderServiceArea>()
            .Where(x => x.ProviderId == providerId)
            .ToListAsync();

    public async Task ReplaceForProviderAsync(Guid providerId, IReadOnlyList<ProviderServiceArea> areas)
    {
        var existing = await _context.Set<ProviderServiceArea>()
            .Where(x => x.ProviderId == providerId)
            .ToListAsync();
        _context.Set<ProviderServiceArea>().RemoveRange(existing);

        await _context.Set<ProviderServiceArea>().AddRangeAsync(areas);

        await _context.SaveChangesAsync();
    }
}
