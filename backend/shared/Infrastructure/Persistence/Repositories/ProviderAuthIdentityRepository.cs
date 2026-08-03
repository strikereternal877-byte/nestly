using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class ProviderAuthIdentityRepository : IProviderAuthIdentityRepository
{
    private readonly NestlyDbContext _context;

    public ProviderAuthIdentityRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(ProviderAuthIdentity entity)
    {
        await _context.Set<ProviderAuthIdentity>().AddAsync(entity);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(ProviderAuthIdentity entity)
    {
        _context.Set<ProviderAuthIdentity>().Update(entity);
        await _context.SaveChangesAsync();
    }

    public Task<ProviderAuthIdentity?> GetByProviderAsync(AuthProviderType provider, string identifier) =>
        _context.Set<ProviderAuthIdentity>()
            .FirstOrDefaultAsync(x => x.Provider == provider && x.Identifier == identifier);

    public async Task<IReadOnlyList<ProviderAuthIdentity>> GetByProviderAsync(Guid providerId) =>
        await _context.Set<ProviderAuthIdentity>()
            .Where(x => x.ProviderId == providerId)
            .ToListAsync();

    public Task<bool> ExistsAsync(AuthProviderType provider, string identifier) =>
        _context.Set<ProviderAuthIdentity>().AnyAsync(x => x.Provider == provider && x.Identifier == identifier);
}
