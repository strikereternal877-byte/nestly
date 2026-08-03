using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class ProviderSessionRepository : IProviderSessionRepository
{
    private readonly NestlyDbContext _context;

    public ProviderSessionRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(ProviderSession entity)
    {
        await _context.Set<ProviderSession>().AddAsync(entity);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(ProviderSession entity)
    {
        _context.Set<ProviderSession>().Update(entity);
        await _context.SaveChangesAsync();
    }

    public Task<ProviderSession?> GetByRefreshTokenHashAsync(string refreshTokenHash) =>
        _context.Set<ProviderSession>().FirstOrDefaultAsync(s => s.RefreshTokenHash == refreshTokenHash);

    public async Task<int> RevokeAllForProviderAsync(Guid providerId)
    {
        var now = DateTime.UtcNow;

        var active = await _context.Set<ProviderSession>()
            .Where(s => s.ProviderId == providerId && s.RevokedAt == null && s.ExpiresAt > now)
            .ToListAsync();

        if (active.Count == 0)
        {
            return 0;
        }

        foreach (var session in active)
        {
            session.Revoke();
        }

        await _context.SaveChangesAsync();
        return active.Count;
    }
}
