using Microsoft.EntityFrameworkCore;
using Nestly.Application.Reschedules;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class BookingRescheduleRepository : IRescheduleRepository
{
    private readonly NestlyDbContext _context;

    public BookingRescheduleRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(BookingReschedule reschedule)
    {
        await _context.BookingReschedules.AddAsync(reschedule);
        await _context.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<BookingReschedule>> ListByBookingAsync(Guid bookingId) =>
        await _context.BookingReschedules
            .Where(r => r.BookingId == bookingId)
            .OrderByDescending(r => r.CreatedAtUtc)
            .ToListAsync();

    public Task<int> CountByBookingAsync(Guid bookingId) =>
        _context.BookingReschedules.CountAsync(r => r.BookingId == bookingId);

    // Summed in memory: a booking has at most a handful of reschedules, and the SQLite provider the test suite runs on
    // cannot aggregate a decimal column in SQL.
    public async Task<decimal> SumCollectedFeesAsync(Guid bookingId) =>
        (await _context.BookingReschedules
            .Where(r => r.BookingId == bookingId && r.FeeCollectedAmount > 0)
            .Select(r => r.FeeCollectedAmount)
            .ToListAsync())
        .Sum();
}
