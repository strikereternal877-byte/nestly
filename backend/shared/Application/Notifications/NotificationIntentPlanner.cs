using Nestly.BuildingBlocks.Primitives;
using Nestly.Domain;
using Nestly.Domain.Events;

namespace Nestly.Application.Notifications;

/// <summary>
/// The single answer to "which customer-facing messages does this domain event
/// owe?" (task 294).
///
/// <para>
/// <b>Why this is shared rather than duplicated.</b> Two places need the
/// answer: <c>NotificationIntentInterceptor</c>, which writes one durable
/// intent row per message inside the transaction that raised the event, and
/// the four trigger handlers, which claim those rows by key before sending. If
/// the two disagreed the guarantee would quietly rot in both directions - a
/// message the planner did not foresee gets sent with no durable record behind
/// it (silently back to at-most-once), and a row no handler ever resolves is
/// retried until it is abandoned. Neither failure is visible in a green test
/// suite, so the only safe structure is one function.
/// </para>
///
/// <para>
/// <b>Pure by construction.</b> No repository reads, no clock, no
/// configuration - it runs inside <c>SavingChanges</c>, where a query would
/// re-enter the context being saved, and it must return the same answer on the
/// retry path months later. Everything conditional that <i>is</i> I/O or
/// runtime policy (the ops mute, chat presence, a row that has since been
/// deleted) stays in the handler and resolves the intent to
/// <see cref="NotificationIntentStatus.Skipped"/> instead.
/// </para>
/// </summary>
public static class NotificationIntentPlanner
{
    /// <summary>
    /// The domain events that can warrant a notification, keyed by the name
    /// persisted on the intent row.
    ///
    /// <para>
    /// An explicit allow-list, not an assembly scan: the sweep resolves a type
    /// from a string it read out of the database and then deserializes into
    /// it, and the set of types that is allowed to happen for should be
    /// readable in one place rather than inferred from what happens to
    /// implement an interface.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Type> EventTypeRegistry = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        [nameof(BookingStatusChangedEvent)] = typeof(BookingStatusChangedEvent),
        [nameof(ProviderAssignmentAcceptedEvent)] = typeof(ProviderAssignmentAcceptedEvent),
        [nameof(BookingProviderChangedEvent)] = typeof(BookingProviderChangedEvent),
        [nameof(ChatMessageSentEvent)] = typeof(ChatMessageSentEvent),
        [nameof(SupportTicketStatusChangedEvent)] = typeof(SupportTicketStatusChangedEvent),
        [nameof(SubscriptionRenewedEvent)] = typeof(SubscriptionRenewedEvent),
        [nameof(SubscriptionExpiringSoonEvent)] = typeof(SubscriptionExpiringSoonEvent),
        [nameof(SubscriptionPaymentFailedEvent)] = typeof(SubscriptionPaymentFailedEvent),
        [nameof(AmcContractPurchasedEvent)] = typeof(AmcContractPurchasedEvent),
        [nameof(AmcVisitRedeemedEvent)] = typeof(AmcVisitRedeemedEvent),
        [nameof(AmcContractExpiringSoonEvent)] = typeof(AmcContractExpiringSoonEvent),
        [nameof(AmcContractExhaustedEvent)] = typeof(AmcContractExhaustedEvent),
        [nameof(MonthlyServiceProviderAssignedEvent)] = typeof(MonthlyServiceProviderAssignedEvent),
        [nameof(MonthlyServiceLeaveMarkedEvent)] = typeof(MonthlyServiceLeaveMarkedEvent),
        [nameof(MonthlyServiceVisitSkippedEvent)] = typeof(MonthlyServiceVisitSkippedEvent),
        [nameof(MonthlyServiceInvoiceIssuedEvent)] = typeof(MonthlyServiceInvoiceIssuedEvent),
        [nameof(MonthlyServicePausedForNonPaymentEvent)] = typeof(MonthlyServicePausedForNonPaymentEvent),
        [nameof(MonthlyServiceCancelledEvent)] = typeof(MonthlyServiceCancelledEvent)
    };

    /// <summary>
    /// Every message <paramref name="domainEvent"/> owes, in dispatch order.
    /// Empty for the vast majority of events - most of what happens in this
    /// system is not worth a customer's attention, and an empty plan writes no
    /// rows at all.
    /// </summary>
    public static IReadOnlyList<NotificationEventType> Plan(IDomainEvent domainEvent) => domainEvent switch
    {
        BookingStatusChangedEvent statusChanged => WithoutUnchargedPaymentSuccess(statusChanged),

        // Task 295: announced on acceptance, never on the offer.
        ProviderAssignmentAcceptedEvent => [NotificationEventType.ProviderAssigned],

        // Task 295: an offer nobody ever accepted was never announced, so
        // there is no expectation to correct and no message is owed.
        BookingProviderChangedEvent providerChanged => providerChanged.PreviousAssignmentAccepted
            ? [NotificationEventType.ProviderChanged]
            : [],

        // Task 194: only the customer side of a thread has a notification
        // path, so a message the customer sent owes nobody anything. Whether
        // they are currently online is a delivery-time question, not a
        // planning one - see this class's doc comment.
        ChatMessageSentEvent chatMessage => chatMessage.SenderType == ChatSenderType.Customer
            ? []
            : [NotificationEventType.NewChatMessage],

        SupportTicketStatusChangedEvent => [NotificationEventType.SupportTicketUpdate],

        SubscriptionRenewedEvent => [NotificationEventType.SubscriptionRenewed],
        SubscriptionExpiringSoonEvent => [NotificationEventType.SubscriptionExpiringSoon],
        SubscriptionPaymentFailedEvent => [NotificationEventType.SubscriptionPaymentFailed],

        AmcContractPurchasedEvent => [NotificationEventType.AmcContractPurchased],
        AmcVisitRedeemedEvent => [NotificationEventType.AmcVisitRedeemed],
        AmcContractExpiringSoonEvent => [NotificationEventType.AmcContractExpiringSoon],
        AmcContractExhaustedEvent => [NotificationEventType.AmcContractExhausted],

        // docs/MONTHLY-SERVICE.md NOTIFICATIONS: an assignment tells both
        // sides; a cancellation only matters to a professional who was going.
        MonthlyServiceProviderAssignedEvent => [NotificationEventType.MonthlyProviderAssigned, NotificationEventType.MonthlyNewClient],
        MonthlyServiceLeaveMarkedEvent => [NotificationEventType.MonthlyProviderLeave],
        MonthlyServiceVisitSkippedEvent => [NotificationEventType.MonthlyVisitSkipped],
        MonthlyServiceInvoiceIssuedEvent => [NotificationEventType.MonthlyInvoiceIssued],
        MonthlyServicePausedForNonPaymentEvent => [NotificationEventType.MonthlyServicePaused],
        MonthlyServiceCancelledEvent cancelled => cancelled.ProviderId is null ? [] : [NotificationEventType.MonthlyClientCancelled],

        _ => []
    };

    /// <summary>
    /// Task 359: <see cref="EventTypesFor"/> is keyed on status alone, so
    /// <see cref="BookingStatus.Confirmed"/> owes both
    /// <see cref="NotificationEventType.BookingConfirmed"/> and
    /// <see cref="NotificationEventType.PaymentSuccess"/>. That is right for
    /// the transition the pair was written for (PaymentPending -&gt; Confirmed,
    /// on a real gateway callback) and false for the one task 331 added: a
    /// booking with nothing payable is confirmed at creation with no payment
    /// behind it, and telling that customer their payment succeeded describes
    /// a charge that never happened.
    ///
    /// <para>
    /// Suppressed here rather than in <see cref="EventTypesFor"/> on purpose.
    /// That table is the shared status mapping - it answers a question about a
    /// status, and it is the only thing standing between the intent writer and
    /// the handlers agreeing. "Was anything actually charged" is a fact about
    /// this particular booking, not about the status, so it belongs on the
    /// per-event arm beside the other two context-dependent silences above
    /// (an unaccepted assignment, a customer's own chat message).
    /// </para>
    ///
    /// <para>
    /// <see cref="NotificationEventType.BookingConfirmed"/> is untouched: the
    /// booking genuinely is confirmed, which is the fact the customer needs.
    /// </para>
    /// </summary>
    private static IReadOnlyList<NotificationEventType> WithoutUnchargedPaymentSuccess(BookingStatusChangedEvent statusChanged)
    {
        var eventTypes = EventTypesFor(statusChanged.ToStatus);
        if (statusChanged.AnythingPayable)
        {
            return eventTypes;
        }

        return eventTypes.Where(eventType => eventType != NotificationEventType.PaymentSuccess).ToList();
    }

    /// <summary>
    /// The booking-lifecycle mapping, lifted out of
    /// <c>BookingNotificationTriggerHandler</c> so the intent writer and the
    /// handler cannot drift apart.
    ///
    /// <para>
    /// Not every transition has a notification - Initiated/PaymentPending/
    /// AwaitingFulfilment/RefundPending are all silent, and deliberately so:
    /// none of them is a fact a customer can act on, and AwaitingFulfilment is
    /// reached both from Confirmed and from a provider rejecting a job, where
    /// "your booking is waiting again" is noise the customer cannot do
    /// anything about.
    /// </para>
    ///
    /// <para>
    /// Confirmed is the one transition that owes two messages
    /// (PaymentPending -&gt; Confirmed in <c>PaymentWebhookService</c> is both
    /// "your booking is confirmed" and "your payment went through"). Assigned
    /// is deliberately absent: reaching it means an offer was made, not that
    /// anybody accepted it - see <see cref="Plan"/>.
    /// </para>
    ///
    /// <para>
    /// Task 359: Confirmed's second message is dropped by <see cref="Plan"/>
    /// for a booking that never had anything to charge. That is a fact about
    /// the booking rather than about the status, so it does not live in this
    /// table - which means callers must plan through <see cref="Plan"/> and
    /// not read this mapping directly for a booking-status event.
    /// </para>
    /// </summary>
    public static IReadOnlyList<NotificationEventType> EventTypesFor(BookingStatus toStatus) => toStatus switch
    {
        BookingStatus.Confirmed => [NotificationEventType.BookingConfirmed, NotificationEventType.PaymentSuccess],
        BookingStatus.PaymentFailed => [NotificationEventType.PaymentFailed],
        BookingStatus.CancelledByCustomer or BookingStatus.CancelledByAdmin => [NotificationEventType.BookingCancelled],
        BookingStatus.Rescheduled => [NotificationEventType.BookingRescheduled],
        BookingStatus.Refunded => [NotificationEventType.RefundProcessed],
        BookingStatus.Expired => [NotificationEventType.BookingExpired],

        // Task 276: the fulfilment half. One event type per transition.
        BookingStatus.ProviderEnRoute => [NotificationEventType.ProviderEnRoute],
        BookingStatus.ProviderArrived => [NotificationEventType.ProviderArrived],
        BookingStatus.InProgress => [NotificationEventType.JobStarted],
        BookingStatus.Completed => [NotificationEventType.JobCompleted],

        _ => []
    };

    /// <summary>
    /// Resolves the CLR type behind a persisted <c>DomainEventType</c> name,
    /// or null if the name is not one this planner recognises - which happens
    /// only when an intent outlives the removal or rename of its event type,
    /// and is a permanent condition the sweep should abandon rather than
    /// retry.
    /// </summary>
    public static Type? ResolveEventType(string domainEventTypeName) =>
        EventTypeRegistry.TryGetValue(domainEventTypeName, out var type) ? type : null;
}
