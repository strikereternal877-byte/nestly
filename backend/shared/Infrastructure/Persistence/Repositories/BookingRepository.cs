using Microsoft.EntityFrameworkCore;
using Nestly.Application.Bookings;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class BookingRepository : IBookingRepository
{
    private readonly NestlyDbContext _context;

    public BookingRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(Booking booking)
    {
        await _context.Bookings.AddAsync(booking);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(Booking booking)
    {
        // Only attach+mark-modified when the booking isn't already tracked
        // by this context - the common case (loaded via this same context,
        // e.g. Payment/Refund services that load, transition, and save
        // within one request-scoped context) needs no attach at all;
        // ordinary change detection handles its modified scalar properties
        // correctly on its own. A same-context TransitionTo() call also
        // appends a brand-new BookingStatusHistory row - see
        // NewOwnedChildEntityInterceptor for why that needs its own,
        // centralized correction rather than being handled here.
        if (_context.Entry(booking).State == EntityState.Detached)
        {
            _context.Bookings.Update(booking);
        }

        await _context.SaveChangesAsync();
    }

    public Task<Booking?> GetByIdAsync(Guid id) =>
        FullyLoaded().FirstOrDefaultAsync(b => b.Id == id);

    // AsNoTracking (task 136a): unlike GetByIdAsync above (which several
    // read-modify-write flows load through and then save back on this same
    // context), every call site of the three methods below is read-only
    // display/reporting - a customer's booking list, admin search, and a
    // provider's assigned-jobs view. None of them ever call UpdateAsync on
    // what they load.

    public async Task<IReadOnlyList<Booking>> ListByCustomerAsync(Guid customerId, IReadOnlyList<BookingStatus> statuses) =>
        await FullyLoaded()
            .AsNoTracking()
            .Where(b => b.CustomerId == customerId && statuses.Contains(b.Status))
            .OrderByDescending(b => b.CreatedAtUtc)
            .ToListAsync();

    /// <summary>
    /// Filterable admin search (SRS 12.11.1, task 115a). Count is taken on
    /// the un-included filter query, then the page is re-queried with
    /// <c>Items</c> attached - the same split CustomerRepository.SearchAsync
    /// uses, so pagination math is never skewed by a collection Include's
    /// row multiplication. Only <c>Items</c> is loaded (not AddOns/
    /// StatusHistory) - a list row only ever needs the service name off the
    /// first item; the detail endpoint's <see cref="GetByIdAsync"/> is what
    /// loads the full aggregate.
    ///
    /// String filters use ToLower()+Contains rather than Npgsql's ILike so
    /// the same LINQ translates on both the production Postgres provider and
    /// the SQLite provider the test suite runs against (see TestDatabase) -
    /// matching CustomerRepository.SearchAsync's documented reasoning.
    /// </summary>
    public async Task<BookingSearchResult> SearchAsync(BookingSearchFilter filter)
    {
        var query = _context.Bookings.AsQueryable();

        if (filter.BookingId.HasValue)
        {
            query = query.Where(b => b.Id == filter.BookingId.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.CustomerName))
        {
            string term = filter.CustomerName.ToLower();
            query = query.Where(b => b.CustomerNameSnapshot.ToLower().Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(filter.CustomerMobile))
        {
            string term = filter.CustomerMobile.ToLower();
            query = query.Where(b => b.CustomerMobileSnapshot.ToLower().Contains(term));
        }

        if (filter.Status.HasValue)
        {
            query = query.Where(b => b.Status == filter.Status.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.City))
        {
            string term = filter.City.ToLower();
            query = query.Where(b => b.AddressCitySnapshot.ToLower().Contains(term));
        }

        if (filter.SlotDateFrom.HasValue)
        {
            query = query.Where(b => b.SlotDate >= filter.SlotDateFrom.Value);
        }

        if (filter.SlotDateTo.HasValue)
        {
            query = query.Where(b => b.SlotDate <= filter.SlotDateTo.Value);
        }

        if (filter.CreatedFromUtc.HasValue)
        {
            query = query.Where(b => b.CreatedAtUtc >= filter.CreatedFromUtc.Value);
        }

        if (filter.CreatedToUtc.HasValue)
        {
            query = query.Where(b => b.CreatedAtUtc <= filter.CreatedToUtc.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.CouponCode))
        {
            string term = filter.CouponCode.ToLower();
            query = query.Where(b => b.CouponCodeSnapshot != null && b.CouponCodeSnapshot.ToLower().Contains(term));
        }

        if (filter.ServiceId.HasValue)
        {
            var serviceId = filter.ServiceId.Value;
            query = query.Where(b => b.Items.Any(i => i.ServiceId == serviceId));
        }

        if (filter.CategoryId.HasValue)
        {
            var categoryId = filter.CategoryId.Value;
            query = query.Where(b => b.Items.Any(i => _context.Set<Service>().Any(s => s.Id == i.ServiceId && s.CategoryId == categoryId)));
        }

        int totalCount = await query.CountAsync();

        var rows = await query
            .AsNoTracking()
            .Include(b => b.Items)
            .OrderByDescending(b => b.CreatedAtUtc)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync();

        return new BookingSearchResult(rows, totalCount);
    }

    /// <summary>Bookings currently assigned to a provider (task 150c performance view).</summary>
    public async Task<IReadOnlyList<Booking>> ListByAssignedProviderAsync(Guid providerId) =>
        await FullyLoaded()
            .AsNoTracking()
            .Where(b => b.AssignedProviderId == providerId)
            .OrderByDescending(b => b.CreatedAtUtc)
            .ToListAsync();

    public Task<int> CountCompletedByCustomerAsync(Guid customerId, Guid excludingBookingId) =>
        _context.Bookings.CountAsync(b =>
            b.CustomerId == customerId && b.Status == BookingStatus.Completed && b.Id != excludingBookingId);

    public Task<int> CountCompletedByAssignedProviderAsync(Guid providerId, Guid excludingBookingId) =>
        _context.Bookings.CountAsync(b =>
            b.AssignedProviderId == providerId && b.Status == BookingStatus.Completed && b.Id != excludingBookingId);

    private IQueryable<Booking> FullyLoaded() =>
        _context.Bookings
            .Include(b => b.Items).ThenInclude(i => i.AddOns)
            .Include(b => b.StatusHistory);
}
