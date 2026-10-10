using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Abstractions.Time;
using Nestly.Application.Bookings;
using Nestly.Application.Cancellations;
using Nestly.Application.Pricing;
using Nestly.Application.RecurringBookings;
using Nestly.Application.Settings;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.Services;

/// <summary>Create/pause/resume/cancel a recurring booking plan and read its state (task 186).</summary>
public class RecurringBookingPlanService : IRecurringBookingPlanService
{
    private readonly IRecurringBookingPlanRepository _planRepository;
    private readonly IRecurringBookingOccurrenceRepository _occurrenceRepository;
    private readonly IBookingSummaryService _bookingSummaryService;
    private readonly IServiceRepository _serviceRepository;
    private readonly IBookingRepository _bookingRepository;
    private readonly IRecurringBookingSchedulerService _schedulerService;
    private readonly ICancellationService _cancellationService;
    private readonly RecurringBookingOptions _options;
    private readonly IBusinessClock _clock;
    private readonly IBookingPolicyProvider _policies;
    private readonly ISlotWindowRepository _slotWindowRepository;
    private readonly IRecurringPlanNotifier _notifier;
    private readonly ILogger<RecurringBookingPlanService> _logger;

    /// <summary>Visit statuses that mean "booked and paid for, still ahead" - what a pause or a time change leaves untouched.</summary>
    private static readonly BookingStatus[] BookedStatuses =
    [
        BookingStatus.Confirmed, BookingStatus.AwaitingFulfilment, BookingStatus.Assigned, BookingStatus.InProgress
    ];

    /// <summary>How far back the plan card looks for the visit it reads the per-visit price from.</summary>
    private const int PriceLookbackDays = 14;

    /// <summary>What an action that cancelled visits actually did, for the confirmation sent afterwards.</summary>
    private sealed record VisitTally(int Count, decimal Fees, decimal Refunded);

