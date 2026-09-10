using Microsoft.EntityFrameworkCore;
using Nestly.Application.Payments;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class PaymentTransactionRepository : IPaymentTransactionRepository
{
    private readonly NestlyDbContext _context;

    public PaymentTransactionRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(PaymentTransaction transaction)
    {
        await _context.PaymentTransactions.AddAsync(transaction);
        await _context.SaveChangesAsync();
    }

    public async Task<bool> TryAddAsync(PaymentTransaction transaction)
    {
        // BookingId's unique index (PaymentTransactionConfiguration) is what
        // actually makes this safe under concurrency (task 135b): two
        // requests that both read "no existing transaction yet" for the same
        // booking and both try to insert the first one can't both win here.
        await _context.PaymentTransactions.AddAsync(transaction);

        try
        {
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException)
        {
            // Lost the race - detach the failed entity (and its owned
            // PaymentAttempt child) so a later SaveChangesAsync on this same
            // request-scoped context doesn't try to re-submit it and throw
            // again. Mirrors SlotCapacityRepository.TryReserveAsync's
            // identical cleanup for the same reason.
            foreach (var entry in _context.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }

            return false;
        }
    }

    public async Task<bool> TryMarkAttemptResolvedAsync(Guid attemptId, PaymentAttemptStatus newStatus)
    {
        // NESTLY-006: the same conditional-ExecuteUpdateAsync idiom
        // SlotCapacityRepository.TryReserveAsync uses to close a capacity
        // race. The WHERE clause is re-evaluated against the committed row
        // at execution time, so of two concurrent webhook deliveries for the
        // same gateway order, only the first to reach the database can ever
        // affect a row here - the second finds Status already flipped and
        // affects zero.
        int affected = await _context.Set<PaymentAttempt>()
            .Where(a => a.Id == attemptId && a.Status == PaymentAttemptStatus.Created)
            .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.Status, newStatus));

        return affected == 1;
    }

    public async Task UpdateAsync(PaymentTransaction transaction)
    {
        // Only attach+mark-modified when not already tracked by this
        // context - see the identical comment in BookingRepository.UpdateAsync.
        // A same-context retry's newly-added PaymentAttempt is corrected by
        // NewOwnedChildEntityInterceptor, not here.
        if (_context.Entry(transaction).State == EntityState.Detached)
        {
            _context.PaymentTransactions.Update(transaction);
        }

        await _context.SaveChangesAsync();
    }

    public Task<PaymentTransaction?> GetByIdAsync(Guid id) =>
        FullyLoaded().FirstOrDefaultAsync(t => t.Id == id);

    public Task<PaymentTransaction?> GetByBookingIdAsync(Guid bookingId) =>
        FullyLoaded().FirstOrDefaultAsync(t => t.BookingId == bookingId);

    public Task<PaymentTransaction?> GetByGatewayOrderIdAsync(string gatewayOrderId) =>
        FullyLoaded().FirstOrDefaultAsync(t => t.Attempts.Any(a => a.GatewayOrderId == gatewayOrderId));

    public async Task<IReadOnlyList<PaymentTransaction>> ListAsync(DateTime? fromUtc, DateTime? toUtc, PaymentTransactionStatus? status)
    {
        var query = FullyLoaded();

        if (fromUtc is not null)
        {
            query = query.Where(t => t.CreatedAtUtc >= fromUtc.Value);
        }

        if (toUtc is not null)
        {
            query = query.Where(t => t.CreatedAtUtc <= toUtc.Value);
        }

        if (status is not null)
        {
            query = query.Where(t => t.Status == status.Value);
        }

        return await query.OrderByDescending(t => t.CreatedAtUtc).ToListAsync();
    }

    public async Task<(IReadOnlyList<PaymentTransaction> Rows, int TotalCount)> SearchAsync(
        Guid? bookingId, PaymentTransactionStatus? status, DateTime? fromUtc, DateTime? toUtc, int page, int pageSize)
    {
        var query = FullyLoaded();

        if (bookingId is not null)
        {
            query = query.Where(t => t.BookingId == bookingId.Value);
        }

        if (status is not null)
        {
            query = query.Where(t => t.Status == status.Value);
        }

        if (fromUtc is not null)
        {
            query = query.Where(t => t.CreatedAtUtc >= fromUtc.Value);
        }

        if (toUtc is not null)
        {
            query = query.Where(t => t.CreatedAtUtc <= toUtc.Value);
        }

        int totalCount = await query.CountAsync();

        var rows = await query
            .OrderByDescending(t => t.CreatedAtUtc)
            .ApplyPaging(page, pageSize)
            .ToListAsync();

        return (rows, totalCount);
    }

    public async Task<IReadOnlyList<PaymentCommissionSnapshot>> ListCommissionSnapshotsByBookingIdsAsync(IReadOnlyCollection<Guid> bookingIds)
    {
        if (bookingIds.Count == 0)
        {
            return [];
        }

        // No FullyLoaded()/Include(Attempts) here on purpose - same reasoning
        // as BookingRepository.ListSummariesByIdsAsync: a page of jobs only
        // needs these four columns per booking, not every gateway attempt.
        return await _context.PaymentTransactions
            .AsNoTracking()
            .Where(t => bookingIds.Contains(t.BookingId))
            .Select(t => new PaymentCommissionSnapshot(t.BookingId, t.Amount, t.CommissionRatePercentage, t.CommissionAmount))
            .ToListAsync();
    }

    public async Task<IReadOnlyList<PaymentTransaction>> ListByBookingIdsAsync(IReadOnlyCollection<Guid> bookingIds)
    {
        if (bookingIds.Count == 0)
        {
            return [];
        }

        return await FullyLoaded().Where(t => bookingIds.Contains(t.BookingId)).ToListAsync();
    }

    private IQueryable<PaymentTransaction> FullyLoaded() =>
        _context.PaymentTransactions.Include(t => t.Attempts);
}
