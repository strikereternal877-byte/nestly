using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class ProviderBankAccountRepository : IProviderBankAccountRepository
{
    private readonly NestlyDbContext _context;

    public ProviderBankAccountRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public Task<ProviderBankAccount?> GetByProviderIdAsync(Guid providerId) =>
        _context.Set<ProviderBankAccount>().FirstOrDefaultAsync(x => x.ProviderId == providerId);

    public Task<ProviderBankAccount?> GetByIdAsync(Guid id) =>
        _context.Set<ProviderBankAccount>().FirstOrDefaultAsync(x => x.Id == id);

    public async Task AddAsync(ProviderBankAccount entity)
    {
        await _context.Set<ProviderBankAccount>().AddAsync(entity);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(ProviderBankAccount entity)
    {
        if (_context.Entry(entity).State == EntityState.Detached)
        {
            _context.Set<ProviderBankAccount>().Update(entity);
        }

        await _context.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<ProviderBankAccount>> ListPendingAsync(CancellationToken cancellationToken = default) =>
        await _context.Set<ProviderBankAccount>()
            .AsNoTracking()
            .Where(x => x.VerificationStatus == ProviderBankAccountVerificationStatus.Pending)
            .OrderBy(x => x.UpdatedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, ProviderBankAccount>> GetByProviderIdsAsync(IReadOnlyCollection<Guid> providerIds)
    {
        if (providerIds.Count == 0)
        {
            return new Dictionary<Guid, ProviderBankAccount>();
        }

        return await _context.Set<ProviderBankAccount>()
            .AsNoTracking()
            .Where(x => providerIds.Contains(x.ProviderId))
            .ToDictionaryAsync(x => x.ProviderId);
    }
}
