using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class ProviderSkillMappingRepository : IProviderSkillMappingRepository
{
    private readonly NestlyDbContext _context;

    public ProviderSkillMappingRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<ProviderSkillMapping>> GetByProviderAsync(Guid providerId) =>
        await _context.Set<ProviderSkillMapping>()
            .Where(x => x.ProviderId == providerId)
            .ToListAsync();

    public async Task ReplaceForProviderAsync(Guid providerId, IReadOnlyList<ProviderSkillMapping> skills)
    {
        var existing = await _context.Set<ProviderSkillMapping>()
            .Where(x => x.ProviderId == providerId)
            .ToListAsync();
        _context.Set<ProviderSkillMapping>().RemoveRange(existing);

        await _context.Set<ProviderSkillMapping>().AddRangeAsync(skills);

        await _context.SaveChangesAsync();
    }
}
