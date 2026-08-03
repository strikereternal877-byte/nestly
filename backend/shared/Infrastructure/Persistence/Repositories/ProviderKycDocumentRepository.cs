using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class ProviderKycDocumentRepository : IProviderKycDocumentRepository
{
    private readonly NestlyDbContext _context;

    public ProviderKycDocumentRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(ProviderKycDocument entity)
    {
        await _context.Set<ProviderKycDocument>().AddAsync(entity);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(ProviderKycDocument entity)
    {
        _context.Set<ProviderKycDocument>().Update(entity);
        await _context.SaveChangesAsync();
    }

    public Task<ProviderKycDocument?> GetByIdAsync(Guid id) =>
        _context.Set<ProviderKycDocument>().FirstOrDefaultAsync(x => x.Id == id);

    public async Task<IReadOnlyList<ProviderKycDocument>> GetByProviderAsync(Guid providerId) =>
        await _context.Set<ProviderKycDocument>()
            .Where(x => x.ProviderId == providerId)
            .OrderByDescending(x => x.SubmittedAt)
            .ToListAsync();
}
