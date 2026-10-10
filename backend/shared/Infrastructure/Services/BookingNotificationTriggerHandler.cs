using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Cancellations;
using Nestly.Application.Notifications;
using Nestly.Application.Payments;
using Nestly.Application.Refunds;
using Nestly.BuildingBlocks.Primitives;
using Nestly.BuildingBlocks.Privacy;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Interceptors;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Notification trigger wiring for booking-lifecycle events (SRS 19.1, tasks
/// 88b-f: booking confirmed, payment success/failure, cancellation,
/// reschedule, refund). Wired the same way <see cref="EscrowReleaseOnCompletionHandler"/>
/// reacts to <see cref="BookingStatusChangedEvent"/> - one handler, branching
/// on <see cref="BookingStatusChangedEvent.ToStatus"/>, rather than a
/// separate domain event per trigger: every one of these triggers already
/// corresponds to exactly one booking status transition in the existing
/// Booking/Payment/Refund/Cancellation/Reschedule services, so reusing that
/// single event stream avoids adding parallel, easy-to-miss event types.
///
/// Payment confirmation is the one place two distinct SRS 19.1 triggers
/// (BookingConfirmed and PaymentSuccess) share one underlying transition
/// (PaymentPending -> Confirmed, in PaymentWebhookService) - both are
/// dispatched from that single ToStatus == Confirmed branch.
///
/// <para>
/// <b>Task 276 - the fulfilment half.</b> ProviderEnRoute/ProviderArrived/
/// InProgress/Completed used to map to nothing, so the entire second half of a
/// booking was silent. All four now dispatch from
/// <see cref="BookingStatusChangedEvent"/> rather than from task 272's
/// <c>ProviderEnRouteEvent</c>/<c>ProviderArrivedEvent</c>. Reasons, in order
/// of weight:
/// </para>
/// <list type="number">
/// <item>Every one of the four is a single <see cref="Booking.TransitionTo"/>
/// call, which raises exactly one <see cref="BookingStatusChangedEvent"/> - so
/// "exactly once per transition" is a property of the event stream, not
/// something this handler has to enforce.</item>
/// <item>The tracking events cover only two of the four, so mixing sources
/// would mean two handlers, two sets of repository reads and two places to
/// forget a new state.</item>
/// <item>Booking.TransitionTo raises the tracking event <i>in addition to</i>
/// BookingStatusChangedEvent, never instead of it (see its doc comment).
/// Subscribing to both would send "on the way" twice.</item>
/// </list>
/// <para>
/// <b>Task 295 - who is coming, and when the customer is told.</b> The fifth
/// of task 276's triggers, ProviderAssigned, is the exception to the paragraph
/// above: it does <i>not</i> hang off a status transition, because the
/// transition it used to hang off (AwaitingFulfilment -&gt; Assigned) happens
/// when <c>BookingProviderAssignmentService.AssignAsync</c> makes the *offer*,
/// before the provider has answered. The product rule is now:
/// </para>
/// <list type="bullet">
/// <item><b>Notify on acceptance only.</b> ProviderAssigned fires on
/// <see cref="ProviderAssignmentAcceptedEvent"/> (task 272), so a name is
/// announced only once its owner has committed to the job. A provider who
/// rejects, and the next candidate, and the next, cost the customer nothing;
/// they hear one name, once, and it is the right one. The price is that they
/// wait longer to learn who is coming - accepted deliberately, since a wrong
/// name is worse than a late one.</item>
/// <item><b>A change of professional is never silent</b>, and never re-sends
/// ProviderAssigned - that template reads as a first assignment.
/// <see cref="BookingProviderChangedEvent"/> (raised by
/// <c>BookingProviderAssignment.MarkReassigned</c>, which is reached whether
/// or not the booking's status moves) dispatches the distinct
/// <see cref="NotificationEventType.ProviderChanged"/> template. It sends only
/// when the superseded assignment had been <i>accepted</i>: an offer that was
/// never accepted was never announced, so there is no name in the customer's
/// head to correct.</item>
/// </list>
/// <para>
/// Consequence worth stating, because it is the point of the rule: no sequence
/// of offers, rejections and reassignments can produce two messages naming
/// different professionals as "assigned". The only messages that can name a
/// provider as assigned are acceptances, and between two acceptances a
/// ProviderChanged always sits, explaining the swap.
/// </para>
/// <para>
/// <b>DURABILITY (task 294) - these notifications survive this handler.</b>
/// Dispatch is still in-process MediatR published post-commit by
/// <c>DomainEventDispatchInterceptor</c>, and this handler still re-reads
/// booking, customer, provider, device tokens and payment/cancellation/refund
/// rows *after* the transaction committed - any of which can throw. What
/// changed is that it is no longer the only path. Before the transition
/// committed, <c>NotificationIntentInterceptor</c> wrote one durable
/// <see cref="NotificationIntent"/> row per message this event owes, inside
/// the same <c>SaveChanges</c>, so the obligation is as durable as the status
/// change that created it. Every dispatch below goes through
/// <see cref="INotificationIntentCoordinator"/>, which claims the intent
/// before sending and marks it delivered after; anything this handler fails to
/// send, or dies before sending, is picked up by
/// <c>NotificationIntentSweepJob</c> and re-run through
/// <see cref="INotificationTriggerHandler.HandleAsync"/> - the same code,
/// against the same rehydrated event. Delivery is at-least-once within the
/// retry bound and deduplicated by the intent's key; see
/// docs/ARCHITECTURE.md, "DOMAIN EVENT DISPATCH AND DELIVERY", for exactly
/// what that does and does not promise.
/// </para>
/// <para>
/// The corollary for anyone editing this class: <b>every path out of a
/// dispatch decision must resolve its intent</b>. Sending resolves it;
/// deciding not to send (muted, booking gone) must call
/// <c>SkipAsync</c>, or the sweep will keep offering a message this handler
/// has already deliberately declined until it is abandoned. And the set of
/// messages an event owes lives in <c>NotificationIntentPlanner</c>, not here,
/// because the intent writer and this handler have to agree on it exactly.
/// </para>
/// </summary>
public sealed class BookingNotificationTriggerHandler :
    INotificationHandler<DomainEventNotification<BookingStatusChangedEvent>>,
    INotificationHandler<DomainEventNotification<ProviderAssignmentAcceptedEvent>>,
    INotificationHandler<DomainEventNotification<BookingProviderChangedEvent>>,
    INotificationTriggerHandler
{
    /// <summary>Stand-in for a provider whose row cannot be resolved - see <see cref="AddProviderVariablesAsync"/>.</summary>
    private const string UnknownProviderName = "Your professional";

    private readonly IBookingRepository _bookingRepository;
    private readonly IPaymentTransactionRepository _paymentRepository;
    private readonly IPaymentGroupRepository _groupRepository;
    private readonly ICancellationRepository _cancellationRepository;
    private readonly IRefundTransactionRepository _refundRepository;
    private readonly IProviderRepository _providerRepository;
    private readonly INotificationDispatchService _notificationDispatchService;
    private readonly INotificationIntentCoordinator _intentCoordinator;
    private readonly IOptionsMonitor<FulfilmentNotificationOptions> _fulfilmentOptions;
    private readonly ILogger<BookingNotificationTriggerHandler> _logger;

    public BookingNotificationTriggerHandler(
        IBookingRepository bookingRepository,
        IPaymentTransactionRepository paymentRepository,
        IPaymentGroupRepository groupRepository,
        ICancellationRepository cancellationRepository,
        IRefundTransactionRepository refundRepository,
        IProviderRepository providerRepository,
        INotificationDispatchService notificationDispatchService,
        INotificationIntentCoordinator intentCoordinator,
        IOptionsMonitor<FulfilmentNotificationOptions> fulfilmentOptions,
        ILogger<BookingNotificationTriggerHandler> logger)
    {
        _bookingRepository = bookingRepository;
        _paymentRepository = paymentRepository;
        _groupRepository = groupRepository;
        _cancellationRepository = cancellationRepository;
        _refundRepository = refundRepository;
        _providerRepository = providerRepository;
        _notificationDispatchService = notificationDispatchService;
        _intentCoordinator = intentCoordinator;
        _fulfilmentOptions = fulfilmentOptions;
        _logger = logger;
    }

    /// <summary>Task 294: the three event types this handler owns, so the intent sweep can route a rehydrated event back to exactly it.</summary>
    public bool CanHandle(Type domainEventType) =>
        domainEventType == typeof(BookingStatusChangedEvent) ||
        domainEventType == typeof(ProviderAssignmentAcceptedEvent) ||
        domainEventType == typeof(BookingProviderChangedEvent);

    /// <inheritdoc />
    public Task HandleAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default) => domainEvent switch
    {
        BookingStatusChangedEvent statusChanged => HandleAsync(statusChanged, cancellationToken),
        ProviderAssignmentAcceptedEvent accepted => HandleAsync(accepted, cancellationToken),
        BookingProviderChangedEvent providerChanged => HandleAsync(providerChanged, cancellationToken),
        _ => Task.CompletedTask
    };

    public Task Handle(DomainEventNotification<BookingStatusChangedEvent> notification, CancellationToken cancellationToken) =>
        HandleAsync(notification.DomainEvent, cancellationToken);

    private async Task HandleAsync(BookingStatusChangedEvent domainEvent, CancellationToken cancellationToken)
    {
        // Which messages this transition owes lives in
        // NotificationIntentPlanner (task 294), because the durable intent
        // rows were written from that same function inside the transaction
        // that raised this event. A copy of the switch here that drifted by
        // one status would either strand rows nothing resolves or send
        // messages nothing recorded, and neither shows up as a failing test.
        //
        // Task 359: through Plan, not EventTypesFor. The intent writer plans
        // from the whole event, and Confirmed's message pair now depends on
        // more than the status (a zero-payable booking is owed no "payment
        // successful"), so asking the status-only table here would send a
        // message no intent row was ever written for - exactly the drift the
        // planner exists to prevent.
        var eventTypes = NotificationIntentPlanner.Plan(domainEvent);

        if (eventTypes.Count == 0)
        {
            return;
        }

        // Task 276: ops mute, applied before any repository read so a muted
        // event costs nothing at all. Only the fulfilment events can be muted;
        // FulfilmentNotificationOptions.IsEnabled returns true for everything
        // else, so the money-and-cancellation notifications above are
        // unreachable from configuration.
        //
        // The mute is applied here rather than at planning time on purpose: it
        // is an incident-response knob that can flip between the commit and
        // the sweep, so the answer has to be re-asked at delivery. A muted
        // message resolves its intent as Skipped, leaving an honest record
        // that it was owed and deliberately withheld.
        var options = _fulfilmentOptions.CurrentValue;
        var enabled = new List<NotificationEventType>(eventTypes.Count);
        foreach (var eventType in eventTypes)
        {
            if (options.IsEnabled(eventType))
            {
                enabled.Add(eventType);
            }
            else
            {
                await _intentCoordinator.SkipAsync(domainEvent, eventType, "Muted by FulfilmentNotificationOptions.", cancellationToken);
            }
        }

        if (enabled.Count == 0)
        {
            return;
        }

        var booking = await _bookingRepository.GetByIdAsync(domainEvent.BookingId);
        if (booking is null)
        {
            _logger.LogWarning("Booking {BookingId} not found while dispatching notifications for {ToStatus}.", domainEvent.BookingId, domainEvent.ToStatus);
            await SkipAllAsync(domainEvent, enabled, "Booking no longer exists.", cancellationToken);
            return;
        }

        // A prepaid plan settles many bookings with one payment. Each of them reaches
        // Confirmed, but the customer paid once and wants to hear it once: the group's
        // lead booking carries the confirmation (with the whole amount), every other
        // member's pair of messages is withheld. Resolved as Skipped, not dropped, so
        // the intent trail still shows they were owed and deliberately not sent.
        PaymentGroup? prepaidGroup = null;
        if (domainEvent.ToStatus == BookingStatus.Confirmed)
        {
            prepaidGroup = await FindPrepaidGroupAsync(booking);
            if (prepaidGroup is not null && prepaidGroup.LeadBookingId != booking.Id)
            {
                await SkipAllAsync(
                    domainEvent, enabled,
                    "Bundled into a prepaid checkout; the lead booking carries the confirmation.", cancellationToken);
                return;
            }
        }

        var recipient = await ResolveCustomerRecipientAsync(booking, cancellationToken);

        foreach (var eventType in enabled)
        {
            await _intentCoordinator.DeliverAsync(
                domainEvent,
                eventType,
                async ct =>
                {
                    var variables = await BuildVariablesAsync(eventType, booking, ct);
                    if (eventType == NotificationEventType.PaymentSuccess && prepaidGroup is not null)
                    {
                        // What the customer actually paid: the whole checkout, not this one visit.
                        variables["Amount"] = prepaidGroup.TotalAmount.ToString("0.00");
                    }

                    await _notificationDispatchService.DispatchAsync(booking.CustomerId, eventType, recipient, variables, bookingId: booking.Id, cancellationToken: ct);
                },
                cancellationToken);
        }
    }

    /// <summary>The prepaid checkout that paid for this booking, or null when it was paid on its own.</summary>
    private async Task<PaymentGroup?> FindPrepaidGroupAsync(Booking booking)
    {
        var transaction = await _paymentRepository.GetByBookingIdAsync(booking.Id);
        var groupId = transaction?.Attempts
            .Where(a => a.PaymentGroupId != null)
            .OrderByDescending(a => a.AttemptNumber)
            .Select(a => a.PaymentGroupId)
            .FirstOrDefault();

        return groupId is { } id ? await _groupRepository.GetByIdAsync(id) : null;
    }

    private async Task SkipAllAsync(
        IDomainEvent domainEvent,
        IReadOnlyList<NotificationEventType> eventTypes,
        string reason,
        CancellationToken cancellationToken)
    {
        foreach (var eventType in eventTypes)
        {
            await _intentCoordinator.SkipAsync(domainEvent, eventType, reason, cancellationToken);
        }
    }

    /// <summary>
    /// Task 295: the acceptance-time half of "who is coming". This, not the
    /// AwaitingFulfilment -&gt; Assigned transition, is where the customer is
    /// told a name - see the class doc comment for the rule and its price.
    /// </summary>
    /// <remarks>
    /// Exactly once per accepted job without this handler having to check
    /// anything: <c>BookingProviderAssignment.Accept</c> throws unless the row
    /// is still outstanding, so a second Accept raises no second event. A
    /// booking re-offered after a rejection produces one acceptance event for
    /// whichever provider finally takes it, and none for those who did not.
    /// </remarks>
    public Task Handle(DomainEventNotification<ProviderAssignmentAcceptedEvent> notification, CancellationToken cancellationToken) =>
        HandleAsync(notification.DomainEvent, cancellationToken);

    private Task HandleAsync(ProviderAssignmentAcceptedEvent domainEvent, CancellationToken cancellationToken) =>
        DispatchProviderNotificationAsync(
            domainEvent,
            domainEvent.BookingId,
            NotificationEventType.ProviderAssigned,
            providerId: domainEvent.ProviderId,
            previousProviderId: null,
            cancellationToken);

    /// <summary>
    /// Task 295: an accepted professional was swapped for another one. The
    /// defect this closes is that the swap moves no booking status - an
    /// already-Assigned booking is still Assigned afterwards - so nothing in
    /// the <see cref="BookingStatusChangedEvent"/> stream ever mentioned it and
    /// the customer would have met a stranger at the door.
    /// </summary>
    /// <remarks>
    /// Silent when the superseded assignment had not been accepted: under the
    /// acceptance-only rule that offer was never announced, so there is no
    /// expectation to correct and "your professional has changed" would name
    /// somebody the customer never heard of. The check lives here rather than
    /// in the domain because it is a rule about what we tell people, not about
    /// what happened.
    /// </remarks>
    public Task Handle(DomainEventNotification<BookingProviderChangedEvent> notification, CancellationToken cancellationToken) =>
        HandleAsync(notification.DomainEvent, cancellationToken);

    private Task HandleAsync(BookingProviderChangedEvent domainEvent, CancellationToken cancellationToken)
    {
        if (!domainEvent.PreviousAssignmentAccepted)
        {
            // NotificationIntentPlanner applies the same rule, so no intent
            // row was ever written for this event - there is nothing to
            // resolve and nothing for the sweep to find.
            return Task.CompletedTask;
        }

        return DispatchProviderNotificationAsync(
            domainEvent,
            domainEvent.BookingId,
            NotificationEventType.ProviderChanged,
            providerId: domainEvent.NewProviderId,
            previousProviderId: domainEvent.PreviousProviderId,
            cancellationToken);
    }

    /// <summary>
    /// The shared body of the two assignment-driven triggers. Same shape as
    /// the status-driven <see cref="Handle(DomainEventNotification{BookingStatusChangedEvent}, CancellationToken)"/>
    /// above - mute first, then booking, then recipient, then dispatch - but
    /// takes the provider from the event rather than from
    /// <see cref="Booking.AssignedProviderId"/>: both events name the provider
    /// they are about, and the denormalized field has already moved on to the
    /// next candidate by the time a ProviderChanged is handled.
    /// </summary>
    private async Task DispatchProviderNotificationAsync(
        IDomainEvent domainEvent,
        Guid bookingId,
        NotificationEventType eventType,
        Guid providerId,
        Guid? previousProviderId,
        CancellationToken cancellationToken)
    {
        if (!_fulfilmentOptions.CurrentValue.IsEnabled(eventType))
        {
            await _intentCoordinator.SkipAsync(domainEvent, eventType, "Muted by FulfilmentNotificationOptions.", cancellationToken);
            return;
        }

        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null)
        {
            _logger.LogWarning("Booking {BookingId} not found while dispatching a {EventType} notification.", bookingId, eventType);
            await _intentCoordinator.SkipAsync(domainEvent, eventType, "Booking no longer exists.", cancellationToken);
            return;
        }

        var recipient = await ResolveCustomerRecipientAsync(booking, cancellationToken);

        await _intentCoordinator.DeliverAsync(
            domainEvent,
            eventType,
            async ct =>
            {
                var variables = BuildBaseVariables(booking);
                await AddProviderVariablesAsync(variables, booking.Id, providerId);

                if (previousProviderId is { } previous)
                {
                    var previousProvider = await _providerRepository.GetByIdAsync(previous);
                    variables["PreviousProviderName"] = string.IsNullOrWhiteSpace(previousProvider?.DisplayName)
                        ? UnknownProviderName
                        : previousProvider.DisplayName;
                }

                await _notificationDispatchService.DispatchAsync(
                    booking.CustomerId, eventType, recipient, variables, bookingId: booking.Id, cancellationToken: ct);
            },
            cancellationToken);
    }

    /// <summary>
    /// Task 277: the customer/device-token lookups this used to do inline now
    /// live in INotificationDispatchService.ResolveRecipientAsync, which is the
    /// same call the provider side needs. Behaviour is unchanged - the ?? below
    /// reproduces the old <c>customer?.Mobile ?? booking.CustomerMobileSnapshot</c>
    /// exactly, including the case where a customer row exists with a blank
    /// mobile (blank wins, snapshot is only a fallback for a missing customer).
    /// The snapshot fallback stays here rather than moving into the resolver
    /// because it is booking data; the resolver only knows principals.
    /// </summary>
    private async Task<NotificationRecipient> ResolveCustomerRecipientAsync(Booking booking, CancellationToken cancellationToken)
    {
        var recipient = await _notificationDispatchService.ResolveRecipientAsync(
            DeviceTokenOwner.ForCustomer(booking.CustomerId), cancellationToken);

        return recipient with { Mobile = recipient.Mobile ?? booking.CustomerMobileSnapshot };
    }

    /// <summary>The variables every booking template can rely on, whichever event brought it here.</summary>
    private static Dictionary<string, string> BuildBaseVariables(Booking booking) => new()
    {
        ["CustomerName"] = booking.CustomerNameSnapshot,
        // The short code the customer sees in the app ("GLX-261001-H9G87"), not the 36-character internal id: a text
        // message that names a GUID cannot be matched to anything on screen. The variable keeps its name so every
        // template - including admin-edited ones - picks this up unchanged. A row from before references existed
        // falls back to the id rather than printing nothing.
        ["BookingId"] = string.IsNullOrWhiteSpace(booking.BookingReference) ? booking.Id.ToString() : booking.BookingReference,
        ["ServiceName"] = booking.Items.Count > 0 ? booking.Items[0].NameSnapshot : string.Empty,
        ["SlotDate"] = booking.SlotDate.ToString("yyyy-MM-dd"),
        ["SlotWindow"] = booking.SlotWindowNameSnapshot,
        ["TotalPayable"] = booking.TotalPayableSnapshot.ToString("0.00")
    };

    private async Task<Dictionary<string, string>> BuildVariablesAsync(NotificationEventType eventType, Booking booking, CancellationToken cancellationToken)
    {
        var variables = BuildBaseVariables(booking);

        switch (eventType)
        {
            case NotificationEventType.PaymentSuccess:
            case NotificationEventType.PaymentFailed:
            {
                var payment = await _paymentRepository.GetByBookingIdAsync(booking.Id);
                variables["Amount"] = (payment?.Amount ?? booking.TotalPayableSnapshot).ToString("0.00");
                break;
            }
            case NotificationEventType.BookingCancelled:
            {
                var cancellation = await _cancellationRepository.GetByBookingIdAsync(booking.Id);
                variables["CancellationFee"] = (cancellation?.CancellationFeeAmount ?? 0m).ToString("0.00");
                variables["RefundAmount"] = (cancellation?.RefundAmount ?? 0m).ToString("0.00");
                break;
            }
            case NotificationEventType.RefundProcessed:
            {
                var refunds = await _refundRepository.ListByBookingAsync(booking.Id);
                var latest = refunds.OrderByDescending(r => r.CreatedAtUtc).FirstOrDefault();
                variables["Amount"] = (latest?.Amount ?? 0m).ToString("0.00");
                variables["Method"] = latest?.Method.ToString() ?? string.Empty;
                break;
            }

            // Task 276: the fulfilment events are the only ones that name the
            // provider. ProviderAssigned and ProviderChanged are the two that
            // never arrive here - they come from the assignment events, which
            // carry the provider they are about (see
            // DispatchProviderNotificationAsync).
            case NotificationEventType.ProviderEnRoute:
            case NotificationEventType.ProviderArrived:
            case NotificationEventType.JobStarted:
            case NotificationEventType.JobCompleted:
            {
                await AddProviderVariablesAsync(variables, booking.Id, booking.AssignedProviderId);
                break;
            }
        }

        return variables;
    }

    /// <summary>
    /// Adds the provider identity the fulfilment templates render (task 276).
    ///
    /// <para>
    /// <b>ProviderMobile is masked here, at the only place it enters a
    /// template's variable set</b> - not in the template, and not at the
    /// channel. Template bodies are admin-editable at runtime
    /// (<c>NotificationTemplatesController</c>), so a variable that is unsafe
    /// only until someone types <c>{{ProviderMobile}}</c> into a text box is
    /// unsafe. Masking it before it is offered means no edit to any template,
    /// by anyone, can put a reachable provider number into an SMS. It is
    /// deliberately absent from every default body; the customer-facing place
    /// to reveal a contact is task 275's tracking response, which applies the
    /// same <see cref="ContactMasking"/> rule.
    /// </para>
    ///
    /// <para>
    /// Falls back to a neutral "Your professional" rather than leaving the
    /// placeholder empty: <c>AssignedProviderId</c> is nullable and the
    /// provider row could have been deleted, and "  is on the way" is a worse
    /// message than a slightly generic one. This is a fifth post-commit
    /// repository read - see the class doc comment on durability.
    /// </para>
    /// </summary>
    /// <param name="providerId">
    /// The provider this particular message is about. The status-driven
    /// triggers pass <see cref="Booking.AssignedProviderId"/>; the two
    /// assignment-driven ones (task 295) pass the id off their own event,
    /// which is authoritative for them - a ProviderChanged is handled after
    /// the denormalized field has already moved to the incoming provider, and
    /// an acceptance names the accepting provider rather than whoever the
    /// field happens to hold.
    /// </param>
    private async Task AddProviderVariablesAsync(Dictionary<string, string> variables, Guid bookingId, Guid? providerId)
    {
        var provider = providerId is { } id
            ? await _providerRepository.GetByIdAsync(id)
            : null;

        if (provider is null)
        {
            _logger.LogWarning(
                "Booking {BookingId} reached a fulfilment status with no resolvable assigned provider - notifying with a generic name.", bookingId);
        }

        variables["ProviderName"] = string.IsNullOrWhiteSpace(provider?.DisplayName) ? UnknownProviderName : provider.DisplayName;
        variables["ProviderMobile"] = ContactMasking.MaskOrNull(provider?.Phone) ?? string.Empty;
    }
}
