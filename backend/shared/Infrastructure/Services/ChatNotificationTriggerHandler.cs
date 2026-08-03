using MediatR;
using Microsoft.Extensions.Logging;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Chat;
using Nestly.Application.Notifications;
using Nestly.Application.Support;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Persistence.Interceptors;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Offline push/SMS fallback for chat (PRODUCT-ENHANCEMENTS.md IN-APP CHAT
/// "Offline delivery", task 194): a message to a recipient with no live
/// SignalR connection falls back to the existing notification dispatch
/// (task 156, <see cref="INotificationDispatchService"/>) - same calling
/// convention as <c>SupportTicketNotificationTriggerHandler</c>.
///
/// Scope gap, documented rather than silently assumed: this only resolves
/// and notifies the *customer* side of a thread. When the customer is the
/// sender, the recipient is either the admin support team (context_type
/// support_ticket) or the assigned provider (context_type booking) - neither
/// has a single-identity, device-token-backed notification path wired up
/// yet (admins already watch the live support console per task 193; provider
/// push would need provider-api's own device-token infrastructure, which
/// task 193's provider reply view does not yet exist to produce messages
/// from anyway). Revisit once provider-side chat lands.
/// </summary>
public sealed class ChatNotificationTriggerHandler : INotificationHandler<DomainEventNotification<ChatMessageSentEvent>>
{
    private readonly IBookingRepository _bookingRepository;
    private readonly ISupportTicketRepository _supportTicketRepository;
    private readonly ICustomerRepository _customerRepository;
    private readonly IDeviceTokenRepository _deviceTokenRepository;
    private readonly IChatPresenceTracker _presenceTracker;
    private readonly INotificationDispatchService _notificationDispatchService;
    private readonly ILogger<ChatNotificationTriggerHandler> _logger;

    public ChatNotificationTriggerHandler(
        IBookingRepository bookingRepository,
        ISupportTicketRepository supportTicketRepository,
        ICustomerRepository customerRepository,
        IDeviceTokenRepository deviceTokenRepository,
        IChatPresenceTracker presenceTracker,
        INotificationDispatchService notificationDispatchService,
        ILogger<ChatNotificationTriggerHandler> logger)
    {
        _bookingRepository = bookingRepository;
        _supportTicketRepository = supportTicketRepository;
        _customerRepository = customerRepository;
        _deviceTokenRepository = deviceTokenRepository;
        _presenceTracker = presenceTracker;
        _notificationDispatchService = notificationDispatchService;
        _logger = logger;
    }

    public async Task Handle(DomainEventNotification<ChatMessageSentEvent> notification, CancellationToken cancellationToken)
    {
        var domainEvent = notification.DomainEvent;

        if (domainEvent.SenderType == ChatSenderType.Customer)
        {
            // The customer is the sender, not the recipient - see this
            // handler's doc comment for why the other direction isn't
            // notified yet.
            return;
        }

        Guid? customerId = domainEvent.ContextType switch
        {
            ChatContextType.Booking => (await _bookingRepository.GetByIdAsync(domainEvent.ContextId))?.CustomerId,
            ChatContextType.SupportTicket => (await _supportTicketRepository.GetByIdAsync(domainEvent.ContextId))?.CustomerId,
            _ => null
        };

        if (customerId is null)
        {
            _logger.LogWarning(
                "Chat message {MessageId} could not resolve a recipient customer for {ContextType} {ContextId}.",
                domainEvent.MessageId, domainEvent.ContextType, domainEvent.ContextId);
            return;
        }

        // Fail-open by design (see IChatPresenceTracker): an unknown or
        // errored presence read is treated as offline, so the notification
        // still fires rather than being silently skipped.
        if (await _presenceTracker.IsOnlineAsync(customerId.Value, cancellationToken))
        {
            return;
        }

        var customer = await _customerRepository.GetByIdAsync(customerId.Value);
        if (customer is null)
        {
            _logger.LogWarning("Customer {CustomerId} not found while dispatching a chat-message notification.", customerId);
            return;
        }

        var variables = new Dictionary<string, string>
        {
            ["CustomerName"] = customer.Name,
            ["SenderType"] = domainEvent.SenderType.ToString(),
            ["MessagePreview"] = domainEvent.Body.Length > 120 ? domainEvent.Body[..120] + "..." : domainEvent.Body
        };

        var deviceTokens = await _deviceTokenRepository.ListActiveByCustomerAsync(customerId.Value);

        await _notificationDispatchService.DispatchAsync(
            customerId.Value,
            NotificationEventType.NewChatMessage,
            new NotificationRecipient(customer.Mobile, customer.Email, deviceTokens.Select(t => t.Token).ToList()),
            variables,
            bookingId: domainEvent.ContextType == ChatContextType.Booking ? domainEvent.ContextId : null,
            supportTicketId: domainEvent.ContextType == ChatContextType.SupportTicket ? domainEvent.ContextId : null,
            cancellationToken: cancellationToken);
    }
}
