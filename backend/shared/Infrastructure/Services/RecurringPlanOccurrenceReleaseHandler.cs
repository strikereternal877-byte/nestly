using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Notifications;
using Nestly.Application.RecurringBookings;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Interceptors;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Occurrence-count integrity fix: gives a recurring plan's occurrence
/// budget back once a generated booking is confirmed to have never been
/// delivered - see <see cref="RecurringBookingPlan.ReleaseOccurrence"/>'s doc
/// comment for what that unlocks. A fifth independent handler on
/// <see cref="BookingStatusChangedEvent"/>, same shape as
/// <see cref="EscrowReleaseOnCompletionHandler"/>/<see cref="ReferralCancellationFraudSignalHandler"/>/
/// <see cref="ReferralQualifyingBookingHandler"/>.
///
/// <para>
/// <b>Trigger set, and why <see cref="BookingStatus.Refunded"/> needs a
/// second check.</b> <c>BookingLifecycle</c>'s own transition table shows
/// <see cref="BookingStatus.CancelledByCustomer"/>/<see cref="BookingStatus.CancelledByAdmin"/>/
/// <see cref="BookingStatus.Expired"/> are each reachable only from a
/// pre-visit state - nothing transitions to any of them from
/// <see cref="BookingStatus.Completed"/> or <see cref="BookingStatus.RefundPending"/>
/// - so reaching one of those three always means the visit never happened,
/// and the occurrence is released unconditionally.
/// </para>
///
/// <para>
/// <see cref="BookingStatus.Refunded"/> is reachable from <i>two</i> different
/// origins the event payload alone cannot tell apart: directly from a
/// cancelled status (the booking never reached the visit) and from
/// <see cref="BookingStatus.Completed"/> via <see cref="BookingStatus.RefundPending"/>
/// (the visit happened, and this is a later quality-dispute refund of the
/// money only). Releasing the occurrence for the second case would be wrong
/// - a professional did the job, so it must still count against the plan's
/// budget regardless of what happened to the payment afterward. The
/// disambiguator is the booking's own status history: if
/// <see cref="BookingStatus.Completed"/> never appears in it, this refund
/// came from a pre-visit cancellation and the occurrence is released; if it
/// does appear, the occurrence stands.
/// </para>
/// </summary>
public sealed class RecurringPlanOccurrenceReleaseHandler : INotificationHandler<DomainEventNotification<BookingStatusChangedEvent>>
{
    private readonly IBookingRepository _bookingRepository;
    private readonly IRecurringBookingPlanRepository _planRepository;
    private readonly IUnpaidBookingReleaseService _releaseService;
    private readonly ICustomerRepository _customerRepository;
    private readonly IServiceRepository _serviceRepository;
    private readonly IDeviceTokenRepository _deviceTokenRepository;
    private readonly INotificationDispatchService _notificationDispatchService;
    private readonly RecurringBookingOptions _options;
    private readonly ILogger<RecurringPlanOccurrenceReleaseHandler> _logger;

