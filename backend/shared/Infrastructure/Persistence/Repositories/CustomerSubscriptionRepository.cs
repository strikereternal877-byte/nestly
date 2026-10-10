using Microsoft.EntityFrameworkCore;
using Nestly.Application.Subscriptions;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class CustomerSubscriptionRepository : ICustomerSubscriptionRepository
{
    private static readonly CustomerSubscriptionStatus[] LiveStatuses =
    [
        CustomerSubscriptionStatus.Active,
        CustomerSubscriptionStatus.PaymentFailed
    ];

    private readonly NestlyDbContext _context;

    public CustomerSubscriptionRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public Task<CustomerSubscription?> GetByIdAsync(Guid id) =>
        _context.CustomerSubscriptions.FirstOrDefaultAsync(s => s.Id == id);

    public Task<CustomerSubscription?> GetCurrentByCustomerAsync(Guid customerId) =>
        _context.CustomerSubscriptions
            .FirstOrDefaultAsync(s => s.CustomerId == customerId && LiveStatuses.Contains(s.Status));

    public Task<CustomerSubscription?> GetActiveByCustomerAsync(Guid customerId) =>
        _context.CustomerSubscriptions
            .FirstOrDefaultAsync(s => s.CustomerId == customerId && s.Status == CustomerSubscriptionStatus.Active);

    public async Task AddAsync(CustomerSubscription subscription)
    {
        await _context.CustomerSubscriptions.AddAsync(subscription);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(CustomerSubscription subscription)
    {
        _context.CustomerSubscriptions.Update(subscription);
        await _context.SaveChangesAsync();
    }

    public async Task<bool> TryConsumeFreeVisitAsync(Guid subscriptionId)
    {
        // A single conditional UPDATE, not read-then-write - see this
        // method's doc comment on the interface. Re-checks both "still
        // Active" and "still has a credit" in the same statement that
        // decrements the counter.
        int affected = await _context.CustomerSubscriptions
            .Where(s => s.Id == subscriptionId
                && s.Status == CustomerSubscriptionStatus.Active
                && s.FreeVisitsRemaining > 0)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.FreeVisitsRemaining, s => s.FreeVisitsRemaining - 1));

        return affected == 1;
    }

    public async Task ReleaseFreeVisitAsync(Guid subscriptionId)
    {
        await _context.CustomerSubscriptions
            .Where(s => s.Id == subscriptionId && s.FreeVisitsRemaining < s.FreeVisitsIncludedSnapshot)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.FreeVisitsRemaining, s => s.FreeVisitsRemaining + 1));
    }

    public async Task<IReadOnlyList<CustomerSubscription>> ListDueForBillingAsync(DateTime asOfUtc) =>
        await _context.CustomerSubscriptions
            .Where(s => LiveStatuses.Contains(s.Status) && s.NextBillingDateUtc <= asOfUtc)
            .ToListAsync();

    public async Task<IReadOnlyList<CustomerSubscription>> ListExpiringSoonAsync(DateTime asOfUtc, DateTime windowEndUtc) =>
        await _context.CustomerSubscriptions
            .Where(s => s.Status == CustomerSubscriptionStatus.Active
                && s.CurrentPeriodEndUtc > asOfUtc
                && s.CurrentPeriodEndUtc <= windowEndUtc
                && s.ExpiringSoonNotifiedForPeriodEndUtc != s.CurrentPeriodEndUtc)
            .ToListAsync();
}
