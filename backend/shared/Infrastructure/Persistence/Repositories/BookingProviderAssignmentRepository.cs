using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class BookingProviderAssignmentRepository : IBookingProviderAssignmentRepository
{
    private readonly NestlyDbContext _context;

    public BookingProviderAssignmentRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(BookingProviderAssignment entity)
    {
        await _context.BookingProviderAssignments.AddAsync(entity);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(BookingProviderAssignment entity)
    {
        if (_context.Entry(entity).State == EntityState.Detached)
        {
            _context.BookingProviderAssignments.Update(entity);
        }

        await _context.SaveChangesAsync();
    }

    public Task<BookingProviderAssignment?> GetByIdAsync(Guid id) =>
        _context.BookingProviderAssignments.FirstOrDefaultAsync(a => a.Id == id);

    public Task<BookingProviderAssignment?> GetActiveByBookingAsync(Guid bookingId) =>
        _context.BookingProviderAssignments
            .Where(a => a.BookingId == bookingId &&
                (a.Status == BookingProviderAssignmentStatus.Assigned || a.Status == BookingProviderAssignmentStatus.Accepted))
            .OrderByDescending(a => a.AssignedAt)
            .FirstOrDefaultAsync();

    public async Task<IReadOnlyList<BookingProviderAssignment>> ListByBookingAsync(Guid bookingId) =>
        await _context.BookingProviderAssignments
            .Where(a => a.BookingId == bookingId)
            .OrderByDescending(a => a.AssignedAt)
            .ToListAsync();

    public async Task<IReadOnlyList<BookingProviderAssignment>> ListByProviderAsync(Guid providerId) =>
        await _context.BookingProviderAssignments
            .Where(a => a.ProviderId == providerId)
            .OrderByDescending(a => a.AssignedAt)
            .ToListAsync();
}