    public RecurringBookingPlanService(
        IRecurringBookingPlanRepository planRepository,
        IRecurringBookingOccurrenceRepository occurrenceRepository,
        IBookingSummaryService bookingSummaryService,
        IServiceRepository serviceRepository,
        IBookingRepository bookingRepository,
        IRecurringBookingSchedulerService schedulerService,
        ICancellationService cancellationService,
        IOptions<RecurringBookingOptions> options,
        IBusinessClock clock,
        IBookingPolicyProvider policies,
        ISlotWindowRepository slotWindowRepository,
        IRecurringPlanNotifier notifier,
        ILogger<RecurringBookingPlanService> logger)
    {
        _planRepository = planRepository;
        _occurrenceRepository = occurrenceRepository;
        _bookingSummaryService = bookingSummaryService;
        _serviceRepository = serviceRepository;
        _bookingRepository = bookingRepository;
        _schedulerService = schedulerService;
        _cancellationService = cancellationService;
        _options = options.Value;
        _clock = clock;
        _policies = policies;
        _slotWindowRepository = slotWindowRepository;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<Result<RecurringBookingPlanResponse>> CreateAsync(Guid customerId, CreateRecurringBookingPlanRequest request)
    {
        // Dry-runs the plan's first occurrence through the exact same
        // orchestration a one-off booking preview uses (task 58) - identity,
        // active catalog, serviceable address, valid slot, price snapshot -
        // so a plan can never be created against a combination that would
        // just fail-and-notify on its very first attempt. Nothing here
        // persists a booking; GetSummaryAsync only validates and prices.
        var summaryRequest = new BookingSummaryRequest(
            request.ServiceId, request.CityId, request.AddressId, request.LocalityId,
            request.SlotWindowId, request.StartDate, request.Quantity, request.AddOns,
            ApplyWalletCredit: request.ApplyWalletCredit);

        var summaryResult = await _bookingSummaryService.GetSummaryAsync(customerId, summaryRequest);
        if (summaryResult.IsFailure)
        {
            return summaryResult.Error;
        }

        if (request.PrepaidUpfront)
        {
            return await CreatePrepaidAsync(customerId, request, summaryResult.Value.Service.Name);
        }

        RecurringBookingPlan plan;
        try
        {
            plan = new RecurringBookingPlan(
                Guid.NewGuid(), customerId, request.ServiceId, request.CityId, request.LocalityId,
                request.AddressId, request.SlotWindowId, request.Quantity, request.Frequency,
                request.RecurrenceDayOfWeek, request.RecurrenceDayOfMonth, request.StartDate,
                request.EndDate, request.OccurrenceCount,
                request.AddOns.Select(a => (a.AddOnId, a.Quantity)).ToList(),
                request.ApplyWalletCredit, request.AutoChargeEnabled);
        }
        catch (ArgumentException ex)
        {
            return Error.Validation("RecurringBookingPlan.InvalidRequest", ex.Message);
        }

        await _planRepository.AddAsync(plan);

        return ToResponse(plan, summaryResult.Value.Service.Name);
    }

    /// <summary>
    /// Buys a plan whose whole first cycle is paid in one checkout. The customer has
    /// just placed <paramref name="request"/>.LeadBookingId (visit 1 of the purchase,
    /// not a plan occurrence); this creates the plan, then creates every remaining
    /// visit of the cycle right away as an unpaid booking so the payment page can
    /// charge for all of them at once (see <c>PaymentService.CreateOrderAsync</c>).
    /// A date that cannot be booked is skipped and returned, not charged for.
    ///
    /// <para>
    /// Idempotent per lead booking: the summary page retries this call after a
    /// failure without creating a second booking, so a second call for a lead that
    /// already has its pending plan returns that plan.
    /// </para>
    /// </summary>
    private async Task<Result<RecurringBookingPlanResponse>> CreatePrepaidAsync(
        Guid customerId, CreateRecurringBookingPlanRequest request, string serviceName)
    {
        var lead = await _bookingRepository.GetByIdAsync(request.LeadBookingId!.Value);
        if (lead is null || lead.CustomerId != customerId)
        {
            return Error.NotFound("RecurringBookingPlan.LeadBookingNotFound", "The booking this plan is being bought with does not exist.");
        }

        var existing = await _planRepository.GetByPendingPrepaymentLeadAsync(lead.Id);
        if (existing is not null)
        {
            return ToResponse(existing, serviceName, await SkippedDatesAsync(existing, lead.SlotDate));
        }

        if (lead.Status != BookingStatus.PaymentPending)
        {
            return Error.Business(
                "RecurringBookingPlan.LeadBookingNotPayable",
                $"A prepaid plan is bought together with a booking awaiting payment; this one is '{lead.Status}'.");
        }

        if (request.StartDate <= lead.SlotDate)
        {
            return Error.Validation(
                "RecurringBookingPlan.InvalidRequest",
                "A prepaid plan's repeats must start after the booking it is bought with.");
        }

        RecurringBookingPlan plan;
        try
        {
            plan = new RecurringBookingPlan(
                Guid.NewGuid(), customerId, request.ServiceId, request.CityId, request.LocalityId,
                request.AddressId, request.SlotWindowId, request.Quantity, request.Frequency,
                request.RecurrenceDayOfWeek, request.RecurrenceDayOfMonth, request.StartDate,
                request.EndDate, request.OccurrenceCount,
                request.AddOns.Select(a => (a.AddOnId, a.Quantity)).ToList(),
                applyWalletCredit: false, autoChargeEnabled: false,
                prepaidUpfront: true, prepaidLeadBookingId: lead.Id);
        }
        catch (ArgumentException ex)
        {
            return Error.Validation("RecurringBookingPlan.InvalidRequest", ex.Message);
        }

        // The window of dates this purchase covers. A bounded plan is bought for exactly its
        // stated visits: its end date is pinned to the last of them, so a date that turns out
        // to be unavailable is dropped from the purchase instead of being made up for later
        // (the daily job's usual "a skip does not consume the budget" rule would silently
        // sell more dates than the customer agreed to pay for).
        DateOnly coveredThrough;
        if (plan.IsOpenEnded)
        {
            // At least one repeat is always inside the cycle, even for a cadence longer than it.
            var cycleEnd = lead.SlotDate.AddDays(_options.PrepaidCycleDays - 1);
            coveredThrough = cycleEnd > plan.NextOccurrenceDate ? cycleEnd : plan.NextOccurrenceDate;
        }
        else
        {
            var dates = plan.PreviewUpcomingOccurrenceDates(_options.MaxPrepaidVisits);
            if (dates.Count == 0)
            {
                return Error.Validation("RecurringBookingPlan.InvalidRequest", "This plan has no visits to buy.");
            }

            if (dates.Count + 1 > _options.MaxPrepaidVisits)
            {
                return Error.Validation(
                    "RecurringBookingPlan.TooManyVisits",
                    $"A prepaid plan covers at most {_options.MaxPrepaidVisits} visits, including the booking you are placing now.");
            }

            coveredThrough = dates[^1];
            try
            {
                plan.SetOccurrenceBounds(coveredThrough, request.OccurrenceCount);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return Error.Validation("RecurringBookingPlan.InvalidRequest", ex.Message);
            }
        }

        await _planRepository.AddAsync(plan);

        var cycle = await _schedulerService.MaterializePrepaidCycleAsync(plan, coveredThrough, CancellationToken.None);

        plan.BeginPrepaymentCycle(lead.Id, coveredThrough);
        await _planRepository.UpdateAsync(plan);

        return ToResponse(plan, serviceName, cycle.SkippedDates);
    }

    public async Task<Result<IReadOnlyList<RecurringBookingPlanResponse>>> ListAsync(Guid customerId)
    {
        var plans = await _planRepository.ListByCustomerAsync(customerId);
        var card = await LoadCardDataAsync(plans);
        var responses = new List<RecurringBookingPlanResponse>(plans.Count);
        foreach (var plan in plans)
        {
            responses.Add(Enrich(ToResponse(plan, await ResolveServiceNameAsync(plan.ServiceId)), plan, card));
        }

        return Result.Success<IReadOnlyList<RecurringBookingPlanResponse>>(responses);
    }

    public async Task<Result<RecurringBookingPlanResponse>> GetAsync(Guid customerId, Guid planId)
    {
        var planResult = await ResolveOwnedPlanAsync(customerId, planId);
        if (planResult.IsFailure)
        {
            return planResult.Error;
        }

        var plan = planResult.Value;
        var card = await LoadCardDataAsync([plan]);
        return Enrich(ToResponse(plan, await ResolveServiceNameAsync(plan.ServiceId)), plan, card);
    }

    public async Task<Result<RecurringBookingPlanResponse>> PauseAsync(Guid customerId, Guid planId)
    {
        var result = await TransitionAsync(customerId, planId, plan => plan.Pause(), "RecurringBookingPlan.InvalidPause");
        if (result.IsSuccess)
        {
            await ConfirmChangeAsync(planId, RecurringPlanChangeKind.Paused);
        }

        return result;
    }

    // Today is passed so a plan that sat paused past some of its dates does not try to book - and notify the customer
    // about - each date that has already gone by (see RecurringBookingPlan.Resume).
    public async Task<Result<RecurringBookingPlanResponse>> ResumeAsync(Guid customerId, Guid planId)
    {
        // A plan support paused stays paused until support resumes it - otherwise the pause would last only until the
        // customer next opened the app.
        var owned = await ResolveOwnedPlanAsync(customerId, planId);
        if (owned.IsSuccess
            && owned.Value.Status == RecurringBookingPlanStatus.Paused
            && owned.Value.PauseReason == RecurringBookingPauseReason.Admin)
        {
            return Error.Business(
                "RecurringBookingPlan.PausedBySupport",
                "Our support team paused this plan, so it has to be resumed by them. Please contact support.");
        }

        var result = await TransitionAsync(customerId, planId, plan => plan.Resume(_clock.Today), "RecurringBookingPlan.InvalidResume");
        if (result.IsSuccess)
        {
            await ConfirmChangeAsync(planId, RecurringPlanChangeKind.Resumed);
        }

        return result;
    }

    public async Task<Result<RecurringBookingPlanResponse>> SkipVisitsAsync(Guid customerId, Guid planId, SkipVisitsRequest request)
    {
        var planResult = await ResolveOwnedPlanAsync(customerId, planId);
        if (planResult.IsFailure)
        {
            return planResult.Error;
        }

        var plan = planResult.Value;
        var today = _clock.Today;

        if (request.ResumeOn <= today)
        {
            return Error.Validation("RecurringBookingPlan.InvalidSkipDate", "Choose a date after today.");
        }

        if (request.ResumeOn.DayNumber - today.DayNumber > _options.MaxSkipDays)
        {
            return Error.Validation(
                "RecurringBookingPlan.SkipTooFar",
                $"You can skip visits for up to {_options.MaxSkipDays} days at a time.");
        }

        if (plan.SkipRangesUsed >= _options.MaxSkipRangesPerPlan)
        {
            return Error.Business(
                "RecurringBookingPlan.SkipLimitReached",
                $"You've used the {_options.MaxSkipRangesPerPlan} skip periods this plan allows. You can still pause the plan and resume it whenever you like.");
        }

        // The scheduler books a few days ahead, so the plan's cursor (the next date still to be generated) can already be
        // past the date the customer is skipping to: every visit before it exists as a booking and there is nothing left to
        // move. The customer can still want those booked visits gone, so with CancelBookedVisits that is all this does;
        // without it there is genuinely nothing to skip, and saying so beats quietly doing nothing.
        bool nothingLeftToMove = plan.Status == RecurringBookingPlanStatus.Active
            && !plan.PrepaidUpfront
            && request.ResumeOn <= plan.NextOccurrenceDate;

        if (nothingLeftToMove && request.CancelBookedVisits)
        {
            var cancelOnly = await CancelBookedVisitsBeforeAsync(customerId, plan, today, request.ResumeOn);
            await ConfirmChangeAsync(plan.Id, RecurringPlanChangeKind.VisitsSkipped, cancelOnly, request.ResumeOn);
            return ToResponse(plan, await ResolveServiceNameAsync(plan.ServiceId));
        }

        try
        {
            plan.SkipVisitsUntil(request.ResumeOn, today);
        }
        catch (InvalidOperationException ex)
        {
            return Error.Business("RecurringBookingPlan.InvalidSkip", ex.Message);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Error.Validation("RecurringBookingPlan.InvalidSkipDate", ex.Message);
        }

        await _planRepository.UpdateAsync(plan);

        VisitTally? tally = null;
        if (request.CancelBookedVisits)
        {
            tally = await CancelBookedVisitsBeforeAsync(customerId, plan, today, request.ResumeOn);
        }

        await ConfirmChangeAsync(plan.Id, RecurringPlanChangeKind.VisitsSkipped, tally, request.ResumeOn);
        return ToResponse(plan, await ResolveServiceNameAsync(plan.ServiceId));
    }

    public async Task<Result<RecurringBookingPlanResponse>> ChangeSlotAsync(Guid customerId, Guid planId, ChangePlanSlotRequest request)
    {
        var planResult = await ResolveOwnedPlanAsync(customerId, planId);
        if (planResult.IsFailure)
        {
            return planResult.Error;
        }

        var plan = planResult.Value;
        if (plan.Status != RecurringBookingPlanStatus.Active)
        {
            // A paused plan's next date may be long past, which no window can serve - so there is nothing sensible to
            // check the new window against. Resuming first moves it forward.
            return Error.Business(
                "RecurringBookingPlan.SlotChangeNotAllowed",
                plan.Status == RecurringBookingPlanStatus.Paused
                    ? "Resume the plan first, then change its time."
                    : $"A {plan.Status.ToString().ToLowerInvariant()} plan's time can't be changed.");
        }

        if (plan.PrepaidUpfront)
        {
            return Error.Business(
                "RecurringBookingPlan.SlotChangeNotAllowed",
                "A prepaid plan's visits are already booked. Reschedule individual visits instead.");
        }

        if (request.SlotWindowId == plan.SlotWindowId)
        {
            return Error.Business("RecurringBookingPlan.SlotChangeNotAllowed", "The plan already uses that time.");
        }

        // The new window must be able to serve the very next visit the plan will create - the same validation a
        // customer's own booking goes through (serviceable address, slot available, bookable that day). A window
        // that cannot would make the next visit fail and be skipped, which is exactly what this check prevents.
        var probe = new BookingSummaryRequest(
            plan.ServiceId, plan.CityId, plan.AddressId, plan.LocalityId, request.SlotWindowId,
            plan.NextOccurrenceDate, plan.Quantity,
            plan.AddOns.Select(a => new AddOnSelection(a.AddOnId, a.Quantity)).ToList());
        var summary = await _bookingSummaryService.GetSummaryAsync(customerId, probe);
        if (summary.IsFailure)
        {
            return Error.Business(
                "RecurringBookingPlan.SlotNotAvailable",
                $"That time isn't available for your next visit on {plan.NextOccurrenceDate:ddd, d MMM yyyy}. {summary.Error.Message}");
        }

        try
        {
            plan.ChangeSlotWindow(request.SlotWindowId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Error.Business("RecurringBookingPlan.SlotChangeNotAllowed", ex.Message);
        }

        await _planRepository.UpdateAsync(plan);
        await ConfirmChangeAsync(plan.Id, RecurringPlanChangeKind.TimeChanged);
        return ToResponse(plan, await ResolveServiceNameAsync(plan.ServiceId));
    }

    /// <summary>
    /// Cancels the plan's already-booked visits dated in [from, before), through the ordinary cancellation service
    /// (so the platform's cancellation policy applies - free well ahead, a fee very close to the visit - and a paid
    /// visit is refunded). One failing does not stop the rest.
    /// </summary>
    private async Task<VisitTally> CancelBookedVisitsBeforeAsync(Guid customerId, RecurringBookingPlan plan, DateOnly from, DateOnly before)
    {
        int count = 0;
        decimal fees = 0m, refunded = 0m;
        var bookings = await _bookingRepository.ListByRecurringPlanAsync(plan.Id);
        foreach (var booking in bookings.Where(b => b.SlotDate >= from && b.SlotDate < before
            && BookingLifecycle.IsValidTransition(b.Status, BookingStatus.CancelledByCustomer)))
        {
            var outcome = await _cancellationService.CancelAsync(
                customerId, booking.Id, new CancelBookingRequest("Skipped while the customer is away."));

            if (outcome.IsFailure)
            {
                _logger.LogWarning(
                    "Skipping visits on plan {PlanId}: visit {BookingId} could not be cancelled ({ErrorCode}): {ErrorMessage}",
                    plan.Id, booking.Id, outcome.Error.Code, outcome.Error.Message);
                continue;
            }

            count++;
            fees += outcome.Value.CancellationFeeAmount;
            refunded += outcome.Value.RefundAmount;
        }

        return new VisitTally(count, fees, refunded);
    }

    public async Task<Result<RecurringBookingPlanResponse>> CancelAsync(Guid customerId, Guid planId)
    {
        var result = await TransitionAsync(customerId, planId, plan => plan.Cancel(), "RecurringBookingPlan.InvalidCancel");
        if (result.IsFailure)
        {
            return result;
        }

        var plan = await _planRepository.GetByIdAsync(planId);
        VisitTally? tally = null;
        if (plan is { PrepaidUpfront: true })
        {
            tally = await CancelUnservedPrepaidVisitsAsync(customerId, plan);
        }

        await ConfirmChangeAsync(planId, RecurringPlanChangeKind.Cancelled, tally);
        return result;
    }

    /// <summary>
    /// A prepaid plan's visits are real, already-paid bookings, so cancelling the plan
    /// must cancel them - through the ordinary cancellation service, which applies the
    /// platform's cancellation policy (free window, late fee) and raises the refund to
    /// the original payment method. Visits that can no longer be cancelled (already
    /// under way or done) are left alone. One visit failing does not stop the rest;
    /// it is logged and stays for support to resolve.
    /// </summary>
    private async Task<VisitTally> CancelUnservedPrepaidVisitsAsync(Guid customerId, RecurringBookingPlan plan)
    {
        int count = 0;
        decimal fees = 0m, refunded = 0m;
        var bookings = await _bookingRepository.ListByRecurringPlanAsync(plan.Id);
        foreach (var booking in bookings.Where(b => BookingLifecycle.IsValidTransition(b.Status, BookingStatus.CancelledByCustomer)))
        {
            var outcome = await _cancellationService.CancelAsync(
                customerId, booking.Id, new CancelBookingRequest("Recurring plan cancelled by the customer."));

            if (outcome.IsFailure)
            {
                _logger.LogWarning(
                    "Cancelling prepaid plan {PlanId}: visit {BookingId} could not be cancelled ({ErrorCode}): {ErrorMessage}",
                    plan.Id, booking.Id, outcome.Error.Code, outcome.Error.Message);
                continue;
            }

            count++;
            fees += outcome.Value.CancellationFeeAmount;
            refunded += outcome.Value.RefundAmount;
        }

        return new VisitTally(count, fees, refunded);
    }

    /// <summary>Dates of a prepaid purchase that were not booked, read back from the occurrence log.</summary>
    private async Task<IReadOnlyList<DateOnly>> SkippedDatesAsync(RecurringBookingPlan plan, DateOnly fromDate) =>
        (await _occurrenceRepository.ListByPlanAsync(plan.Id))
            .Where(o => !o.Outcome.CreatedBooking() && o.ScheduledDate >= fromDate)
            .Select(o => o.ScheduledDate)
            .OrderBy(d => d)
            .ToList();

    public async Task<Result<RecurringBookingPlanResponse>> SetAutoChargeAsync(Guid customerId, Guid planId, bool enabled) =>
        await TransitionAsync(customerId, planId, plan => plan.SetAutoCharge(enabled), "RecurringBookingPlan.InvalidAutoChargeChange");

    public async Task<Result<RecurringBookingPlanResponse>> SetOccurrenceBoundsAsync(Guid customerId, Guid planId, DateOnly? endDate, int? occurrenceCount)
    {
        var planResult = await ResolveOwnedPlanAsync(customerId, planId);
        if (planResult.IsFailure)
        {
            return planResult.Error;
        }

        var plan = planResult.Value;
        try
        {
            plan.SetOccurrenceBounds(endDate, occurrenceCount);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Error.Validation("RecurringBookingPlan.InvalidOccurrenceBounds", ex.Message);
        }

        await _planRepository.UpdateAsync(plan);

        return ToResponse(plan, await ResolveServiceNameAsync(plan.ServiceId));
    }

    public async Task<Result<IReadOnlyList<UpcomingOccurrenceResponse>>> ListUpcomingOccurrencesAsync(Guid customerId, Guid planId, int count = 5)
    {
        var planResult = await ResolveOwnedPlanAsync(customerId, planId);
        if (planResult.IsFailure)
        {
            return planResult.Error;
        }

        IReadOnlyList<UpcomingOccurrenceResponse> response = planResult.Value
            .PreviewUpcomingOccurrenceDates(count)
            .Select(date => new UpcomingOccurrenceResponse(date, IsProjected: true))
            .ToList();

        return Result.Success(response);
    }

    public async Task<Result<IReadOnlyList<OccurrenceHistoryResponse>>> ListOccurrenceHistoryAsync(Guid customerId, Guid planId)
    {
        var planResult = await ResolveOwnedPlanAsync(customerId, planId);
        if (planResult.IsFailure)
        {
            return planResult.Error;
        }

        var occurrences = await _occurrenceRepository.ListByPlanAsync(planId);
        IReadOnlyList<OccurrenceHistoryResponse> response = occurrences
            .OrderByDescending(o => o.ScheduledDate)
            .Select(o => new OccurrenceHistoryResponse(o.ScheduledDate, o.Outcome, o.BookingId, o.SkipReason, o.ProcessedAtUtc))
            .ToList();

        return Result.Success(response);
    }

    private async Task<Result<RecurringBookingPlanResponse>> TransitionAsync(
        Guid customerId, Guid planId, Action<RecurringBookingPlan> transition, string errorCode)
    {
        var planResult = await ResolveOwnedPlanAsync(customerId, planId);
        if (planResult.IsFailure)
        {
            return planResult.Error;
        }

        var plan = planResult.Value;
        try
        {
            transition(plan);
        }
        catch (InvalidOperationException ex)
        {
            return Error.Business(errorCode, ex.Message);
        }

        await _planRepository.UpdateAsync(plan);

        return ToResponse(plan, await ResolveServiceNameAsync(plan.ServiceId));
    }

    private async Task<Result<RecurringBookingPlan>> ResolveOwnedPlanAsync(Guid customerId, Guid planId)
    {
        var plan = await _planRepository.GetByIdAsync(planId);
        if (plan is null || plan.CustomerId != customerId)
        {
            return Error.NotFound("RecurringBookingPlan.NotFound", "The specified recurring booking plan does not exist.");
        }

        return plan;
    }

    /// <summary>
    /// Tells the customer what they just did to their plan (see <see cref="IRecurringPlanNotifier"/>). Runs only after
    /// the change succeeded and can never fail it: the facts it gathers (visits still ahead) and the sending are both
    /// best effort, logged if they go wrong.
    /// </summary>
    private async Task ConfirmChangeAsync(
        Guid planId, RecurringPlanChangeKind kind, VisitTally? tally = null, DateOnly? resumeOn = null)
    {
        try
        {
            var plan = await _planRepository.GetByIdAsync(planId);
            if (plan is null)
            {
                return;
            }

            // Visits already booked that this change leaves going ahead. For a skip, only those before the resume
            // date matter (later ones were never in question).
            var today = _clock.Today;
            var ahead = (await _bookingRepository.ListByRecurringPlanAsync(plan.Id))
                .Where(b => b.SlotDate >= today
                    && (resumeOn is null || b.SlotDate < resumeOn)
                    && BookingLifecycle.IsValidTransition(b.Status, BookingStatus.CancelledByCustomer))
                .OrderBy(b => b.SlotDate)
                .ToList();

            await _notifier.NotifyChangedAsync(plan, new RecurringPlanChange(
                kind,
                BookedVisitsStillAhead: ahead.Count,
                NextBookedVisitDate: ahead.Count > 0 ? ahead[0].SlotDate : null,
                ResumeOn: resumeOn,
                VisitsCancelled: tally?.Count ?? 0,
                CancellationFees: tally?.Fees ?? 0m,
                Refunded: tally?.Refunded ?? 0m));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not confirm the {Kind} change to recurring plan {PlanId} to the customer.", kind, planId);
        }
    }

    /// <summary>What the plan card needs beyond the plan itself, read for all of a customer's plans at once.</summary>
    private sealed record CardData(
        ILookup<Guid, PlanVisitSummary> VisitsByPlan, IReadOnlyDictionary<Guid, SlotWindow> Windows, CancellationSettings Cancellation);

    private async Task<CardData> LoadCardDataAsync(IReadOnlyList<RecurringBookingPlan> plans)
    {
        var visits = await _bookingRepository.ListVisitSummariesByPlansAsync(
            plans.Select(p => p.Id).ToList(), _clock.Today.AddDays(-PriceLookbackDays));

        var windows = new Dictionary<Guid, SlotWindow>();
        foreach (var windowId in plans.Select(p => p.SlotWindowId).Distinct())
        {
            var window = await _slotWindowRepository.GetByIdAsync(windowId);
            if (window is not null)
            {
                windows[windowId] = window;
            }
        }

        return new CardData(visits.ToLookup(v => v.PlanId), windows, await _policies.GetCancellationAsync());
    }

    private RecurringBookingPlanResponse Enrich(RecurringBookingPlanResponse response, RecurringBookingPlan plan, CardData card)
    {
        var today = _clock.Today;
        var visits = card.VisitsByPlan[plan.Id].ToList();

        var awaitingPayment = visits
            .Where(v => v.Status == BookingStatus.PaymentPending && v.SlotDate >= today)
            .OrderBy(v => v.SlotDate)
            .FirstOrDefault();

        var upcoming = visits
            .Where(v => v.SlotDate >= today && BookedStatuses.Contains(v.Status))
            .Select(v => v.SlotDate)
            .Order()
            .ToList();

        // The per-visit price: what the plan's newest real visit (not a cancelled or expired one) cost in total.
        var newest = visits
            .Where(v => !RecurringPlanProviderContinuityService.NonPrecedentStatuses.Contains(v.Status))
            .OrderByDescending(v => v.SlotDate)
            .FirstOrDefault();

        card.Windows.TryGetValue(plan.SlotWindowId, out var window);

        return response with
        {
            SlotWindowName = window?.Name,
            SlotStartTime = window?.StartTime,
            SlotEndTime = window?.EndTime,
            VisitAmount = newest is null ? null : newest.TotalPayable + newest.WalletCreditApplied,
            UpcomingBookedVisitDates = upcoming,
            VisitAwaitingPayment = awaitingPayment is null
                ? null
                : new PlanVisitAwaitingPaymentResponse(awaitingPayment.BookingId, awaitingPayment.SlotDate, awaitingPayment.TotalPayable),
            CancellationFreeWindowHours = card.Cancellation.FreeCancellationWindowHours,
            LateCancellationFeePercentage = card.Cancellation.LateCancellationFeePercentage
        };
    }

    private async Task<string> ResolveServiceNameAsync(Guid serviceId) =>
        (await _serviceRepository.GetByIdAsync(serviceId))?.Name ?? string.Empty;

    private static RecurringBookingPlanResponse ToResponse(
        RecurringBookingPlan plan, string serviceName, IReadOnlyList<DateOnly>? skippedDates = null) => new(
        plan.Id, plan.ServiceId, serviceName, plan.AddressId, plan.SlotWindowId, plan.Quantity, plan.ApplyWalletCredit,
        plan.AutoChargeEnabled, plan.Frequency, plan.RecurrenceDayOfWeek, plan.RecurrenceDayOfMonth, plan.StartDate, plan.EndDate,
        plan.OccurrenceCount, plan.CompletedOccurrenceCount, plan.NextOccurrenceDate, plan.Status, plan.CreatedAtUtc,
        plan.PrepaidUpfront, plan.PendingPrepaymentLeadBookingId, plan.PrepaidThroughDate, skippedDates,
        plan.PauseReason, plan.SkipUntilDate, plan.SkipRangesUsed, plan.CityId, plan.LocalityId);
}
