using Microsoft.EntityFrameworkCore;
using Nestly.Application.Bookings;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class BookingCompletionProofRepository : IBookingCompletionProofRepository
{
    private readonly NestlyDbContext _context;

    public BookingCompletionProofRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(BookingCompletionProof proof)
    {
        await _context.BookingCompletionProofs.AddAsync(proof);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(BookingCompletionProof proof)
    {
        if (_context.Entry(proof).State == EntityState.Detached)
        {
            _context.BookingCompletionProofs.Update(proof);
        }

        await _context.SaveChangesAsync();
    }

    public Task<BookingCompletionProof?> GetByBookingIdAsync(Guid bookingId) =>
        _context.BookingCompletionProofs.FirstOrDefaultAsync(p => p.BookingId == bookingId);

    public Task<bool> ExistsForBookingAsync(Guid bookingId) =>
        _context.BookingCompletionProofs.AnyAsync(p => p.BookingId == bookingId);

    public async Task<IReadOnlyList<BookingCompletionProof>> ListPendingAsync(CancellationToken cancellationToken = default) =>
        await _context.BookingCompletionProofs
            .AsNoTracking()
            .Where(p => p.ReviewStatus == CompletionProofReviewStatus.Pending)
            .OrderBy(p => p.SubmittedAtUtc)
            .ToListAsync(cancellationToken);
}
