using Microsoft.EntityFrameworkCore;
using Nestly.Application.RecurringBookings;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class RecurringBookingPlanRepository : IRecurringBookingPlanRepository
{
    private readonly NestlyDbContext _context;

    public RecurringBookingPlanRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(RecurringBookingPlan plan)
    {
        await _context.RecurringBookingPlans.AddAsync(plan);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(RecurringBookingPlan plan)
    {
        if (_context.Entry(plan).State == EntityState.Detached)
        {
            _context.RecurringBookingPlans.Update(plan);
        }

        await _context.SaveChangesAsync();
    }

    public Task<RecurringBookingPlan?> GetByIdAsync(Guid id) =>
        _context.RecurringBookingPlans
            .Include(p => p.AddOns)
            .FirstOrDefaultAsync(p => p.Id == id);

    public async Task<IReadOnlyList<RecurringBookingPlan>> ListByCustomerAsync(Guid customerId) =>
        await _context.RecurringBookingPlans
            .Include(p => p.AddOns)
            .AsNoTracking()
            .Where(p => p.CustomerId == customerId)
            .OrderByDescending(p => p.CreatedAtUtc)
            .ToListAsync();

    public async Task<IReadOnlyDictionary<Guid, RecurringBookingRecurrenceFrequency>> ListFrequenciesByIdsAsync(
        IReadOnlyCollection<Guid> planIds)
    {
        if (planIds.Count == 0)
        {
            return new Dictionary<Guid, RecurringBookingRecurrenceFrequency>();
        }

        // Two columns, one round trip, no add-ons - see the interface's doc
        // comment on why this is not GetByIdAsync in a loop.
        return await _context.RecurringBookingPlans
            .AsNoTracking()
            .Where(p => planIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Frequency })
            .ToDictionaryAsync(p => p.Id, p => p.Frequency);
    }

    public async Task<IReadOnlyList<RecurringBookingPlan>> ListDueAsync(DateOnly onOrBefore) =>
        await _context.RecurringBookingPlans
            .Include(p => p.AddOns)
            .Where(p => p.Status == RecurringBookingPlanStatus.Active && !p.PrepaidUpfront && p.NextOccurrenceDate <= onOrBefore)
            .OrderBy(p => p.NextOccurrenceDate)
            .ToListAsync();

    public Task<RecurringBookingPlan?> GetByPendingPrepaymentLeadAsync(Guid leadBookingId) =>
        _context.RecurringBookingPlans
            .Include(p => p.AddOns)
            .FirstOrDefaultAsync(p => p.PrepaidUpfront && p.PendingPrepaymentLeadBookingId == leadBookingId);

    public async Task<IReadOnlyList<RecurringBookingPlan>> ListPrepaidRenewalDueAsync(DateOnly coveredThroughOnOrBefore) =>
        await _context.RecurringBookingPlans
            .Include(p => p.AddOns)
            .Where(p => p.Status == RecurringBookingPlanStatus.Active
                && p.PrepaidUpfront
                && p.PendingPrepaymentLeadBookingId == null
                && p.EndDate == null && p.OccurrenceCount == null
                && p.PrepaidThroughDate != null && p.PrepaidThroughDate <= coveredThroughOnOrBefore)
            .OrderBy(p => p.PrepaidThroughDate)
            .ToListAsync();
}
