using Microsoft.EntityFrameworkCore;
using Nestly.Application.Notifications;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class NotificationEventRepository : INotificationEventRepository
{
    private readonly NestlyDbContext _context;

    public NotificationEventRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(NotificationEvent notification)
    {
        await _context.NotificationEvents.AddAsync(notification);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(NotificationEvent notification)
    {
        _context.NotificationEvents.Update(notification);
        await _context.SaveChangesAsync();
    }

    public async Task<DateTime?> GetLatestCreatedAtUtcAsync(Guid customerId, NotificationEventType eventType) =>
        await _context.NotificationEvents
            .Where(n => n.CustomerId == customerId && n.EventType == eventType)
            .OrderByDescending(n => n.CreatedAtUtc)
            .Select(n => (DateTime?)n.CreatedAtUtc)
            .FirstOrDefaultAsync();

    public async Task<IReadOnlyList<NotificationEvent>> ListByCustomerAsync(Guid customerId) =>
        await _context.NotificationEvents
            .Where(n => n.CustomerId == customerId)
            .OrderByDescending(n => n.CreatedAtUtc)
            .ToListAsync();
}
