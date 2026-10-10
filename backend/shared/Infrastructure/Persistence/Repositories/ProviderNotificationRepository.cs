using Microsoft.EntityFrameworkCore;
using Nestly.Application.Notifications;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class ProviderNotificationRepository : IProviderNotificationRepository
{
    private readonly NestlyDbContext _context;

    public ProviderNotificationRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(ProviderNotification notification)
    {
        await _context.ProviderNotifications.AddAsync(notification);
        await _context.SaveChangesAsync();
    }

    public Task<ProviderNotification?> GetByIdAsync(Guid id) =>
        _context.ProviderNotifications.FirstOrDefaultAsync(n => n.Id == id);

    public async Task<(IReadOnlyList<ProviderNotification> Items, int TotalCount)> ListByProviderAsync(Guid providerId, int page, int pageSize)
    {
        var query = _context.ProviderNotifications.Where(n => n.ProviderId == providerId);

        int totalCount = await query.CountAsync();
        var items = await query
            .OrderByDescending(n => n.CreatedAtUtc)
            .ApplyPaging(page, pageSize)
            .AsNoTracking()
            .ToListAsync();

        return (items, totalCount);
    }

    public Task<int> CountUnreadByProviderAsync(Guid providerId) =>
        _context.ProviderNotifications.CountAsync(n => n.ProviderId == providerId && !n.IsRead);

    public async Task UpdateAsync(ProviderNotification notification)
    {
        _context.ProviderNotifications.Update(notification);
        await _context.SaveChangesAsync();
    }

    public Task MarkAllReadAsync(Guid providerId, DateTime readAtUtc) =>
        _context.ProviderNotifications
            .Where(n => n.ProviderId == providerId && !n.IsRead)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(n => n.IsRead, true)
                .SetProperty(n => n.ReadAtUtc, readAtUtc));
}
