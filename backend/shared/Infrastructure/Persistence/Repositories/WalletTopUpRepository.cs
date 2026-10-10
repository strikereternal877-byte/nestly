using Microsoft.EntityFrameworkCore;
using Nestly.Application.Wallet;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class WalletTopUpRepository : IWalletTopUpRepository
{
    private readonly NestlyDbContext _context;

    public WalletTopUpRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(WalletTopUp topUp)
    {
        await _context.WalletTopUps.AddAsync(topUp);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(WalletTopUp topUp)
    {
        // Only attach+mark-modified when not already tracked by this context - see the identical comment in
        // BookingRepository.UpdateAsync.
        if (_context.Entry(topUp).State == EntityState.Detached)
        {
            _context.WalletTopUps.Update(topUp);
        }

        await _context.SaveChangesAsync();
    }

    public Task<WalletTopUp?> GetByIdAsync(Guid id) =>
        _context.WalletTopUps.FirstOrDefaultAsync(t => t.Id == id);

    public Task<WalletTopUp?> GetByGatewayOrderIdAsync(string gatewayOrderId) =>
        _context.WalletTopUps.FirstOrDefaultAsync(t => t.GatewayOrderId == gatewayOrderId);

    public Task<WalletTopUp?> FindRecentPendingAsync(Guid customerId, decimal amount, DateTime createdAfterUtc) =>
        _context.WalletTopUps
            .Where(t => t.CustomerId == customerId
                && t.Status == WalletTopUpStatus.Pending
                && t.Amount == amount
                && t.CreatedAtUtc >= createdAfterUtc)
            .OrderByDescending(t => t.CreatedAtUtc)
            .FirstOrDefaultAsync();

    public Task<int> CountCreatedSinceAsync(Guid customerId, DateTime sinceUtc) =>
        _context.WalletTopUps.CountAsync(t => t.CustomerId == customerId && t.CreatedAtUtc >= sinceUtc);

    public async Task<decimal> SumRecentPendingAsync(Guid customerId, DateTime createdAfterUtc) =>
        // Summed client-side: SQLite (used by the test database) cannot SUM a decimal column in SQL, and the set
        // is at most a handful of rows (the daily limit caps it).
        (await _context.WalletTopUps
            .Where(t => t.CustomerId == customerId
                && t.Status == WalletTopUpStatus.Pending
                && t.CreatedAtUtc >= createdAfterUtc)
            .Select(t => t.Amount)
            .ToListAsync())
        .Sum();

    public async Task<IReadOnlyList<WalletTopUp>> ListPendingBetweenAsync(DateTime newerThanUtc, DateTime olderThanUtc, int take) =>
        await _context.WalletTopUps
            .Where(t => t.Status == WalletTopUpStatus.Pending
                && t.CreatedAtUtc >= newerThanUtc
                && t.CreatedAtUtc < olderThanUtc)
            .OrderBy(t => t.CreatedAtUtc)
            .Take(take)
            .ToListAsync();

    public async Task<bool> TryMarkResolvedAsync(Guid id, WalletTopUpStatus newStatus)
    {
        // The conditional-ExecuteUpdateAsync idiom (PaymentTransactionRepository.TryMarkAttemptResolvedAsync):
        // the WHERE is re-evaluated against the committed row, so of two concurrent deliveries only one affects it.
        // A Success may also replace a Failed (a late payment after a write-off); a Failed never replaces a Success.
        int affected = newStatus == WalletTopUpStatus.Success
            ? await _context.WalletTopUps
                .Where(t => t.Id == id && (t.Status == WalletTopUpStatus.Pending || t.Status == WalletTopUpStatus.Failed))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, newStatus)
                    .SetProperty(t => t.CompletedAtUtc, DateTime.UtcNow))
            : await _context.WalletTopUps
                .Where(t => t.Id == id && t.Status == WalletTopUpStatus.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, newStatus)
                    .SetProperty(t => t.CompletedAtUtc, DateTime.UtcNow));

        return affected == 1;
    }
}
