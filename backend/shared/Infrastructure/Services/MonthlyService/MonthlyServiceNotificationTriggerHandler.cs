using System.Globalization;
using MediatR;
using Microsoft.Extensions.Logging;
using Nestly.Application;
using Nestly.Application.MonthlyService;
using Nestly.Application.Notifications;
using Nestly.BuildingBlocks.Primitives;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Domain.MonthlyService;
using Nestly.Infrastructure.Persistence.Interceptors;

namespace Nestly.Infrastructure.Services.MonthlyService;

/// <summary>
/// Sends the Monthly Service notifications (docs/MONTHLY-SERVICE.md
/// NOTIFICATIONS) - to the customer (professional assigned / on leave, bill
/// issued, service paused) and to the professional (new home, day skipped,
/// home stopped). Same shape as <see cref="AmcNotificationTriggerHandler"/>:
/// the in-process path via MediatR, and the durable retry path via
/// <see cref="INotificationTriggerHandler"/>, both through the intent
/// coordinator so a notification is sent once and never silently lost.
///
/// The notification log is keyed by customer for every message, including
/// the ones addressed to the professional - the same convention booking
/// notifications to a professional already follow.
/// </summary>
public sealed class MonthlyServiceNotificationTriggerHandler :
    INotificationHandler<DomainEventNotification<MonthlyServiceProviderAssignedEvent>>,
    INotificationHandler<DomainEventNotification<MonthlyServiceLeaveMarkedEvent>>,
    INotificationHandler<DomainEventNotification<MonthlyServiceVisitSkippedEvent>>,
    INotificationHandler<DomainEventNotification<MonthlyServiceInvoiceIssuedEvent>>,
    INotificationHandler<DomainEventNotification<MonthlyServicePausedForNonPaymentEvent>>,
    INotificationHandler<DomainEventNotification<MonthlyServiceCancelledEvent>>,
    INotificationTriggerHandler
{
    // Invariant (English) formats: "Tue, 6 Oct", "8:00 AM", "October 2026" -
    // no dependence on the host's installed cultures.
    private static readonly CultureInfo Formats = CultureInfo.InvariantCulture;

    private readonly IMonthlyServiceContractRepository _contractRepository;
    private readonly ICustomerRepository _customerRepository;
    private readonly IProviderRepository _providerRepository;
    private readonly INotificationDispatchService _dispatchService;
    private readonly INotificationIntentCoordinator _intentCoordinator;
    private readonly ILogger<MonthlyServiceNotificationTriggerHandler> _logger;

    public MonthlyServiceNotificationTriggerHandler(
        IMonthlyServiceContractRepository contractRepository,
        ICustomerRepository customerRepository,
        IProviderRepository providerRepository,
        INotificationDispatchService dispatchService,
        INotificationIntentCoordinator intentCoordinator,
        ILogger<MonthlyServiceNotificationTriggerHandler> logger)
    {
        _contractRepository = contractRepository;
        _customerRepository = customerRepository;
        _providerRepository = providerRepository;
        _dispatchService = dispatchService;
        _intentCoordinator = intentCoordinator;
        _logger = logger;
    }

    public bool CanHandle(Type domainEventType) =>
        domainEventType == typeof(MonthlyServiceProviderAssignedEvent)
        || domainEventType == typeof(MonthlyServiceLeaveMarkedEvent)
        || domainEventType == typeof(MonthlyServiceVisitSkippedEvent)
        || domainEventType == typeof(MonthlyServiceInvoiceIssuedEvent)
        || domainEventType == typeof(MonthlyServicePausedForNonPaymentEvent)
        || domainEventType == typeof(MonthlyServiceCancelledEvent);

    public Task HandleAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default) => domainEvent switch
    {
        MonthlyServiceProviderAssignedEvent e => HandleAssignedAsync(e, cancellationToken),
        MonthlyServiceLeaveMarkedEvent e => SendAsync(e, NotificationEventType.MonthlyProviderLeave, e.ContractId, Audience.Customer, e.ProviderId,
            v => v["Date"] = FormatDay(e.Date), cancellationToken),
        MonthlyServiceVisitSkippedEvent e => SendAsync(e, NotificationEventType.MonthlyVisitSkipped, e.ContractId, Audience.Provider, e.ProviderId,
            v => v["Date"] = FormatDay(e.Date), cancellationToken),
        MonthlyServiceInvoiceIssuedEvent e => SendAsync(e, NotificationEventType.MonthlyInvoiceIssued, e.ContractId, Audience.Customer, null,
            v =>
            {
                v["Amount"] = e.Amount.ToString("0.00", CultureInfo.InvariantCulture);
                v["Month"] = e.PeriodStart.ToString("MMMM yyyy", Formats);
                v["DueDate"] = FormatDay(e.DueDate);
            },
            cancellationToken),
        MonthlyServicePausedForNonPaymentEvent e => SendAsync(e, NotificationEventType.MonthlyServicePaused, e.ContractId, Audience.Customer, null, null, cancellationToken),
        MonthlyServiceCancelledEvent e => e.ProviderId is { } providerId
            ? SendAsync(e, NotificationEventType.MonthlyClientCancelled, e.ContractId, Audience.Provider, providerId, null, cancellationToken)
            : Task.CompletedTask,
        _ => Task.CompletedTask
    };

    public Task Handle(DomainEventNotification<MonthlyServiceProviderAssignedEvent> notification, CancellationToken cancellationToken) =>
        HandleAsync(notification.DomainEvent, cancellationToken);

    public Task Handle(DomainEventNotification<MonthlyServiceLeaveMarkedEvent> notification, CancellationToken cancellationToken) =>
        HandleAsync(notification.DomainEvent, cancellationToken);

    public Task Handle(DomainEventNotification<MonthlyServiceVisitSkippedEvent> notification, CancellationToken cancellationToken) =>
        HandleAsync(notification.DomainEvent, cancellationToken);

    public Task Handle(DomainEventNotification<MonthlyServiceInvoiceIssuedEvent> notification, CancellationToken cancellationToken) =>
        HandleAsync(notification.DomainEvent, cancellationToken);

    public Task Handle(DomainEventNotification<MonthlyServicePausedForNonPaymentEvent> notification, CancellationToken cancellationToken) =>
        HandleAsync(notification.DomainEvent, cancellationToken);

    public Task Handle(DomainEventNotification<MonthlyServiceCancelledEvent> notification, CancellationToken cancellationToken) =>
        HandleAsync(notification.DomainEvent, cancellationToken);

    private enum Audience
    {
        Customer,
        Provider
    }

    private async Task HandleAssignedAsync(MonthlyServiceProviderAssignedEvent e, CancellationToken cancellationToken)
    {
        await SendAsync(e, NotificationEventType.MonthlyProviderAssigned, e.ContractId, Audience.Customer, e.ProviderId, null, cancellationToken);
        await SendAsync(e, NotificationEventType.MonthlyNewClient, e.ContractId, Audience.Provider, e.ProviderId, null, cancellationToken);
        if (e.PreviousProviderId is { } previousProviderId)
        {
            await SendAsync(e, NotificationEventType.MonthlyClientCancelled, e.ContractId, Audience.Provider, previousProviderId, null, cancellationToken);
        }
    }

    private async Task SendAsync(
        IDomainEvent domainEvent,
        NotificationEventType eventType,
        Guid contractId,
        Audience audience,
        Guid? providerId,
        Action<Dictionary<string, string>>? addVariables,
        CancellationToken cancellationToken)
    {
        var contract = await _contractRepository.GetByIdAsync(contractId);
        if (contract is null)
        {
            _logger.LogWarning("Monthly service notification {EventType} found no contract {ContractId}.", eventType, contractId);
            await _intentCoordinator.SkipAsync(domainEvent, eventType, "Contract no longer exists.", cancellationToken);
            return;
        }

        var customer = await _customerRepository.GetByIdAsync(contract.CustomerId);
        var provider = (providerId ?? contract.ProviderId) is { } pid ? await _providerRepository.GetByIdAsync(pid) : null;
        if (audience == Audience.Provider && provider is null)
        {
            await _intentCoordinator.SkipAsync(domainEvent, eventType, "No professional to notify.", cancellationToken);
            return;
        }

        var variables = new Dictionary<string, string>
        {
            ["CustomerName"] = customer?.Name ?? "Customer",
            ["ProviderName"] = provider?.DisplayName ?? "Your professional",
            ["PlanName"] = contract.PlanNameSnapshot,
            ["Schedule"] = DescribeSchedule(contract),
            ["VisitTime"] = contract.VisitStartTime.ToString("h:mm tt", Formats)
        };
        addVariables?.Invoke(variables);

        await _intentCoordinator.DeliverAsync(
            domainEvent,
            eventType,
            async ct =>
            {
                var owner = audience == Audience.Customer
                    ? DeviceTokenOwner.ForCustomer(contract.CustomerId)
                    : DeviceTokenOwner.ForProvider(provider!.Id);
                var recipient = await _dispatchService.ResolveRecipientAsync(owner, ct);
                await _dispatchService.DispatchAsync(contract.CustomerId, eventType, recipient, variables, cancellationToken: ct);
            },
            cancellationToken);
    }

    private static string Ordinal(int day) => day switch
    {
        1 or 21 => $"{day}st",
        2 or 22 => $"{day}nd",
        3 or 23 => $"{day}rd",
        _ => $"{day}th"
    };

    private static string FormatDay(DateOnly date) => date.ToString("ddd, d MMM", Formats);

    /// <summary>"Mon-Sat", "3 times a week (Mon, Wed, Fri)" or "4 times a month (1, 8, 15, 22)" - plain words for a message.</summary>
    public static string DescribeSchedule(MonthlyServiceContract contract)
    {
        if (contract.FrequencySnapshot == MonthlyServiceFrequency.TimesPerMonth)
        {
            return $"{contract.TimesPerPeriodSnapshot} times a month (on the {string.Join(", ", MonthDays.FromMask(contract.MonthDaysMask).Select(Ordinal))})";
        }

        var days = MonthlyServiceEngine.Days(contract.Weekdays);
        string text = days.Count switch
        {
            7 => "every day",
            6 when !days.Contains(DayOfWeek.Sunday) => "Mon-Sat",
            5 when !days.Contains(DayOfWeek.Sunday) && !days.Contains(DayOfWeek.Saturday) => "Mon-Fri",
            _ => string.Join(", ", days.Select(d => d.ToString()[..3]))
        };

        return contract.FrequencySnapshot == MonthlyServiceFrequency.TimesPerWeek
            ? $"{contract.TimesPerPeriodSnapshot} times a week ({text})"
            : text;
    }
}
