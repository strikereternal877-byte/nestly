using Nestly.Domain;

namespace Nestly.Application.Notifications;

public interface INotificationEventRepository
{
    Task AddAsync(NotificationEvent notification);

    Task UpdateAsync(NotificationEvent notification);

    Task<IReadOnlyList<NotificationEvent>> ListByCustomerAsync(Guid customerId);

    /// <summary>
    /// When this customer was last sent a notification of this type, or null if never - a targeted read (one row) for
    /// callers that only need "have I told them recently", instead of loading the customer's whole history.
    /// </summary>
    Task<DateTime?> GetLatestCreatedAtUtcAsync(Guid customerId, NotificationEventType eventType);
}
