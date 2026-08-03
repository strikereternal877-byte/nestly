using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class ProviderBlackoutDateRepository : IProviderBlackoutDateRepository
{
    private readonly NestlyDbContext _context;

    public ProviderBlackoutDateRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<ProviderBlackoutDate>> GetByProviderAsync(Guid providerId) =>
        await _context.Set<ProviderBlackoutDate>()
            .Where(x => x.ProviderId == providerId)
            .OrderByDescending(x => x.StartDate)
            .ToListAsync();

    public Task<ProviderBlackoutDate?> GetByIdAsync(Guid id) =>
        _context.Set<ProviderBlackoutDate>().FirstOrDefaultAsync(x => x.Id == id);

    public async Task AddAsync(ProviderBlackoutDate entity)
    {
        await _context.Set<ProviderBlackoutDate>().AddAsync(entity);
        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(ProviderBlackoutDate entity)
    {
        _context.Set<ProviderBlackoutDate>().Remove(entity);
        await _context.SaveChangesAsync();
    }
}
