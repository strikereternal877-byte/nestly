using Microsoft.EntityFrameworkCore;
using Nestly.Application.Escrow;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class PlatformEscrowLedgerRepository : IPlatformEscrowLedgerRepository
{
    private readonly NestlyDbContext _context;

    public PlatformEscrowLedgerRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(PlatformEscrowLedger entry)
    {
        await _context.PlatformEscrowLedgers.AddAsync(entry);
        await _context.SaveChangesAsync();
    }

    public async Task<bool> TryAddCompletionReleaseAsync(PlatformEscrowLedger entry)
    {
        await _context.PlatformEscrowLedgers.AddAsync(entry);

        try
        {
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException)
        {
            // Lost the race against a concurrent completion of the same
            // booking - the filtered unique index on (BookingId) WHERE
            // Release/BookingCompleted rejected this insert. Detach rather
            // than leave it tracked as Added, mirroring RescheduleService's
            // own DetachPendingAssignmentWrites: a caller that goes on to
            // save this same DbContext again (the ambient transaction this
            // runs inside, per DomainEventDispatchInterceptor) must not keep
            // retrying an insert that will only ever fail the same way.
            _context.Entry(entry).State = EntityState.Detached;
            return false;
        }
    }

    public Task<PlatformEscrowLedger?> GetLatestAsync() =>
        _context.PlatformEscrowLedgers
            .OrderByDescending(e => e.CreatedAtUtc)
            .FirstOrDefaultAsync();

    public async Task<IReadOnlyList<PlatformEscrowLedger>> ListByBookingAsync(Guid bookingId) =>
        await _context.PlatformEscrowLedgers
            .Where(e => e.BookingId == bookingId)
            .OrderBy(e => e.CreatedAtUtc)
            .ToListAsync();
}
