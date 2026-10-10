using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Notifications;
using Nestly.Application.Pricing;
using Nestly.Application.ProviderManagement;
using Nestly.Application.RecurringBookings;
using Nestly.Application.Wallet;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// The Hangfire recurring job for task 185. Runs on a daily cron
/// (registered in <c>BackgroundJobRegistration</c>) and, for every active
/// plan due within <see cref="RecurringBookingOptions.LeadTimeDays"/>, calls
/// <see cref="IBookingService.CreateAsync"/> - the exact orchestration a
/// customer's own "Book now" tap uses (task 58) - never a second,
/// parallel booking-creation path. A failure (slot gone, address gone,
/// service deactivated, anything the orchestration itself rejects) is
/// treated as "skip and notify", never a silent drop and never an attempt to
/// pick a different slot on the customer's behalf (PRODUCT-ENHANCEMENTS.md
/// section 2).
///
/// <b>Task 297 - the provider dimension.</b> Every generated booking now
/// carries <see cref="Booking.RecurringBookingPlanId"/> (task 296's FK),
/// passed through the same <see cref="IBookingService.CreateAsync"/> call
/// rather than stamped on afterwards. Once the booking exists, the generator
/// asks the question the row is really about: can the professional this
/// customer already knows serve this date? Provider assignment itself is not
/// part of booking creation for a one-off booking either - a new booking is
/// <see cref="BookingStatus.PaymentPending"/>, and
/// <c>ProviderAutoAssignmentHandler</c> places a provider only once it
/// reaches <see cref="BookingStatus.AwaitingFulfilment"/> - so this is a
/// forecast made deliberately early, which is the entire reason the job runs
/// <see cref="RecurringBookingOptions.LeadTimeDays"/> ahead of the date
/// ("enough lead time to catch and surface a problem before the customer
/// expects the visit"). It records one of three booked outcomes:
///
/// <list type="bullet">
/// <item><see cref="RecurringBookingOccurrenceOutcome.Booked"/> - no standing
/// provider yet, or the standing provider can serve the date.</item>
/// <item><see cref="RecurringBookingOccurrenceOutcome.BookedProviderReassigned"/> -
/// the standing provider cannot, but a substitute can. The occurrence is
/// handed to the existing reassignment flow instead of being skipped;
/// <c>ProviderAutoAssignmentHandler</c> makes the swap for real, which raises
/// <c>BookingProviderChangedEvent</c> and tells the customer (task 295).</item>
/// <item><see cref="RecurringBookingOccurrenceOutcome.BookedProviderUnavailable"/> -
/// nobody is eligible. Recorded with its reason and logged as a warning, not
/// dropped; the booking joins the manual admin queue, exactly where an
/// unstaffable one-off booking already goes.</item>
/// </list>
///
/// The search for a substitute is <see cref="IEligibleProviderSearchService"/>
/// - the same ranking-plus-gate walk the auto-assignment engine performs, not
/// a second matcher with its own idea of who is available.
/// </summary>
public class RecurringBookingSchedulerService : IRecurringBookingSchedulerService
{
    /// <summary>Codes <c>BookingSummaryService</c>/<c>BookingService</c> return for a slot/availability rejection specifically, as opposed to some other orchestration failure - classified separately in the occurrence log and used to decide notification wording, though today both outcomes dispatch the same <see cref="NotificationEventType.RecurringBookingSkipped"/> event.</summary>
    private static readonly HashSet<string> SlotUnavailableErrorCodes =
    [
        "Booking.NotServiceable",
        "Booking.SlotNotAvailable",
        "Booking.SlotCapacityReached"
    ];

