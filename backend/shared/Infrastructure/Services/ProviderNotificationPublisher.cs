using Microsoft.Extensions.Logging;
using Nestly.Application.Notifications;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <inheritdoc cref="IProviderNotificationPublisher"/>
/// <remarks>
/// Provider Management UX pass. Task 277 gave a provider somewhere to
/// register a device ("no provider could be told a job had been assigned to
/// them - the trigger the whole tracking chain waits on", <see cref="DeviceToken"/>'s
/// doc comment) but nothing ever called <see cref="IPushNotificationProvider"/>
/// with a provider's tokens - registration existed, dispatch did not. This is
/// that dispatch, plus the in-app row provider-web's notification bell/list
/// reads, so the notification survives even when push fails or arrives while
/// the app is closed.
///
/// Deliberately NOT routed through <see cref="INotificationDispatchService"/>/
/// <see cref="NotificationIntent"/>'s durable at-least-once machinery: that
/// system exists because SMS/email are a customer's only channel for
/// something like a booking confirmation, so a lost send is a lost message.
/// Here the <see cref="ProviderNotification"/> row itself is the durable
/// record - a provider who misses the push still sees it next time they open
/// the app - so push is a best-effort "wake up and look" nudge, not the
/// channel of record. Building the intent/sweep/retry apparatus for a nudge
/// would be effort spent on a guarantee this feature does not need.
/// </remarks>
public class ProviderNotificationPublisher : IProviderNotificationPublisher
{
    private readonly IProviderNotificationRepository _notificationRepository;
    private readonly IDeviceTokenRepository _deviceTokenRepository;
    private readonly IPushNotificationProvider _pushNotificationProvider;
    private readonly ILogger<ProviderNotificationPublisher> _logger;

    public ProviderNotificationPublisher(
        IProviderNotificationRepository notificationRepository,
        IDeviceTokenRepository deviceTokenRepository,
        IPushNotificationProvider pushNotificationProvider,
        ILogger<ProviderNotificationPublisher> logger)
    {
        _notificationRepository = notificationRepository;
        _deviceTokenRepository = deviceTokenRepository;
        _pushNotificationProvider = pushNotificationProvider;
        _logger = logger;
    }

    public async Task NotifyAsync(
        Guid providerId, ProviderNotificationType type, string title, string body, string? deepLinkPath = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var notification = new ProviderNotification(Guid.NewGuid(), providerId, type, title, body, deepLinkPath);
            await _notificationRepository.AddAsync(notification);
        }
        catch (Exception ex)
        {
            // The row is what provider-web's inbox reads - if this fails,
            // still attempt the push below rather than compounding the loss,
            // but log loudly since a provider now has no durable record at
            // all for this event.
            _logger.LogError(ex, "Failed to persist a {Type} in-app notification for provider {ProviderId}.", type, providerId);
        }

        try
        {
            var deviceTokens = await _deviceTokenRepository.ListActiveByOwnerAsync(DeviceTokenOwner.ForProvider(providerId));
            foreach (var deviceToken in deviceTokens)
            {
                var result = await _pushNotificationProvider.SendPushAsync(deviceToken.Token, title, body, cancellationToken);
                if (!result.IsSuccess)
                {
                    _logger.LogWarning(
                        "Push delivery of a {Type} notification to provider {ProviderId} failed: {Error}",
                        type, providerId, result.Error.Message);
                }
            }
        }
        catch (Exception ex)
        {
            // Best-effort by design - see this class's doc comment. The
            // in-app row above (if it saved) is what actually matters.
            _logger.LogWarning(ex, "Push delivery of a {Type} notification to provider {ProviderId} threw.", type, providerId);
        }
    }
}
