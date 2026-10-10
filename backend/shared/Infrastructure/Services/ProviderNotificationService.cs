using Nestly.Application.Notifications;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <inheritdoc cref="IProviderNotificationService"/>
public class ProviderNotificationService : IProviderNotificationService
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    private readonly IProviderNotificationRepository _repository;

    public ProviderNotificationService(IProviderNotificationRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<ProviderNotificationListResponse>> ListAsync(Guid providerId, int page, int pageSize)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize switch
        {
            <= 0 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => pageSize
        };

        var (items, totalCount) = await _repository.ListByProviderAsync(providerId, page, pageSize);
        int unreadCount = await _repository.CountUnreadByProviderAsync(providerId);

        return new ProviderNotificationListResponse(items.Select(ToResponse).ToList(), totalCount, unreadCount, page, pageSize);
    }

    public async Task<Result<ProviderNotificationResponse>> MarkReadAsync(Guid providerId, Guid notificationId)
    {
        var notification = await _repository.GetByIdAsync(notificationId);
        if (notification is null || notification.ProviderId != providerId)
        {
            return Error.NotFound("ProviderNotification.NotFound", "Notification was not found.");
        }

        notification.MarkRead();
        await _repository.UpdateAsync(notification);

        return ToResponse(notification);
    }

    public async Task<Result> MarkAllReadAsync(Guid providerId)
    {
        await _repository.MarkAllReadAsync(providerId, DateTime.UtcNow);
        return Result.Success();
    }

    private static ProviderNotificationResponse ToResponse(ProviderNotification notification) => new(
        notification.Id, notification.Type, notification.Title, notification.Body,
        notification.DeepLinkPath, notification.IsRead, notification.CreatedAtUtc);
}
