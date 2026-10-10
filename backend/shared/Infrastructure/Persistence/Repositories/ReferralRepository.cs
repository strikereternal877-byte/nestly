using Microsoft.EntityFrameworkCore;
using Nestly.Application.Referral;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class ReferralRepository : IReferralRepository
{
    private readonly NestlyDbContext _context;

    public ReferralRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public Task<Referral?> GetByIdAsync(Guid id) =>
        _context.Referrals.FirstOrDefaultAsync(r => r.Id == id);

    public Task<Referral?> GetByRefereeCustomerIdAsync(Guid refereeCustomerId) =>
        _context.Referrals.FirstOrDefaultAsync(r => r.RefereeCustomerId == refereeCustomerId);

    public Task<Referral?> GetByQualifyingBookingIdAsync(Guid bookingId) =>
        _context.Referrals.FirstOrDefaultAsync(r => r.QualifyingBookingId == bookingId);

    public async Task<IReadOnlyList<Referral>> ListByReferrerCustomerIdAsync(Guid referrerCustomerId) =>
        await _context.Referrals
            .Where(r => r.ReferrerCustomerId == referrerCustomerId)
            .OrderByDescending(r => r.RegisteredAtUtc)
            .ToListAsync();

    public Task<int> CountRewardedByReferrerAsync(Guid referrerCustomerId) =>
        _context.Referrals.CountAsync(r =>
            r.ReferrerCustomerId == referrerCustomerId
            && r.Status == ReferralStatus.Rewarded
            && !_context.Bookings.Any(b => b.Id == r.QualifyingBookingId && b.Status == BookingStatus.Refunded));

    public async Task<IReadOnlyList<Referral>> ListExpiredAsync(DateTime asOfUtc) =>
        await _context.Referrals
            .Where(r => r.Status == ReferralStatus.Registered && r.ExpiresAtUtc <= asOfUtc)
            .ToListAsync();

    public async Task<(IReadOnlyList<Referral> Items, int TotalCount)> SearchAsync(
        ReferralStatus? status, bool? isFraudFlagged, IReadOnlyList<Guid>? customerIds, int page, int pageSize)
    {
        var query = _context.Referrals.AsQueryable();

        if (status.HasValue)
        {
            query = query.Where(r => r.Status == status.Value);
        }

        if (isFraudFlagged.HasValue)
        {
            query = query.Where(r => r.IsFraudFlagged == isFraudFlagged.Value);
        }

        if (customerIds is { Count: > 0 })
        {
            query = query.Where(r => customerIds.Contains(r.ReferrerCustomerId) || customerIds.Contains(r.RefereeCustomerId));
        }

        int totalCount = await query.CountAsync();
        var items = await query
            .OrderByDescending(r => r.RegisteredAtUtc)
            .ApplyPaging(page, pageSize)
            .ToListAsync();

        return (items, totalCount);
    }

    public async Task<IReadOnlyList<Referral>> ListRegisteredInRangeAsync(DateTime? fromUtc, DateTime? toUtc)
    {
        var query = _context.Referrals.AsQueryable();
        if (fromUtc.HasValue)
        {
            query = query.Where(r => r.RegisteredAtUtc >= fromUtc.Value);
        }

        if (toUtc.HasValue)
        {
            query = query.Where(r => r.RegisteredAtUtc <= toUtc.Value);
        }

        return await query.ToListAsync();
    }

    public async Task<IReadOnlyList<Referral>> ListRewardedInRangeAsync(DateTime? fromUtc, DateTime? toUtc)
    {
        var query = _context.Referrals.Where(r => r.Status == ReferralStatus.Rewarded && r.RewardedAtUtc != null);
        if (fromUtc.HasValue)
        {
            query = query.Where(r => r.RewardedAtUtc >= fromUtc.Value);
        }

        if (toUtc.HasValue)
        {
            query = query.Where(r => r.RewardedAtUtc <= toUtc.Value);
        }

        return await query.ToListAsync();
    }

    public async Task AddAsync(Referral referral)
    {
        await _context.Referrals.AddAsync(referral);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(Referral referral)
    {
        _context.Referrals.Update(referral);
        await _context.SaveChangesAsync();
    }

    public async Task<bool> TryMarkQualifiedAsync(Guid referralId, Guid qualifyingBookingId)
    {
        var nowUtc = DateTime.UtcNow;
        int affected = await _context.Referrals
            .Where(r => r.Id == referralId && r.Status == ReferralStatus.Registered)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.Status, ReferralStatus.Qualified)
                .SetProperty(r => r.QualifyingBookingId, qualifyingBookingId)
                .SetProperty(r => r.QualifiedAtUtc, nowUtc));

        return affected == 1;
    }
}
