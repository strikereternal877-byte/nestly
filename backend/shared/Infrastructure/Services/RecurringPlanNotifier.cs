using Microsoft.Extensions.Logging;
using Nestly.Application;
using Nestly.Application.Notifications;
using Nestly.Application.RecurringBookings;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>See <see cref="IRecurringPlanNotifier"/>.</summary>
public class RecurringPlanNotifier : IRecurringPlanNotifier
{
    private readonly ICustomerRepository _customerRepository;
    private readonly IServiceRepository _serviceRepository;
    private readonly ISlotWindowRepository _slotWindowRepository;
    private readonly IDeviceTokenRepository _deviceTokenRepository;
    private readonly INotificationDispatchService _dispatchService;
    private readonly ILogger<RecurringPlanNotifier> _logger;

    public RecurringPlanNotifier(
        ICustomerRepository customerRepository,
        IServiceRepository serviceRepository,
        ISlotWindowRepository slotWindowRepository,
        IDeviceTokenRepository deviceTokenRepository,
        INotificationDispatchService dispatchService,
        ILogger<RecurringPlanNotifier> logger)
    {
        _customerRepository = customerRepository;
        _serviceRepository = serviceRepository;
        _slotWindowRepository = slotWindowRepository;
        _deviceTokenRepository = deviceTokenRepository;
        _dispatchService = dispatchService;
        _logger = logger;
    }

    public async Task NotifyChangedAsync(RecurringBookingPlan plan, RecurringPlanChange change, CancellationToken cancellationToken = default)
    {
        try
        {
            var customer = await _customerRepository.GetByIdAsync(plan.CustomerId);
            if (customer is null)
            {
                _logger.LogWarning("Recurring plan {PlanId}'s customer {CustomerId} was not found; skipping the {Kind} confirmation.", plan.Id, plan.CustomerId, change.Kind);
                return;
            }

            var service = await _serviceRepository.GetByIdAsync(plan.ServiceId);
            var window = await _slotWindowRepository.GetByIdAsync(plan.SlotWindowId);
            var deviceTokens = await _deviceTokenRepository.ListActiveByOwnerAsync(DeviceTokenOwner.ForCustomer(plan.CustomerId));
            var recipient = new NotificationRecipient(customer.Mobile, customer.Email, deviceTokens.Select(t => t.Token).ToList());

            var message = RecurringPlanChangeMessages.Build(plan, WindowLabel(window), change);

            var variables = new Dictionary<string, string>
            {
                ["CustomerName"] = customer.Name,
                ["ServiceName"] = service?.Name ?? string.Empty,
                ["ActionTitle"] = message.Title,
                ["Summary"] = message.Summary,
                ["Details"] = message.Details
            };

            await _dispatchService.DispatchAsync(
                plan.CustomerId, NotificationEventType.RecurringPlanChanged, recipient, variables, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            // The change has already happened and is what matters; a confirmation that cannot be sent must not undo
            // it or surface as a failure of the customer's action.
            _logger.LogError(ex, "Could not send the {Kind} confirmation for recurring plan {PlanId}.", change.Kind, plan.Id);
        }
    }

    /// <summary>"Morning 09:00-13:00" - the window's own name and hours, as the customer picked it.</summary>
    private static string WindowLabel(SlotWindow? window) =>
        window is null
            ? string.Empty
            : $"{window.Name} {window.StartTime:hh\\:mm}-{window.EndTime:hh\\:mm}";
}
