using Microsoft.EntityFrameworkCore;
using Nestly.Application.Coupons;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class CouponRepository : ICouponRepository
{
    private readonly NestlyDbContext _context;

    public CouponRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public Task<Coupon?> GetByIdAsync(Guid id) =>
        _context.Coupons.FirstOrDefaultAsync(c => c.Id == id);

    public Task<Coupon?> GetByCodeAsync(string code) =>
        _context.Coupons.FirstOrDefaultAsync(c => c.Code == code.Trim().ToUpper());

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<Guid, string>> GetCodesByIdsAsync(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        return await _context.Coupons
            .AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Code })
            .ToDictionaryAsync(c => c.Id, c => c.Code);
    }

    public async Task<bool> TryReserveRedemptionAsync(Guid couponId, Guid customerId)
    {
        // Both caps are claimed by conditional UPDATEs, never read-then-write
        // (task 72c, NESTLY-009). The per-customer cap deliberately does not
        // count CouponRedemption rows: those carry a required foreign key to
        // the booking, so they are written only *after* the booking is
        // persisted - well after this reservation runs. Every request in a
        // concurrent batch would therefore count zero and all of them would
        // pass a single-use-per-customer cap. The claim has to land on
        // something that exists now, so it lands on a per-(coupon, customer)
        // counter row, incremented under the same conditional-UPDATE
        // discipline SlotCapacityRepository uses for slot capacity.
        int? perCustomerLimit = await _context.Coupons
            .AsNoTracking()
            .Where(c => c.Id == couponId)
            .Select(c => c.UsageLimitPerCustomer)
            .FirstOrDefaultAsync();

        // Per-customer first: it is the cap a customer is most likely to hit,
        // and failing it costs nothing to unwind. The global claim below is
        // the one that may need compensating.
        if (perCustomerLimit is not null
            && !await TryReservePerCustomerAsync(couponId, customerId, perCustomerLimit.Value))
        {
            return false;
        }

        int affected = await _context.Coupons
            .Where(c => c.Id == couponId && (c.UsageLimitTotal == null || c.RedemptionCount < c.UsageLimitTotal))
            .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.RedemptionCount, c => c.RedemptionCount + 1));

        if (affected != 1 && perCustomerLimit is not null)
        {
            // The campaign-wide cap was already exhausted, so this booking
            // will not happen. Hand the customer's allowance back rather than
            // burning it on a redemption that never occurred - otherwise
            // losing this race would lock them out of a single-use coupon
            // permanently. Guarded on ReservedCount > 0 for the same reason
            // the increment is guarded: a single UPDATE that cannot be lost
            // to a concurrent one, and that can never go negative.
            await _context.Set<CouponCustomerRedemptionCounter>()
                .Where(c => c.CouponId == couponId && c.CustomerId == customerId && c.ReservedCount > 0)
                .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.ReservedCount, c => c.ReservedCount - 1));
        }

        return affected == 1;
    }

    /// <summary>
    /// Claims one unit of <paramref name="customerId"/>'s allowance on this
    /// coupon, creating the counter row on first use. Mirrors
    /// SlotCapacityRepository.TryReserveAsync: if two concurrent requests
    /// both race to create that first row, the unique index on
    /// (CouponId, CustomerId) lets exactly one INSERT win and the loser falls
    /// back to the conditional UPDATE it would have taken had the row already
    /// existed, so the outcome is still cap-correct.
    /// </summary>
    private async Task<bool> TryReservePerCustomerAsync(Guid couponId, Guid customerId, int limit)
    {
        if (await TryIncrementPerCustomerAsync(couponId, customerId, limit))
        {
            return true;
        }

        // ExecuteUpdateAsync can't tell "no row yet" apart from "row exists
        // and is at the cap" - both match zero rows. Assume the optimistic
        // case (this customer has never used this coupon) and try to create
        // the counter. A limit of zero has no optimistic case to try.
        if (limit < 1)
        {
            return false;
        }

        try
        {
            _context.Set<CouponCustomerRedemptionCounter>()
                .Add(new CouponCustomerRedemptionCounter(Guid.NewGuid(), couponId, customerId, 1));
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException)
        {
            // Lost the race to create the row - another request's insert
            // already committed (or the row genuinely existed and was at the
            // cap all along). Detach the failed entity so it isn't
            // re-submitted by a later SaveChangesAsync on this same
            // request-scoped context, then re-check against the real row.
            foreach (var entry in _context.ChangeTracker.Entries<CouponCustomerRedemptionCounter>().ToList())
            {
                if (entry.State == EntityState.Added)
                {
                    entry.State = EntityState.Detached;
                }
            }

            return await TryIncrementPerCustomerAsync(couponId, customerId, limit);
        }
    }

    private async Task<bool> TryIncrementPerCustomerAsync(Guid couponId, Guid customerId, int limit)
    {
        int affected = await _context.Set<CouponCustomerRedemptionCounter>()
            .Where(c => c.CouponId == couponId && c.CustomerId == customerId && c.ReservedCount < limit)
            .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.ReservedCount, c => c.ReservedCount + 1));

        return affected == 1;
    }

    public async Task ReleaseRedemptionAsync(Guid couponId, Guid customerId)
    {
        await _context.Coupons
            .Where(c => c.Id == couponId && c.RedemptionCount > 0)
            .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.RedemptionCount, c => c.RedemptionCount - 1));

        await _context.Set<CouponCustomerRedemptionCounter>()
            .Where(c => c.CouponId == couponId && c.CustomerId == customerId && c.ReservedCount > 0)
            .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.ReservedCount, c => c.ReservedCount - 1));
    }

    public async Task AddAsync(Coupon coupon)
    {
        await _context.Coupons.AddAsync(coupon);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(Coupon coupon)
    {
        _context.Coupons.Update(coupon);
        await _context.SaveChangesAsync();
    }

    public Task<bool> CodeExistsAsync(string code) =>
        _context.Coupons.AnyAsync(c => c.Code == code.Trim().ToUpper());

    public async Task<CouponSearchResult> SearchAsync(CouponSearchFilter filter)
    {
        var query = _context.Coupons.AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Code))
        {
            string term = filter.Code.Trim().ToUpper();
            query = query.Where(c => c.Code.Contains(term));
        }

        if (filter.IsActive.HasValue)
        {
            query = query.Where(c => c.IsActive == filter.IsActive.Value);
        }

        if (filter.DiscountType.HasValue)
        {
            query = query.Where(c => c.DiscountType == filter.DiscountType.Value);
        }

        if (filter.CustomerSegment.HasValue)
        {
            query = query.Where(c => c.CustomerSegment == filter.CustomerSegment.Value);
        }

        if (filter.ApplicableCategoryId.HasValue)
        {
            query = query.Where(c => c.ApplicableCategoryId == filter.ApplicableCategoryId.Value);
        }

        if (filter.ValidOnUtc.HasValue)
        {
            query = query.Where(c => c.ValidFromUtc <= filter.ValidOnUtc.Value && c.ValidToUtc >= filter.ValidOnUtc.Value);
        }

        int totalCount = await query.CountAsync();

        var page = await query
            .OrderByDescending(c => c.CreatedAtUtc)
            .ApplyPaging(filter.Page, filter.PageSize)
            .ToListAsync();

        // Category names resolved in one batch query rather than per-row -
        // same N+1 avoidance CategoryCityMappingRepository.ListAsync gets for
        // free from its join, done as a separate lookup here since a
        // coupon's applicable category is optional (a plain join would drop
        // uncategorized coupons, or need a LEFT JOIN Postgres/SQLite both
        // translate less predictably than a Contains lookup).
        var categoryIds = page
            .Where(c => c.ApplicableCategoryId.HasValue)
            .Select(c => c.ApplicableCategoryId!.Value)
            .Distinct()
            .ToList();

        var categoryNames = await _context.Set<Category>()
            .Where(category => categoryIds.Contains(category.Id))
            .ToDictionaryAsync(category => category.Id, category => category.Name);

        var items = page.Select(c => ToAdminResponse(c, categoryNames)).ToList();
        return new CouponSearchResult(items, totalCount);
    }

    /// <summary>Shared with <see cref="Nestly.Infrastructure.Services.CouponManagementService"/> for single-coupon reads that don't need a batch lookup.</summary>
    internal static CouponAdminResponse ToAdminResponse(Coupon coupon, IReadOnlyDictionary<Guid, string> categoryNames) =>
        new(
            coupon.Id,
            coupon.Code,
            coupon.Description,
            coupon.DiscountType,
            coupon.DiscountValue,
            coupon.MaxDiscountAmount,
            coupon.MinOrderAmount,
            coupon.ValidFromUtc,
            coupon.ValidToUtc,
            coupon.IsActive,
            coupon.UsageLimitTotal,
            coupon.UsageLimitPerCustomer,
            coupon.RedemptionCount,
            coupon.ApplicableCategoryId,
            coupon.ApplicableCategoryId.HasValue && categoryNames.TryGetValue(coupon.ApplicableCategoryId.Value, out string? name) ? name : null,
            coupon.CustomerSegment,
            coupon.CreatedAtUtc);
}
