using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class AdminSessionRepository : IAdminSessionRepository
{
    private readonly NestlyDbContext _context;

    public AdminSessionRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(AdminSession entity)
    {
        await _context.Set<AdminSession>().AddAsync(entity);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(AdminSession entity)
    {
        _context.Set<AdminSession>().Update(entity);
        await _context.SaveChangesAsync();
    }

    public Task<AdminSession?> GetByRefreshTokenHashAsync(string refreshTokenHash) =>
        _context.Set<AdminSession>().FirstOrDefaultAsync(s => s.RefreshTokenHash == refreshTokenHash);
}
