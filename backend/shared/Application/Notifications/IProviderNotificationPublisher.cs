using Nestly.Domain;

namespace Nestly.Application.Notifications;

/// <summary>
/// Write side of a provider's in-app notification inbox (Provider Management
/// UX pass) - the one entry point every provider-facing trigger (a new job
/// offer, a KYC rejection, a suspension, a payout going through) calls
/// through, mirroring how <see cref="INotificationDispatchService"/> is the
/// one entry point for customer-facing SMS/email/push. Kept as its own thin
/// interface, not folded into <see cref="IProviderNotificationService"/>:
/// that one is read-side (list/mark-read) for provider-api's own controller,
/// this one is write-side, called from unrelated services (KYC approval,
/// suspend, payout) that have no other reason to depend on the read surface.
/// </summary>
public interface IProviderNotificationPublisher
{
    /// <summary>
    /// Persists the notification (the durable record) and best-effort pushes
    /// it to the provider's registered devices. Never throws - a failure here
    /// must never fail the business operation that triggered it (suspending a
    /// provider, rejecting a document, etc. all still need to succeed even if
    /// notifying about it does not).
    /// </summary>
    Task NotifyAsync(Guid providerId, ProviderNotificationType type, string title, string body, string? deepLinkPath = null, CancellationToken cancellationToken = default);
}
