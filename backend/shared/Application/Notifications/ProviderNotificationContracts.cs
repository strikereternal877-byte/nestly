using Nestly.Domain;

namespace Nestly.Application.Notifications;

public sealed record ProviderNotificationResponse(
    Guid Id,
    ProviderNotificationType Type,
    string Title,
    string Body,
    string? DeepLinkPath,
    bool IsRead,
    DateTime CreatedAtUtc);

/// <summary>A page of a provider's notifications plus the unread count - the header bell needs the count on every screen, not only the notification list screen, so it rides along here rather than requiring a second round trip.</summary>
public sealed record ProviderNotificationListResponse(
    IReadOnlyList<ProviderNotificationResponse> Items,
    int TotalCount,
    int UnreadCount,
    int Page,
    int PageSize);
