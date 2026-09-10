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

    /// <summary>Task 241: mirrors PaymentTransactionRepository.TryAddAsync - the unique index on IdempotencyKey (BookingConfiguration) is what actually makes this race-safe, not this catch block by itself.</summary>
    public async Task<bool> TryAddAsync(Booking booking)
    {
        await _context.Bookings.AddAsync(booking);

        try
        {
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException)
        {
            foreach (var entry in _context.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }

            return false;
        }
    }

    public Task<Booking?> GetByIdempotencyKeyAsync(Guid customerId, string idempotencyKey) =>
        FullyLoaded().FirstOrDefaultAsync(b => b.CustomerId == customerId && b.IdempotencyKey == idempotencyKey);

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

    /// <inheritdoc/>
    public void DiscardChanges(Booking booking)
    {
        // The status-history rows go first and by booking id rather than
        // through the navigation: a transition that failed appended a fresh
        // BookingStatusHistory the collection may not even have been loaded to
        // hold, and detaching the parent alone would leave that orphan tracked
        // and INSERT it on the next unrelated save.
        foreach (var history in _context.ChangeTracker.Entries<BookingStatusHistory>()
                     .Where(entry => entry.Entity.BookingId == booking.Id)
                     .ToList())
        {
            history.State = EntityState.Detached;
        }

        // Also drops the aggregate's unpublished domain events with it, which
        // is exactly right: nothing should react to a transition that was
        // never persisted.
        _context.Entry(booking).State = EntityState.Detached;
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
    /// Paged like <see cref="SearchAsync"/>: counts on the bare filter query, then
    /// re-queries the page with only <c>Items</c> attached. <see cref="FullyLoaded"/>
    /// also brings in Items.AddOns and StatusHistory, which a list row's
    /// <c>ToListItem</c> mapping never reads - paging a query with two collection
    /// Includes made every page load pull and multiply the full AddOns/StatusHistory
    /// rows for each booking before EF could apply Skip/Take, which is what made this
    /// endpoint slow.
    /// </summary>
    public async Task<(IReadOnlyList<Booking> Rows, int TotalCount)> ListByCustomerPagedAsync(Guid customerId, IReadOnlyList<BookingStatus> statuses, int page, int pageSize)
    {
        var filtered = _context.Bookings
            .AsNoTracking()
            .Where(b => b.CustomerId == customerId && statuses.Contains(b.Status));

        int totalCount = await filtered.CountAsync();

        var rows = await filtered
            .Include(b => b.Items)
            .OrderByDescending(b => b.CreatedAtUtc)
            .ApplyPaging(page, pageSize)
            .ToListAsync();

        return (rows, totalCount);
    }

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

        // Substring, not exact match: an admin pasting from a support chat or
        // reading it off a screenshot may only have part of it, or extra
        // whitespace around it - same tolerance CustomerName/CustomerMobile
        // already get below.
        if (!string.IsNullOrWhiteSpace(filter.Reference))
        {
            string term = filter.Reference.Trim().ToLower();
            query = query.Where(b => b.BookingReference.ToLower().Contains(term));
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
            .ApplyPaging(filter.Page, filter.PageSize)
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

    public async Task<IReadOnlyList<Booking>> ListByRecurringPlanAsync(Guid recurringBookingPlanId) =>
        await FullyLoaded()
            .AsNoTracking()
            .Where(b => b.RecurringBookingPlanId == recurringBookingPlanId)
            .OrderByDescending(b => b.SlotDate)
            .ToListAsync();

    public Task<int> CountCompletedByCustomerAsync(Guid customerId, Guid excludingBookingId) =>
        _context.Bookings.CountAsync(b =>
            b.CustomerId == customerId && b.Status == BookingStatus.Completed && b.Id != excludingBookingId);

    public Task<int> CountCompletedByAssignedProviderAsync(Guid providerId, Guid excludingBookingId) =>
        _context.Bookings.CountAsync(b =>
            b.AssignedProviderId == providerId && b.Status == BookingStatus.Completed && b.Id != excludingBookingId);

    /// <summary>Task 240: BookingExpirySweepJob's candidate set - not AsNoTracking, since the job transitions and saves each row it loads here.</summary>
    public async Task<IReadOnlyList<Booking>> ListStalePaymentPendingAsync(DateTime olderThanUtc) =>
        await FullyLoaded()
            .Where(b => b.Status == BookingStatus.PaymentPending && b.CreatedAtUtc < olderThanUtc)
            .ToListAsync();

    /// <inheritdoc/>
    /// <remarks>
    /// Tracked (the job transitions and saves what it loads) but deliberately
    /// NOT <see cref="FullyLoaded"/>, unlike
    /// <see cref="ListStalePaymentPendingAsync"/>: this query is paged, and
    /// applying Skip/Take over two collection Includes makes the database
    /// multiply out every add-on and history row before the window can be
    /// applied - the exact cost documented on
    /// <see cref="ListByCustomerPagedAsync"/>. A status transition reads
    /// neither collection, and the fresh <see cref="BookingStatusHistory"/> row
    /// it appends is inserted correctly without the collection being loaded
    /// (see <c>NewOwnedChildEntityInterceptor</c>).
    ///
    /// <para>
    /// <c>(status, slot_date)</c> is exactly the composite index task 333 added
    /// to <c>booking</c>, and it serves the predicate and the ordering both.
    /// The tie-break on <c>Id</c> is not cosmetic: <c>Skip</c> only pages
    /// correctly over a total order, and slot date alone is not one.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Booking>> ListConfirmedDueForFulfilmentAsync(DateOnly onOrAfterSlotDate, DateOnly onOrBeforeSlotDate, int skip, int take) =>
        await _context.Bookings
            .Where(b => b.Status == BookingStatus.Confirmed
                && b.SlotDate >= onOrAfterSlotDate
                && b.SlotDate <= onOrBeforeSlotDate)
            .OrderBy(b => b.SlotDate)
            .ThenBy(b => b.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync();

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Booking>> ListSummariesByIdsAsync(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        // No FullyLoaded() here on purpose - see the interface's doc comment.
        // Pulling items/add-ons/status history for a whole page of bookings
        // just to render snapshot columns is what made the per-row version
        // expensive in the first place.
        return await _context.Bookings
            .AsNoTracking()
            .Where(b => ids.Contains(b.Id))
            .ToListAsync();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Guid>> ListServiceIdsEverBookedAsync() =>
        await _context.BookingItems
            .AsNoTracking()
            .Select(i => i.ServiceId)
            .Distinct()
            .ToListAsync();

    private IQueryable<Booking> FullyLoaded() =>
        _context.Bookings
            .Include(b => b.Items).ThenInclude(i => i.AddOns)
            .Include(b => b.StatusHistory);
}