    private readonly IRecurringBookingPlanRepository _planRepository;
    private readonly IRecurringBookingOccurrenceRepository _occurrenceRepository;
    private readonly IBookingService _bookingService;
    private readonly ICustomerRepository _customerRepository;
    private readonly IServiceRepository _serviceRepository;
    private readonly ISlotWindowRepository _slotWindowRepository;
    private readonly IDeviceTokenRepository _deviceTokenRepository;
    private readonly INotificationDispatchService _notificationDispatchService;
    private readonly IRecurringPlanProviderContinuityService _continuityService;
    private readonly IProviderAssignmentEligibilityService _eligibilityService;
    private readonly IEligibleProviderSearchService _eligibleProviderSearch;
    private readonly IWalletService _walletService;
    private readonly INotificationEventRepository _notificationEvents;
    private readonly RecurringBookingOptions _options;
    private readonly ILogger<RecurringBookingSchedulerService> _logger;

    public RecurringBookingSchedulerService(
        IRecurringBookingPlanRepository planRepository,
        IRecurringBookingOccurrenceRepository occurrenceRepository,
        IBookingService bookingService,
        ICustomerRepository customerRepository,
        IServiceRepository serviceRepository,
        ISlotWindowRepository slotWindowRepository,
        IDeviceTokenRepository deviceTokenRepository,
        INotificationDispatchService notificationDispatchService,
        IRecurringPlanProviderContinuityService continuityService,
        IProviderAssignmentEligibilityService eligibilityService,
        IEligibleProviderSearchService eligibleProviderSearch,
        IWalletService walletService,
        INotificationEventRepository notificationEvents,
        IOptions<RecurringBookingOptions> options,
        ILogger<RecurringBookingSchedulerService> logger)
    {
        _planRepository = planRepository;
        _occurrenceRepository = occurrenceRepository;
        _bookingService = bookingService;
        _customerRepository = customerRepository;
        _serviceRepository = serviceRepository;
        _slotWindowRepository = slotWindowRepository;
        _deviceTokenRepository = deviceTokenRepository;
        _notificationDispatchService = notificationDispatchService;
        _continuityService = continuityService;
        _eligibilityService = eligibilityService;
        _eligibleProviderSearch = eligibleProviderSearch;
        _walletService = walletService;
        _notificationEvents = notificationEvents;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ProcessDueOccurrencesAsync(CancellationToken cancellationToken)
    {
        var horizon = DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(_options.LeadTimeDays);
        var duePlans = await _planRepository.ListDueAsync(horizon);

        _logger.LogInformation("Recurring booking scheduler sweep: {Count} plan(s) due on or before {Horizon}.", duePlans.Count, horizon);

        foreach (var plan in duePlans)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await ProcessPlanAsync(plan, horizon, cancellationToken);
            }
            catch (Exception ex)
            {
                // One plan's unexpected failure must not abort the sweep for
                // every other plan in the batch - logged with full detail
                // (server-side only, never surfaced to a customer) and left
                // for the next run/retry to pick back up, since the plan's
                // NextOccurrenceDate was never advanced.
                _logger.LogError(ex, "Unexpected failure processing recurring plan {PlanId}.", plan.Id);
            }
        }
    }

    /// <summary>
    /// Books every occurrence of <paramref name="plan"/> that falls on or before
    /// <paramref name="horizon"/>, not just the next one. The sweep runs once a day,
    /// so a daily plan handled one date per sweep would only ever book the visit for
    /// the day the sweep runs - no lead time at all for payment or assignment.
    /// Looping keeps it <see cref="RecurringBookingOptions.LeadTimeDays"/> ahead
    /// (weekly/monthly plans still book exactly one date, since their next date is
    /// already past the horizon). The iteration cap is a runaway guard only.
    /// </summary>
    private async Task ProcessPlanAsync(RecurringBookingPlan plan, DateOnly horizon, CancellationToken cancellationToken)
    {
        for (var i = 0; i <= _options.LeadTimeDays; i++)
        {
            if (plan.Status != RecurringBookingPlanStatus.Active
                || plan.NextOccurrenceDate > horizon
                || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!(await ProcessOccurrenceAsync(plan, notify: true, cancellationToken)).Advanced)
            {
                return;
            }
        }
    }

    /// <param name="Advanced">True when the plan's pointer advanced; false when nothing was done (already recorded) and looping further would repeat the same date.</param>
    /// <param name="Date">The date that was processed.</param>
    /// <param name="BookingId">The booking created, or null when the date was skipped.</param>
    /// <param name="Payable">What the created booking still needs paid (0 for a skipped date).</param>
    private sealed record OccurrenceResult(bool Advanced, DateOnly Date, Guid? BookingId, decimal Payable);

    /// <param name="notify">False for a prepaid purchase: the customer is on the payment page, which lists what was and was not booked, so a per-visit notification each would only be noise.</param>
    private async Task<OccurrenceResult> ProcessOccurrenceAsync(RecurringBookingPlan plan, bool notify, CancellationToken cancellationToken)
    {
        var occurrenceDate = plan.NextOccurrenceDate;

        // Idempotency guard (BackgroundJobRegistration: "jobs must be
        // idempotent - a retry re-runs the whole method"). A prior run that
        // got as far as recording the occurrence but crashed before saving
        // the plan's advanced pointer would otherwise double-book on retry.
        if (await _occurrenceRepository.ExistsForDateAsync(plan.Id, occurrenceDate))
        {
            _logger.LogWarning(
                "Recurring plan {PlanId} already has a recorded occurrence for {ScheduledDate}; skipping re-processing.",
                plan.Id, occurrenceDate);
            return new OccurrenceResult(false, occurrenceDate, null, 0m);
        }

        var addOns = plan.AddOns.Select(a => new AddOnSelection(a.AddOnId, a.Quantity)).ToList();
        // ApplyWalletCredit reuses the plan's own choice (task 370) - there
        // is no per-occurrence UI moment to ask the customer again, unlike
        // an ad-hoc booking's checkbox.
        var request = new BookingSummaryRequest(
            plan.ServiceId, plan.CityId, plan.AddressId, plan.LocalityId, plan.SlotWindowId,
            occurrenceDate, plan.Quantity, addOns, ApplyWalletCredit: plan.ApplyWalletCredit);

        // Task 297: the plan id goes in through the orchestration's own
        // parameter, so the occurrence is a plan booking from its very first
        // INSERT rather than a one-off that gets adopted a moment later.
        Result<BookingDetailResponse> result = await _bookingService.CreateAsync(plan.CustomerId, request, plan.Id);
        Guid? bookedId = null;
        decimal payable = 0m;

        if (result.IsSuccess)
        {
            var (outcome, providerNote) = await ResolveProviderPlacementAsync(plan, result.Value.Id, occurrenceDate, cancellationToken);
            // Task (recurring payment-timing fix): whether this occurrence
            // still needs payment travels with the outcome, not just the
            // booking id - see NotifyAsync's doc comment on why a recurring
            // occurrence cannot reuse the one-off flow's "PaymentPending is
            // silent" rule.
            var requiresPayment = result.Value.Status == BookingStatus.PaymentPending;
            await RecordOccurrenceAsync(plan, occurrenceDate, outcome, result.Value.Id, providerNote, requiresPayment, result.Value.FinalPayable, result.Value.WalletCreditApplied ?? 0m, notify, cancellationToken);
            plan.RecordOccurrenceBooked(occurrenceDate);
            bookedId = result.Value.Id;
            payable = result.Value.FinalPayable;

            if (notify && plan.ApplyWalletCredit && !requiresPayment)
            {
                await WarnIfWalletRunningLowAsync(plan, result.Value);
            }
        }
        else
        {
            var outcome = SlotUnavailableErrorCodes.Contains(result.Error.Code)
                ? RecurringBookingOccurrenceOutcome.SkippedSlotUnavailable
                : RecurringBookingOccurrenceOutcome.SkippedOrchestrationRejected;

            _logger.LogWarning(
                "Recurring plan {PlanId} occurrence for {ScheduledDate} skipped ({ErrorCode}): {ErrorMessage}",
                plan.Id, occurrenceDate, result.Error.Code, result.Error.Message);

            await RecordOccurrenceAsync(plan, occurrenceDate, outcome, null, result.Error.Message, requiresPayment: false, payableAmount: 0m, walletPaid: 0m, notify, cancellationToken);
            plan.RecordOccurrenceSkipped(occurrenceDate);
        }

        await _planRepository.UpdateAsync(plan);
        return new OccurrenceResult(true, occurrenceDate, bookedId, payable);
    }

    /// <summary>
    /// A plan that pays each visit from the wallet has just had a visit paid in full; if what is left would no
    /// longer cover the next few, tell the customer now rather than when a visit is left waiting on payment.
    /// Quiet in three cases: the visit still needed paying (the "payment due" message already says so), the
    /// balance comfortably covers the next few visits, or they were already warned in the last 24 hours. Never
    /// throws - a missed warning must not undo a visit that was booked correctly.
    /// </summary>
    private async Task WarnIfWalletRunningLowAsync(RecurringBookingPlan plan, BookingDetailResponse visit)
    {
        try
        {
            var balance = (await _walletService.GetBalanceAsync(plan.CustomerId)).Value.Balance;

            // What one visit costs: a persisted booking's Price.TotalPayable is only the part still to be paid
            // after wallet credit, which for a visit the wallet covered in full is zero - so add back what the
            // wallet put in, or "three visits' worth" would be zero and the warning could never fire.
            decimal costOfOneVisit = visit.Price.TotalPayable + (visit.WalletCreditApplied ?? 0m);
            if (costOfOneVisit <= 0m || balance >= costOfOneVisit * _options.WalletLowBalanceVisits)
            {
                return;
            }

            var lastWarned = await _notificationEvents.GetLatestCreatedAtUtcAsync(plan.CustomerId, NotificationEventType.WalletLowBalance);
            if (lastWarned is { } last && last > DateTime.UtcNow.AddHours(-24))
            {
                return;
            }

            var customer = await _customerRepository.GetByIdAsync(plan.CustomerId);
            if (customer is null)
            {
                return;
            }

            var service = await _serviceRepository.GetByIdAsync(plan.ServiceId);
            var deviceTokens = await _deviceTokenRepository.ListActiveByOwnerAsync(DeviceTokenOwner.ForCustomer(plan.CustomerId));
            var recipient = new NotificationRecipient(customer.Mobile, customer.Email, deviceTokens.Select(t => t.Token).ToList());

            var variables = new Dictionary<string, string>
            {
                ["CustomerName"] = customer.Name,
                ["ServiceName"] = service?.Name ?? string.Empty,
                ["WalletBalance"] = $"₹{balance:0.00}",
                ["Visits"] = _options.WalletLowBalanceVisits.ToString()
            };

            await _notificationDispatchService.DispatchAsync(plan.CustomerId, NotificationEventType.WalletLowBalance, recipient, variables);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not check or send the low wallet balance warning for recurring plan {PlanId}.", plan.Id);
        }
    }

    public async Task<PrepaidCycleMaterialization> MaterializePrepaidCycleAsync(
        RecurringBookingPlan plan, DateOnly? coveredThroughDate, CancellationToken cancellationToken)
    {
        if (!plan.PrepaidUpfront)
        {
            throw new InvalidOperationException("Only a prepaid plan is materialised up front; other plans are generated by the daily job.");
        }

        var skipped = new List<DateOnly>();
        int booked = 0;
        decimal totalPayable = 0m;
        Guid? firstBookingId = null;
        DateOnly? firstVisitDate = null;

        // The iteration cap is a runaway guard only: a bounded plan stops by itself
        // once its window ends, and MaxPrepaidVisits is enforced when the plan is
        // created.
        for (var i = 0; i <= _options.MaxPrepaidVisits; i++)
        {
            if (plan.Status != RecurringBookingPlanStatus.Active
                || (coveredThroughDate is { } through && plan.NextOccurrenceDate > through))
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var result = await ProcessOccurrenceAsync(plan, notify: false, cancellationToken);
            if (!result.Advanced)
            {
                break;
            }

            if (result.BookingId is { } bookingId)
            {
                booked++;
                totalPayable += result.Payable;
                if (firstBookingId is null)
                {
                    firstBookingId = bookingId;
                    firstVisitDate = result.Date;
                }
            }
            else
            {
                skipped.Add(result.Date);
            }
        }

        return new PrepaidCycleMaterialization(booked, skipped, firstBookingId, firstVisitDate, totalPayable);
    }

    public async Task ProcessPrepaidRenewalsAsync(CancellationToken cancellationToken)
    {
        var horizon = DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(_options.PrepaidRenewalLeadDays);
        var plans = await _planRepository.ListPrepaidRenewalDueAsync(horizon);

        _logger.LogInformation("Prepaid renewal sweep: {Count} open-ended prepaid plan(s) need their next cycle.", plans.Count);

        foreach (var plan in plans)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await RenewPrepaidCycleAsync(plan, cancellationToken);
            }
            catch (Exception ex)
            {
                // One plan's failure must not stop the others; the plan keeps its old
                // PrepaidThroughDate, so the next sweep picks it up again.
                _logger.LogError(ex, "Unexpected failure renewing prepaid recurring plan {PlanId}.", plan.Id);
            }
        }
    }

    private async Task RenewPrepaidCycleAsync(RecurringBookingPlan plan, CancellationToken cancellationToken)
    {
        // The next cycle starts where the plan's next visit is due (later than the day after
        // the paid coverage if a paused period was skipped) and runs for a full cycle.
        var cycleStart = plan.NextOccurrenceDate > plan.PrepaidThroughDate!.Value
            ? plan.NextOccurrenceDate
            : plan.PrepaidThroughDate.Value.AddDays(1);
        var coveredThrough = cycleStart.AddDays(_options.PrepaidCycleDays - 1);
        var cycle = await MaterializePrepaidCycleAsync(plan, coveredThrough, cancellationToken);

        if (cycle.FirstBookingId is not { } leadBookingId)
        {
            // Nothing in the next cycle could be booked, so there is nothing to pay for.
            // Move the coverage window forward regardless, or this plan would be picked
            // up again by every sweep.
            plan.AdvancePrepaidCoverage(coveredThrough);
            await _planRepository.UpdateAsync(plan);
            return;
        }

        plan.BeginPrepaymentCycle(leadBookingId, coveredThrough);
        await _planRepository.UpdateAsync(plan);

        await NotifyRenewalDueAsync(plan, leadBookingId, cycle.FirstVisitDate!.Value, cycle.TotalPayable, cancellationToken);
    }

    /// <summary>
    /// Asks the customer to pay for the next prepaid cycle. Reuses the ordinary
    /// "payment due" event; the notification's booking is the cycle's first visit,
    /// whose payment page settles the whole cycle.
    /// </summary>
    private async Task NotifyRenewalDueAsync(RecurringBookingPlan plan, Guid leadBookingId, DateOnly firstVisitDate, decimal totalPayable, CancellationToken cancellationToken)
    {
        var customer = await _customerRepository.GetByIdAsync(plan.CustomerId);
        if (customer is null)
        {
            _logger.LogWarning("Recurring plan {PlanId}'s customer {CustomerId} was not found; skipping the renewal notification.", plan.Id, plan.CustomerId);
            return;
        }

        var service = await _serviceRepository.GetByIdAsync(plan.ServiceId);
        var slotWindow = await _slotWindowRepository.GetByIdAsync(plan.SlotWindowId);
        var deviceTokens = await _deviceTokenRepository.ListActiveByOwnerAsync(DeviceTokenOwner.ForCustomer(plan.CustomerId));
        var recipient = new NotificationRecipient(customer.Mobile, customer.Email, deviceTokens.Select(t => t.Token).ToList());

        var variables = new Dictionary<string, string>
        {
            ["CustomerName"] = customer.Name,
            ["ServiceName"] = service?.Name ?? string.Empty,
            ["SlotDate"] = firstVisitDate.ToString("yyyy-MM-dd"),
            ["SlotWindow"] = slotWindow?.Name ?? string.Empty,
            ["Amount"] = totalPayable.ToString("0.00"),
            ["PaymentWindowHours"] = _options.PaymentWindowHours.ToString()
        };

        await _notificationDispatchService.DispatchAsync(
            plan.CustomerId, NotificationEventType.RecurringBookingPaymentDue, recipient, variables,
            bookingId: leadBookingId, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Task 297's provider-unavailable-on-date handling, for a booking that
    /// has just been created. Returns the outcome to record and the
    /// human-readable note that goes with it (never a raw error or an
    /// internal detail - docs/CODING-STANDARDS.md).
    ///
    /// The decision is a forecast, not an assignment: nothing here writes a
    /// <c>BookingProviderAssignment</c>, because a brand new booking is
    /// PaymentPending and assignment belongs at
    /// <see cref="BookingStatus.AwaitingFulfilment"/> for a recurring
    /// occurrence exactly as it does for a one-off. What it buys is the thing
    /// the row asks for - the generator no longer treats "the regular
    /// professional can't make this date" as a reason to drop the visit, and
    /// the one case where nobody at all can serve it is recorded instead of
    /// disappearing.
    /// </summary>
    private async Task<(RecurringBookingOccurrenceOutcome Outcome, string? Note)> ResolveProviderPlacementAsync(
        RecurringBookingPlan plan,
        Guid bookingId,
        DateOnly occurrenceDate,
        CancellationToken cancellationToken)
    {
        var standingProviderId = await _continuityService.FindStandingProviderAsync(plan.Id, bookingId);
        if (standingProviderId is null)
        {
            // Nothing to keep continuous - the plan's first occurrence, or one
            // whose history carries no provider. Ordinary matching decides,
            // and there is nothing to tell the customer about.
            return (RecurringBookingOccurrenceOutcome.Booked, null);
        }

        if (await _eligibilityService.IsEligibleAsync(standingProviderId.Value, bookingId, cancellationToken))
        {
            return (RecurringBookingOccurrenceOutcome.Booked, null);
        }

        // The fall-back is the existing flow, streamed lazily so an eligible
        // substitute found first costs nothing for the rest of the ranked
        // list (each gate check can be a billed route lookup - task 289).
        await foreach (var substitute in _eligibleProviderSearch
            .FindEligibleAsync(bookingId, [standingProviderId.Value], cancellationToken))
        {
            _logger.LogInformation(
                "Recurring plan {PlanId}: standing provider {StandingProviderId} is unavailable on {ScheduledDate}; "
                + "provider {SubstituteProviderId} is eligible, so booking {BookingId} goes to reassignment rather than being skipped.",
                plan.Id, standingProviderId.Value, occurrenceDate, substitute.ProviderId, bookingId);

            return (
                RecurringBookingOccurrenceOutcome.BookedProviderReassigned,
                "The professional who usually handles this plan is unavailable on this date; another one will be assigned.");
        }

        _logger.LogWarning(
            "Recurring plan {PlanId}: no eligible provider at all for {ScheduledDate}; booking {BookingId} was still created and needs manual assignment.",
            plan.Id, occurrenceDate, bookingId);

        return (
            RecurringBookingOccurrenceOutcome.BookedProviderUnavailable,
            "No professional is currently available for this date; the visit is booked and will be assigned manually.");
    }

    private async Task RecordOccurrenceAsync(
        RecurringBookingPlan plan,
        DateOnly occurrenceDate,
        RecurringBookingOccurrenceOutcome outcome,
        Guid? bookingId,
        string? skipReason,
        bool requiresPayment,
        decimal payableAmount,
        decimal walletPaid,
        bool notify,
        CancellationToken cancellationToken)
    {
        var occurrence = new RecurringBookingOccurrence(Guid.NewGuid(), plan.Id, occurrenceDate, outcome, bookingId, skipReason);
        await _occurrenceRepository.AddAsync(occurrence);

        if (!notify)
        {
            return;
        }

        await NotifyAsync(plan, occurrenceDate, outcome, bookingId, requiresPayment, payableAmount, walletPaid, cancellationToken);
    }

    private async Task NotifyAsync(
        RecurringBookingPlan plan,
        DateOnly occurrenceDate,
        RecurringBookingOccurrenceOutcome outcome,
        Guid? bookingId,
        bool requiresPayment,
        decimal payableAmount,
        decimal walletPaid,
        CancellationToken cancellationToken)
    {
        var customer = await _customerRepository.GetByIdAsync(plan.CustomerId);
        if (customer is null)
        {
            _logger.LogWarning("Recurring plan {PlanId}'s customer {CustomerId} was not found; skipping notification.", plan.Id, plan.CustomerId);
            return;
        }

        var service = await _serviceRepository.GetByIdAsync(plan.ServiceId);
        var slotWindow = await _slotWindowRepository.GetByIdAsync(plan.SlotWindowId);
        var deviceTokens = await _deviceTokenRepository.ListActiveByOwnerAsync(DeviceTokenOwner.ForCustomer(plan.CustomerId));
        var recipient = new NotificationRecipient(customer.Mobile, customer.Email, deviceTokens.Select(t => t.Token).ToList());

        var variables = new Dictionary<string, string>
        {
            ["CustomerName"] = customer.Name,
            ["ServiceName"] = service?.Name ?? string.Empty,
            ["SlotDate"] = occurrenceDate.ToString("yyyy-MM-dd"),
            ["SlotWindow"] = slotWindow?.Name ?? string.Empty,
            ["Amount"] = payableAmount.ToString("0.00"),
            ["PaymentWindowHours"] = _options.PaymentWindowHours.ToString()
        };

        // Task 297: driven by "did a booking happen", not by a single enum
        // member. A BookedProviderReassigned/BookedProviderUnavailable
        // occurrence produced a real visit on the customer's calendar, so
        // telling them it was skipped would be a lie; the staffing detail is
        // an ops concern that lives in the occurrence log and the warning log
        // above, and the customer hears about a provider change from the
        // reassignment flow itself (task 295's ProviderChanged) at the moment
        // it actually happens, rather than from a forecast days earlier that
        // supply may yet make untrue.
        //
        // Recurring payment-timing fix: a booked occurrence with something
        // still payable is NOT "confirmed" - it is sitting in PaymentPending,
        // created unattended with nobody on a checkout screen to see it. Only
        // a booking with nothing left to pay (wallet/subscription covered, or
        // AMC-redeemed) is genuinely confirmed and gets the original message.
        // Of the two payable cases: a plan with auto-charge on gets the
        // advance notice that a charge is coming (never a silent deduction -
        // see NotificationEventType.RecurringAutoChargeScheduled's doc
        // comment), everyone else gets the manual "pay before this expires"
        // message, with the amount and the deadline before
        // BookingExpirySweepJob releases the slot (see
        // RecurringBookingOptions.PaymentWindowHours).
        //
        // A plan that pays from the wallet gets its own message when the wallet could not cover the visit: the
        // generic "payment needed" reminder never said that the wallet ran short, or how much it did pay, so the
        // customer was left to work out why a plan they had set to pay itself was asking for money. (A plan with
        // auto-charge keeps its advance notice - the card is what pays the remainder.)
        var eventType = !outcome.CreatedBooking()
            ? NotificationEventType.RecurringBookingSkipped
            : !requiresPayment
                ? NotificationEventType.RecurringBookingUpcoming
                : plan.AutoChargeEnabled
                    ? NotificationEventType.RecurringAutoChargeScheduled
                    : plan.ApplyWalletCredit
                        ? NotificationEventType.RecurringWalletShortfall
                        : NotificationEventType.RecurringBookingPaymentDue;

        if (eventType == NotificationEventType.RecurringWalletShortfall)
        {
            variables["Amount"] = $"₹{payableAmount:0.00}";
            variables["WalletNote"] = walletPaid > 0
                ? $"₹{walletPaid:0.00} was paid from your wallet."
                : "Your wallet was empty, so nothing could be taken from it.";
        }

        await _notificationDispatchService.DispatchAsync(
            plan.CustomerId, eventType, recipient, variables, bookingId: bookingId, cancellationToken: cancellationToken);
    }
}
