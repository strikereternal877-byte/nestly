using Microsoft.EntityFrameworkCore;
using Nestly.Application.Payments;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class PaymentGroupRepository : IPaymentGroupRepository
{
    private readonly NestlyDbContext _context;

    public PaymentGroupRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task CreateAsync(
        PaymentGroup group,
        IReadOnlyCollection<PaymentTransaction> newTransactions,
        IReadOnlyCollection<PaymentTransaction> retriedTransactions)
    {
        // One SaveChanges for the group, every brand-new member transaction (with its
        // attempt) and every retried member's new attempt: either the whole gateway
        // order is recorded or none of it is. EF orders the inserts by FK, so the
        // group row lands before the attempts that reference it.
        await _context.PaymentGroups.AddAsync(group);
        await _context.PaymentTransactions.AddRangeAsync(newTransactions);

        foreach (var retried in retriedTransactions)
        {
            if (_context.Entry(retried).State == EntityState.Detached)
            {
                _context.PaymentTransactions.Update(retried);
            }
        }

        await _context.SaveChangesAsync();
    }

    public Task<PaymentGroup?> GetByGatewayOrderIdAsync(string gatewayOrderId) =>
        _context.PaymentGroups.FirstOrDefaultAsync(g => g.GatewayOrderId == gatewayOrderId);

    public Task<PaymentGroup?> GetByIdAsync(Guid id) =>
        _context.PaymentGroups.FirstOrDefaultAsync(g => g.Id == id);

    public Task<PaymentGroup?> GetLatestByLeadBookingIdAsync(Guid leadBookingId) =>
        _context.PaymentGroups
            .Where(g => g.LeadBookingId == leadBookingId)
            .OrderByDescending(g => g.CreatedAtUtc)
            .FirstOrDefaultAsync();

    public async Task<IReadOnlyList<PaymentTransaction>> ListMemberTransactionsAsync(Guid groupId) =>
        await _context.PaymentTransactions
            .Include(t => t.Attempts)
            .Where(t => t.Attempts.Any(a => a.PaymentGroupId == groupId))
            .OrderBy(t => t.CreatedAtUtc)
            .ToListAsync();

    public async Task<bool> TryMarkResolvedAsync(Guid groupId, PaymentGroupStatus newStatus)
    {
        // Same conditional-UPDATE idiom as PaymentTransactionRepository.TryMarkAttemptResolvedAsync:
        // of two concurrent deliveries for the same gateway order only one can flip Pending.
        int affected = await _context.PaymentGroups
            .Where(g => g.Id == groupId && g.Status == PaymentGroupStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(g => g.Status, newStatus)
                .SetProperty(g => g.CompletedAtUtc, DateTime.UtcNow));

        return affected == 1;
    }

    public async Task UpdateAsync(PaymentGroup group)
    {
        if (_context.Entry(group).State == EntityState.Detached)
        {
            _context.PaymentGroups.Update(group);
        }

        await _context.SaveChangesAsync();
    }
}
