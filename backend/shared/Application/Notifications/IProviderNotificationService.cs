using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.Notifications;

/// <summary>Read side of a provider's in-app notification inbox (Provider Management UX pass) - the provider-web header bell and the dedicated /notifications list both call through this.</summary>
public interface IProviderNotificationService
{
    Task<Result<ProviderNotificationListResponse>> ListAsync(Guid providerId, int page, int pageSize);

    Task<Result<ProviderNotificationResponse>> MarkReadAsync(Guid providerId, Guid notificationId);

    Task<Result> MarkAllReadAsync(Guid providerId);
}
