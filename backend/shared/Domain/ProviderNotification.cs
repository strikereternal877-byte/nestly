using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>
/// An in-app notification for a provider (Provider Management UX pass).
/// Unlike <see cref="NotificationEvent"/> (a logged SMS/email/push send
/// attempt against a customer, purely an audit trail nothing reads back),
/// this is the record itself: what the provider's in-app bell/inbox
/// displays. Created by <c>ProviderNotificationPublisher</c>, which also
/// best-effort fans it out to the provider's registered devices via
/// <see cref="IPushNotificationProvider"/> - but the row here is the durable
/// source of truth, so a missed push is never a lost notification, only a
/// delayed one until the provider next opens the app.
/// </summary>
public class ProviderNotification : Entity<Guid>
{
    public Guid ProviderId { get; private set; }
    public ProviderNotificationType Type { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Body { get; private set; } = string.Empty;

    /// <summary>Relative in-app path the notification should open when tapped (e.g. "/jobs/{id}") - null when there is nowhere more specific to go than the notification list itself.</summary>
    public string? DeepLinkPath { get; private set; }

    public bool IsRead { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? ReadAtUtc { get; private set; }

    protected ProviderNotification() { }

    public ProviderNotification(Guid id, Guid providerId, ProviderNotificationType type, string title, string body, string? deepLinkPath = null)
        : base(id)
    {
        ProviderId = providerId;
        Type = type;
        Title = string.IsNullOrWhiteSpace(title)
            ? throw new ArgumentException("Title is required.", nameof(title))
            : title;
        Body = string.IsNullOrWhiteSpace(body)
            ? throw new ArgumentException("Body is required.", nameof(body))
            : body;
        DeepLinkPath = deepLinkPath;
        IsRead = false;
        CreatedAtUtc = DateTime.UtcNow;
    }

    public void MarkRead()
    {
        if (IsRead)
        {
            return;
        }

        IsRead = true;
        ReadAtUtc = DateTime.UtcNow;
    }
}
