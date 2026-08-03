using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class ProviderEarningLedgerRepository : IProviderEarningLedgerRepository
{
    private readonly NestlyDbContext _context;

    public ProviderEarningLedgerRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(ProviderEarningLedgerEntry entry)
    {
        await _context.ProviderEarningLedgerEntries.AddAsync(entry);
        await _context.SaveChangesAsync();
    }

    public Task<ProviderEarningLedgerEntry?> GetLatestAsync(Guid providerId) =>
        _context.ProviderEarningLedgerEntries
            .Where(e => e.ProviderId == providerId)
            .OrderByDescending(e => e.CreatedAtUtc)
            .FirstOrDefaultAsync();

    public async Task<IReadOnlyList<ProviderEarningLedgerEntry>> ListByProviderAsync(Guid providerId) =>
        await _context.ProviderEarningLedgerEntries
            .Where(e => e.ProviderId == providerId)
            .OrderByDescending(e => e.CreatedAtUtc)
            .ToListAsync();

    public async Task<IReadOnlyList<ProviderEarningLedgerEntry>> ListByProviderAndPeriodAsync(Guid providerId, DateOnly periodStart, DateOnly periodEnd)
    {
        var startUtc = periodStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var endUtc = periodEnd.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);

        return await _context.ProviderEarningLedgerEntries
            .Where(e => e.ProviderId == providerId && e.CreatedAtUtc >= startUtc && e.CreatedAtUtc <= endUtc)
            .OrderBy(e => e.CreatedAtUtc)
            .ToListAsync();
    }

    // Sums client-side over just the Amount column (not SumAsync) - SQLite's
    // EF provider (this repo's test suite) cannot translate a SQL-side Sum
    // over decimal, only Postgres can; mirrors WalletLedgerRepository's
    // equivalent method for the same reason.
    public async Task<decimal> SumCreditsBySourceTypeInRangeAsync(Guid providerId, ProviderEarningSourceType sourceType, DateTime fromUtc, DateTime toUtc) =>
        (await _context.ProviderEarningLedgerEntries
            .Where(e => e.ProviderId == providerId
                && e.SourceType == sourceType
                && e.EntryType == ProviderEarningEntryType.Credit
                && e.CreatedAtUtc >= fromUtc && e.CreatedAtUtc < toUtc)
            .Select(e => e.Amount)
            .ToListAsync())
            .Sum();

    public Task<ProviderEarningLedgerEntry?> FindBySourceAsync(ProviderEarningSourceType sourceType, Guid sourceReferenceId) =>
        _context.ProviderEarningLedgerEntries
            .FirstOrDefaultAsync(e => e.SourceType == sourceType && e.SourceReferenceId == sourceReferenceId);

    public async Task<decimal> SumBySourceTypeInRangeAsync(ProviderEarningSourceType sourceType, ProviderEarningEntryType entryType, DateTime fromUtc, DateTime toUtc) =>
        (await _context.ProviderEarningLedgerEntries
            .Where(e => e.SourceType == sourceType
                && e.EntryType == entryType
                && e.CreatedAtUtc >= fromUtc && e.CreatedAtUtc < toUtc)
            .Select(e => e.Amount)
            .ToListAsync())
            .Sum();
}
