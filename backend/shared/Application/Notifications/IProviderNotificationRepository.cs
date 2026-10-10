using Nestly.Domain;

namespace Nestly.Application.Notifications;

public interface IProviderNotificationRepository
{
    Task AddAsync(ProviderNotification notification);

    Task<ProviderNotification?> GetByIdAsync(Guid id);

    /// <summary>Newest first, paged - the provider-web notification list and the header bell's recent-items panel both read through this.</summary>
    Task<(IReadOnlyList<ProviderNotification> Items, int TotalCount)> ListByProviderAsync(Guid providerId, int page, int pageSize);

    Task<int> CountUnreadByProviderAsync(Guid providerId);

    Task UpdateAsync(ProviderNotification notification);

    /// <summary>Bulk-marks every unread row for the provider read in one statement, mirroring <c>ChatMessageRepository.MarkThreadReadAsync</c>'s ExecuteUpdateAsync idiom rather than loading N rows to flip one flag each.</summary>
    Task MarkAllReadAsync(Guid providerId, DateTime readAtUtc);
}