    public RecurringPlanOccurrenceReleaseHandler(
        IBookingRepository bookingRepository,
        IRecurringBookingPlanRepository planRepository,
        IUnpaidBookingReleaseService releaseService,
        ICustomerRepository customerRepository,
        IServiceRepository serviceRepository,
        IDeviceTokenRepository deviceTokenRepository,
        INotificationDispatchService notificationDispatchService,
        IOptions<RecurringBookingOptions> options,
        ILogger<RecurringPlanOccurrenceReleaseHandler> logger)
    {
        _bookingRepository = bookingRepository;
        _planRepository = planRepository;
        _releaseService = releaseService;
        _customerRepository = customerRepository;
        _serviceRepository = serviceRepository;
        _deviceTokenRepository = deviceTokenRepository;
        _notificationDispatchService = notificationDispatchService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task Handle(DomainEventNotification<BookingStatusChangedEvent> notification, CancellationToken cancellationToken)
    {
        var domainEvent = notification.DomainEvent;
        if (domainEvent.ToStatus is not (BookingStatus.CancelledByCustomer or BookingStatus.CancelledByAdmin
            or BookingStatus.Expired or BookingStatus.Refunded))
        {
            return;
        }

        // A prepaid cycle stands or falls with the booking its payment page is keyed by. If that
        // booking dies unpaid, so does the whole unpaid cycle.
        if (domainEvent.ToStatus is BookingStatus.CancelledByCustomer or BookingStatus.CancelledByAdmin or BookingStatus.Expired)
        {
            var leadPlan = await _planRepository.GetByPendingPrepaymentLeadAsync(domainEvent.BookingId);
            if (leadPlan is not null)
            {
                await AbandonUnpaidCycleAsync(leadPlan, domainEvent.BookingId);
            }
        }

        var booking = await _bookingRepository.GetByIdAsync(domainEvent.BookingId);
        if (booking?.RecurringBookingPlanId is not { } planId)
        {
            return;
        }

        if (domainEvent.ToStatus == BookingStatus.Refunded
            && booking.StatusHistory.Any(h => h.ToStatus == BookingStatus.Completed))
        {
            // The visit happened; this refund is a later, money-only dispute
            // resolution and must not give the occurrence back.
            return;
        }

        var plan = await _planRepository.GetByIdAsync(planId);
        if (plan is null)
        {
            _logger.LogWarning(
                "Booking {BookingId} reached {ToStatus} but its recurring plan {PlanId} no longer exists; skipping occurrence release.",
                booking.Id, domainEvent.ToStatus, planId);
            return;
        }

        plan.ReleaseOccurrence();
        await _planRepository.UpdateAsync(plan);

        // An unpaid visit expiring is the only trigger for the "keeps going unpaid" check. (A prepaid plan is exempt:
        // its visits are paid for up front, so there is nothing per visit to leave unpaid.)
        if (domainEvent.ToStatus == BookingStatus.Expired && !plan.PrepaidUpfront)
        {
            await PauseIfRepeatedlyUnpaidAsync(plan);
        }
    }

    /// <summary>
    /// Pauses a pay-as-you-go plan once its most recent visits have all expired unpaid
    /// (<see cref="RecurringBookingOptions.PauseAfterUnpaidVisits"/> in a row).
    ///
    /// <para>
    /// What "in a row" means: walk the plan's visits newest first. A visit still awaiting payment has not been
    /// decided either way and is skipped over; an expired one extends the streak; anything else - paid, completed,
    /// cancelled by the customer, anything that shows they are engaged - ends it. So one missed payment between
    /// paid ones never pauses a plan, and neither does a customer who pays late and then lets one lapse.
    /// </para>
    ///
    /// <para>
    /// An expired visit never reached a professional (assignment only follows payment), so no visit is lost here -
    /// what stops is the plan carrying on creating a booking a day that holds a slot for the whole payment window
    /// and sends a reminder each time. The customer is told, with how to get it going again.
    /// </para>
    /// </summary>
    private async Task PauseIfRepeatedlyUnpaidAsync(RecurringBookingPlan plan)
    {
        var visits = await _bookingRepository.ListByRecurringPlanAsync(plan.Id);

        int streak = 0;
        foreach (var visit in visits.OrderByDescending(v => v.SlotDate))
        {
            if (visit.Status is BookingStatus.PaymentPending or BookingStatus.PaymentFailed)
            {
                continue;
            }

            if (visit.Status != BookingStatus.Expired)
            {
                break;
            }

            streak++;
            if (streak >= _options.PauseAfterUnpaidVisits)
            {
                break;
            }
        }

        if (streak < _options.PauseAfterUnpaidVisits || !plan.PauseForUnpaidVisits())
        {
            return;
        }

        await _planRepository.UpdateAsync(plan);
        _logger.LogInformation(
            "Recurring plan {PlanId} paused: its last {Streak} visit(s) expired unpaid.", plan.Id, streak);

        try
        {
            await NotifyPausedAsync(plan, streak);
        }
        catch (Exception ex)
        {
            // The pause is already saved and is what matters; a notification that cannot be sent must not undo it
            // or fail the event this handler is part of.
            _logger.LogError(ex, "Could not notify the customer that recurring plan {PlanId} was paused.", plan.Id);
        }
    }

    private async Task NotifyPausedAsync(RecurringBookingPlan plan, int unpaidVisits)
    {
        var customer = await _customerRepository.GetByIdAsync(plan.CustomerId);
        if (customer is null)
        {
            _logger.LogWarning("Recurring plan {PlanId}'s customer {CustomerId} was not found; skipping the paused notification.", plan.Id, plan.CustomerId);
            return;
        }

        var service = await _serviceRepository.GetByIdAsync(plan.ServiceId);
        var deviceTokens = await _deviceTokenRepository.ListActiveByOwnerAsync(DeviceTokenOwner.ForCustomer(plan.CustomerId));
        var recipient = new NotificationRecipient(customer.Mobile, customer.Email, deviceTokens.Select(t => t.Token).ToList());

        var variables = new Dictionary<string, string>
        {
            ["CustomerName"] = customer.Name,
            ["ServiceName"] = service?.Name ?? string.Empty,
            ["UnpaidVisits"] = unpaidVisits.ToString()
        };

        await _notificationDispatchService.DispatchAsync(
            plan.CustomerId, NotificationEventType.RecurringPlanPaused, recipient, variables);
    }

    /// <summary>
    /// The unpaid prepaid cycle's lead booking is gone (payment window lapsed, or the customer
    /// cancelled it), so nothing will ever pay for the rest of the cycle. The first cycle ends the
    /// plan; a renewal pauses it (it has delivered paid visits and can be resumed). The cycle's
    /// other unpaid bookings are released so they stop holding provider capacity.
    /// </summary>
    private async Task AbandonUnpaidCycleAsync(RecurringBookingPlan plan, Guid leadBookingId)
    {
        bool changed = plan.PrepaidCyclesPaid == 0 ? plan.AbandonUnpaidFirstCycle() : plan.AbandonUnpaidRenewal();
        if (!changed)
        {
            return;
        }

        await _planRepository.UpdateAsync(plan);

        foreach (var member in await _bookingRepository.ListByRecurringPlanAsync(plan.Id))
        {
            if (member.Id == leadBookingId)
            {
                continue;
            }

            if (member.Status == BookingStatus.PaymentFailed)
            {
                member.TransitionTo(BookingStatus.PaymentPending, "Releasing an unpaid prepaid purchase.");
                await _bookingRepository.UpdateAsync(member);
            }

            if (member.Status == BookingStatus.PaymentPending)
            {
                await _releaseService.ExpireAsync(member, "The prepaid purchase this visit belonged to was not paid for.");
            }
        }

        _logger.LogInformation(
            "Prepaid recurring plan {PlanId}: its unpaid cycle was abandoned when lead booking {LeadBookingId} ended without payment.",
            plan.Id, leadBookingId);
    }
}
