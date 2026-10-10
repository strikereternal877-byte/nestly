using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>
/// A customer's standing instruction to repeat a booking on a schedule
/// (PRODUCT-ENHANCEMENTS.md section 2, tasks 184-188) - e.g. "clean my flat
/// every other Tuesday morning". This entity only ever describes the
/// schedule; it never creates a booking itself. The Hangfire scheduler
/// (task 185, <c>IRecurringBookingSchedulerService</c>) reads plans that are
/// due and calls the exact same booking-creation orchestration a customer's
/// own "Book now" tap uses (<c>IBookingService.CreateAsync</c>, task 58) -
/// this aggregate is deliberately thin and holds no pricing, payment, or
/// serviceability logic of its own, so there is no second copy of those
/// rules to keep in sync with Booking's.
///
/// Unlike <see cref="Booking"/>, <see cref="AddressId"/> and
/// <see cref="SlotWindowId"/> here are live references, not snapshots - a
/// plan re-resolves the customer's current address and the current slot
/// window definition at every occurrence (the orchestration re-validates
/// both fresh each time), because a standing instruction should track "my
/// home" and "Tuesday mornings", not freeze whatever the address/window
/// looked like on the day the plan was created. If the address is later
/// deleted or the window deactivated, the next occurrence simply fails
/// orchestration validation and is skipped-and-notified like any other
/// unavailable-slot case.
///
/// OPEN DECISION (closed, see PRODUCT-ENHANCEMENTS.md OPEN DECISIONS):
/// a skipped occurrence (slot unavailable) does not count against
/// <see cref="OccurrenceCount"/> - only successfully booked occurrences
/// increment <see cref="CompletedOccurrenceCount"/>. See
/// <see cref="RecordOccurrenceSkipped"/>.
/// </summary>
public class RecurringBookingPlan : AggregateRoot<Guid>
{
    private readonly List<RecurringBookingPlanAddOn> _addOns = [];

    public Guid CustomerId { get; private set; }

    public Guid ServiceId { get; private set; }

    public Guid CityId { get; private set; }

    public Guid LocalityId { get; private set; }

    /// <summary>Live reference - see the class doc comment for why this differs from Booking's snapshot fields.</summary>
    public Guid AddressId { get; private set; }

    /// <summary>Live reference - the recurring time-of-day window (e.g. "9am-11am"). Re-validated by the orchestration at every occurrence.</summary>
    public Guid SlotWindowId { get; private set; }

    public int Quantity { get; private set; }

    /// <summary>
    /// Whether every occurrence this plan generates should apply the
    /// customer's wallet balance (task 370). Chosen once, at plan creation,
    /// and reused for every future occurrence - there is no per-occurrence
    /// UI moment to ask again, unlike an ad-hoc booking's own wallet
    /// checkbox (<c>BookingSummaryRequest.ApplyWalletCredit</c>). Defaults
    /// to false, matching that checkbox's own off-by-default precedent
    /// (booking/summary's UI deliberately avoids a silent auto-apply).
    /// </summary>
    public bool ApplyWalletCredit { get; private set; }

    /// <summary>
    /// Recurring-booking payment-timing fix: whether the customer has opted
    /// in to letting <c>RecurringOccurrenceAutoChargeJob</c> attempt payment
    /// on their behalf, off-session, through the same sandbox gateway seam
    /// <c>SubscriptionBillingJob</c> already uses (<c>IPaymentGateway</c>/
    /// <c>ISandboxPaymentSimulator</c>) - not a second, invented payment
    /// integration.
    ///
    /// <para>
    /// Defaults to false and is never inferred: consent to auto-deduction is
    /// an explicit customer choice, set at creation or toggled later via
    /// <see cref="SetAutoCharge"/>, never turned on implicitly by this
    /// aggregate itself. A plan with this off simply keeps getting the
    /// existing "payment due, please pay manually" notification for every
    /// occurrence.
    /// </para>
    /// </summary>
    public bool AutoChargeEnabled { get; private set; }

    public RecurringBookingRecurrenceFrequency Frequency { get; private set; }

    /// <summary>Required for <see cref="RecurringBookingRecurrenceFrequency.Weekly"/>/<see cref="RecurringBookingRecurrenceFrequency.Biweekly"/>; null for <see cref="RecurringBookingRecurrenceFrequency.Monthly"/>.</summary>
    public DayOfWeek? RecurrenceDayOfWeek { get; private set; }

    /// <summary>Required for <see cref="RecurringBookingRecurrenceFrequency.Monthly"/> (1-31, clamped to the actual month length); null otherwise.</summary>
    public int? RecurrenceDayOfMonth { get; private set; }

    public DateOnly StartDate { get; private set; }

    /// <summary>Optional. With <see cref="OccurrenceCount"/> also null the plan is open-ended ("until I cancel") - see <see cref="IsOpenEnded"/>.</summary>
    public DateOnly? EndDate { get; private set; }

    public int? OccurrenceCount { get; private set; }

    /// <summary>
    /// True when neither <see cref="EndDate"/> nor <see cref="OccurrenceCount"/> is set: the plan never
    /// completes on its own and runs until the customer cancels it (or it is auto-paused, see
    /// <see cref="PauseForPaymentFailure"/>). Safe for the scheduler because it only ever books
    /// <c>RecurringBookingOptions.LeadTimeDays</c> ahead, never the whole future at once.
    /// </summary>
    public bool IsOpenEnded => EndDate is null && OccurrenceCount is null;

    /// <summary>Successfully booked occurrences only - a skipped occurrence never increments this. See the class doc comment's OPEN DECISION note.</summary>
    public int CompletedOccurrenceCount { get; private set; }

    /// <summary>The next date the scheduler should attempt (or skip-and-notify), advanced by <see cref="RecordOccurrenceBooked"/>/<see cref="RecordOccurrenceSkipped"/> regardless of outcome.</summary>
    public DateOnly NextOccurrenceDate { get; private set; }

    public RecurringBookingPlanStatus Status { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>
    /// True when the customer pays for a whole cycle of visits in one checkout
    /// instead of each visit as it is generated. The plan's bookings are then
    /// created together up front (see <c>IRecurringBookingSchedulerService.MaterializePrepaidCycleAsync</c>)
    /// and settled by one <see cref="PaymentGroup"/>; the daily scheduler never
    /// generates occurrences for such a plan itself. Fixed at creation.
    /// </summary>
    public bool PrepaidUpfront { get; private set; }

    /// <summary>
    /// For a prepaid plan: the booking whose payment page settles the cycle
    /// that is still unpaid - the booking placed together with the plan for
    /// the first cycle, the first visit of the cycle for a renewal. Null once
    /// that cycle is paid (and always null for a pay-per-visit plan).
    /// </summary>
    public Guid? PendingPrepaymentLeadBookingId { get; private set; }

    /// <summary>True while a prepaid cycle exists that the customer has not paid yet.</summary>
    public bool IsAwaitingPrepayment => PendingPrepaymentLeadBookingId is not null;

    /// <summary>How many prepaid cycles have been paid; 0 means the plan has never been started (its first cycle is still unpaid or was abandoned).</summary>
    public int PrepaidCyclesPaid { get; private set; }

    /// <summary>The last date covered by a <i>paid</i> prepaid cycle; drives renewal of an open-ended prepaid plan. Never advanced by a cycle that is still unpaid.</summary>
    public DateOnly? PrepaidThroughDate { get; private set; }

    /// <summary>The last date of the cycle that is awaiting payment; becomes <see cref="PrepaidThroughDate"/> when it is paid, and is discarded if it never is.</summary>
    public DateOnly? PendingPrepaymentThroughDate { get; private set; }

    /// <summary>Why the plan is Paused; null whenever it is not. See <see cref="RecurringBookingPauseReason"/>.</summary>
    public RecurringBookingPauseReason? PauseReason { get; private set; }

    /// <summary>
    /// When the customer last asked to skip visits until a date (<see cref="SkipVisitsUntil"/>): no visit
    /// is generated before it. Informational once it has passed - the plan is Active throughout and the
    /// scheduler simply finds nothing due until <see cref="NextOccurrenceDate"/> comes within its lead time.
    /// </summary>
    public DateOnly? SkipUntilDate { get; private set; }

    /// <summary>How many "skip visits until" requests this plan has used - capped by policy so it cannot be chained into a permanent gap.</summary>
    public int SkipRangesUsed { get; private set; }

    public IReadOnlyList<RecurringBookingPlanAddOn> AddOns => _addOns;

    protected RecurringBookingPlan() { }

    public RecurringBookingPlan(
        Guid id,
        Guid customerId,
        Guid serviceId,
        Guid cityId,
        Guid localityId,
        Guid addressId,
        Guid slotWindowId,
        int quantity,
        RecurringBookingRecurrenceFrequency frequency,
        DayOfWeek? recurrenceDayOfWeek,
        int? recurrenceDayOfMonth,
        DateOnly startDate,
        DateOnly? endDate,
        int? occurrenceCount,
        IReadOnlyList<(Guid AddOnId, int Quantity)>? addOns = null,
        bool applyWalletCredit = false,
        bool autoChargeEnabled = false,
        bool prepaidUpfront = false,
        Guid? prepaidLeadBookingId = null)
        : base(id)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive.");
        }

        if (prepaidUpfront && prepaidLeadBookingId is null)
        {
            throw new ArgumentException("A prepaid plan needs the booking that its first payment page is keyed by.", nameof(prepaidLeadBookingId));
        }

        if (prepaidUpfront && (autoChargeEnabled || applyWalletCredit))
        {
            throw new ArgumentException("A prepaid plan is paid for at checkout; auto-charge and per-visit wallet credit do not apply to it.");
        }

        ValidateRecurrenceFields(frequency, recurrenceDayOfWeek, recurrenceDayOfMonth);

        if (endDate is { } end && end < startDate)
        {
            throw new ArgumentOutOfRangeException(nameof(endDate), "End date cannot be before the start date.");
        }

        if (occurrenceCount is { } count && count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(occurrenceCount), "Occurrence count must be positive.");
        }

        CustomerId = customerId;
        ServiceId = serviceId;
        CityId = cityId;
        LocalityId = localityId;
        AddressId = addressId;
        SlotWindowId = slotWindowId;
        Quantity = quantity;
        ApplyWalletCredit = applyWalletCredit;
        AutoChargeEnabled = autoChargeEnabled;
        PrepaidUpfront = prepaidUpfront;
        PendingPrepaymentLeadBookingId = prepaidUpfront ? prepaidLeadBookingId : null;
        Frequency = frequency;
        RecurrenceDayOfWeek = recurrenceDayOfWeek;
        RecurrenceDayOfMonth = recurrenceDayOfMonth;
        StartDate = startDate;
        EndDate = endDate;
        OccurrenceCount = occurrenceCount;
        CompletedOccurrenceCount = 0;
        Status = RecurringBookingPlanStatus.Active;
        CreatedAtUtc = DateTime.UtcNow;

        NextOccurrenceDate = NextOccurrenceOnOrAfter(startDate, frequency, recurrenceDayOfWeek, recurrenceDayOfMonth);

        foreach (var (addOnId, addOnQuantity) in addOns ?? [])
        {
            _addOns.Add(new RecurringBookingPlanAddOn(Guid.NewGuid(), Id, addOnId, addOnQuantity));
        }
    }

    /// <summary>
    /// Projects the next <paramref name="count"/> scheduled dates from
    /// <see cref="NextOccurrenceDate"/> forward, without persisting anything -
    /// pure calculation for the "list upcoming occurrences" API (task 186).
    /// Stops early at <see cref="EndDate"/> or once <see cref="OccurrenceCount"/>
    /// worth of bookings would be reached (assuming, optimistically, every
    /// remaining projected date is eventually booked rather than skipped -
    /// this is a preview, not a guarantee, exactly like <c>RevalidateSlotAsync</c>
    /// is a preview rather than a hold).
    /// </summary>
    public IReadOnlyList<DateOnly> PreviewUpcomingOccurrenceDates(int count)
    {
        if (count <= 0)
        {
            return [];
        }

        var dates = new List<DateOnly>(count);
        var candidate = NextOccurrenceDate;
        int remainingBudget = OccurrenceCount is { } target ? Math.Max(0, target - CompletedOccurrenceCount) : int.MaxValue;

        while (dates.Count < count && dates.Count < remainingBudget)
        {
            if (EndDate is { } end && candidate > end)
            {
                break;
            }

            dates.Add(candidate);
            candidate = NextOccurrenceStrictlyAfter(candidate, Frequency, RecurrenceDayOfWeek, RecurrenceDayOfMonth);
        }

        return dates;
    }

    /// <summary>Active -> Paused. The scheduler skips a paused plan entirely (never even attempts, never skip-and-notifies) - pausing is the customer saying "not right now", not "keep trying and tell me".</summary>
    public void Pause()
    {
        if (Status != RecurringBookingPlanStatus.Active)
        {
            throw new InvalidOperationException($"Only an active plan can be paused (current status: {Status}).");
        }

        Status = RecurringBookingPlanStatus.Paused;
        PauseReason = RecurringBookingPauseReason.Customer;
    }

    /// <summary>
    /// Active -> Paused by an admin. Same effect on the scheduler as a customer pause, but recorded as
    /// <see cref="RecurringBookingPauseReason.Admin"/> so the customer is told the truth about who paused it and
    /// cannot undo what support did by tapping Resume.
    /// </summary>
    public void PauseByAdmin()
    {
        if (Status != RecurringBookingPlanStatus.Active)
        {
            throw new InvalidOperationException($"Only an active plan can be paused (current status: {Status}).");
        }

        Status = RecurringBookingPlanStatus.Paused;
        PauseReason = RecurringBookingPauseReason.Admin;
    }

    /// <summary>
    /// The prepaid cycle's payment landed: the plan no longer waits on
    /// <see cref="PendingPrepaymentLeadBookingId"/>, and the paid coverage now
    /// reaches the cycle's last date.
    /// </summary>
    public void ConfirmPrepayment()
    {
        PendingPrepaymentLeadBookingId = null;
        PrepaidCyclesPaid++;
        if (PendingPrepaymentThroughDate is { } through)
        {
            PrepaidThroughDate = through;
        }

        PendingPrepaymentThroughDate = null;
    }

    /// <summary>
    /// A renewal window produced nothing to pay for (no date in it could be
    /// booked): move the paid coverage forward anyway, so the renewal job does
    /// not pick this plan up again on every run.
    /// </summary>
    public void AdvancePrepaidCoverage(DateOnly coveredThroughDate)
    {
        PrepaidThroughDate = coveredThroughDate;
    }

    /// <summary>
    /// Records that the prepaid cycle has been created and now waits for payment
    /// - the first cycle at plan creation, a renewal cycle later.
    /// </summary>
    public void BeginPrepaymentCycle(Guid leadBookingId, DateOnly coveredThroughDate)
    {
        if (!PrepaidUpfront)
        {
            throw new InvalidOperationException("Only a prepaid plan has prepayment cycles.");
        }

        PendingPrepaymentLeadBookingId = leadBookingId;
        PendingPrepaymentThroughDate = coveredThroughDate;
    }

    /// <summary>
    /// The customer never paid the first cycle (its bookings expired or were
    /// cancelled): the plan was never really started, so it ends here rather
    /// than sitting Active with nothing behind it. A no-op unless a cycle is
    /// still awaiting payment. For an unpaid <i>renewal</i> of an open-ended
    /// plan use <see cref="PauseForPaymentFailure"/> instead - that plan has
    /// already delivered visits and can be resumed.
    /// </summary>
    public bool AbandonUnpaidFirstCycle()
    {
        if (!PrepaidUpfront || PendingPrepaymentLeadBookingId is null || PrepaidCyclesPaid > 0)
        {
            return false;
        }

        PendingPrepaymentLeadBookingId = null;
        PendingPrepaymentThroughDate = null;
        if (Status is RecurringBookingPlanStatus.Active or RecurringBookingPlanStatus.Paused or RecurringBookingPlanStatus.Completed)
        {
            Status = RecurringBookingPlanStatus.Cancelled;
        }

        return true;
    }

    /// <summary>
    /// A renewal cycle of an open-ended prepaid plan went unpaid (its bookings
    /// expired or were cancelled). The plan has already delivered paid visits,
    /// so it is paused rather than ended - the customer can resume it, which
    /// starts a fresh renewal. A no-op unless a renewal is actually pending.
    /// </summary>
    public bool AbandonUnpaidRenewal()
    {
        if (!PrepaidUpfront || PendingPrepaymentLeadBookingId is null || PrepaidCyclesPaid == 0)
        {
            return false;
        }

        PendingPrepaymentLeadBookingId = null;
        PendingPrepaymentThroughDate = null;
        if (Status == RecurringBookingPlanStatus.Active)
        {
            Status = RecurringBookingPlanStatus.Paused;
            PauseReason = RecurringBookingPauseReason.UnpaidVisits;
        }

        return true;
    }

    /// <summary>
    /// Safety net for open-ended plans: pauses the plan after its auto-charge ran out of retries, so an
    /// "until I cancel" plan cannot keep generating unpaid occurrences indefinitely. Returns whether the plan
    /// was paused - false (a no-op) for a bounded plan, which ends on its own, or one that is not Active.
    /// Uses the same Paused state as <see cref="Pause"/>, so the customer resumes it the normal way.
    /// </summary>
    public bool PauseForPaymentFailure()
    {
        if (!IsOpenEnded || Status != RecurringBookingPlanStatus.Active)
        {
            return false;
        }

        Status = RecurringBookingPlanStatus.Paused;
        PauseReason = RecurringBookingPauseReason.PaymentFailure;
        return true;
    }

    /// <summary>
    /// Pauses a pay-as-you-go plan whose recent visits keep expiring unpaid. Nothing here changes whether
    /// an unpaid visit happens - it never does: a booking is only assigned a professional once it is paid,
    /// and an unpaid one expires. What this stops is the plan carrying on creating a new booking a day,
    /// holding a slot for the payment window and sending a reminder each time, for a customer who is not
    /// paying. Returns whether the plan was paused; false (a no-op) for a prepaid plan - its visits are
    /// paid for up front, so there is nothing to pay per visit - or one that is not Active.
    /// </summary>
    public bool PauseForUnpaidVisits()
    {
        if (PrepaidUpfront || Status != RecurringBookingPlanStatus.Active)
        {
            return false;
        }

        Status = RecurringBookingPlanStatus.Paused;
        PauseReason = RecurringBookingPauseReason.UnpaidVisits;
        return true;
    }

    /// <summary>
    /// Paused -> Active. Not in PRODUCT-ENHANCEMENTS.md's literal API list
    /// ("create/pause/cancel"), added because pause is meaningless as a
    /// customer-facing action without a way back - without resume, "pause"
    /// would just be a slower, more confusing "cancel". <see cref="NextOccurrenceDate"/>
    /// is left exactly where it was; a plan paused mid-cycle resumes from
    /// the same next date rather than fast-forwarding, so the customer never
    /// loses a date they were still owed.
    /// </summary>
    /// <param name="today">
    /// When given (the service always gives it), a cursor that has fallen into the past while the plan was
    /// paused is moved forward to the first occurrence on or after today. Dates that have already gone by
    /// cannot be booked, so leaving the cursor there would make the scheduler walk through them one by one,
    /// recording a skipped visit and notifying the customer for each, before it ever reached a real date.
    /// A date that is still ahead is never touched, so the customer keeps every date they were still owed.
    /// </param>
    public void Resume(DateOnly? today = null)
    {
        if (Status != RecurringBookingPlanStatus.Paused)
        {
            throw new InvalidOperationException($"Only a paused plan can be resumed (current status: {Status}).");
        }

        Status = RecurringBookingPlanStatus.Active;
        PauseReason = null;

        if (today is { } now && NextOccurrenceDate < now)
        {
            NextOccurrenceDate = NextOccurrenceOnOrAfter(now, Frequency, RecurrenceDayOfWeek, RecurrenceDayOfMonth);

            if (EndDate is { } end && NextOccurrenceDate > end)
            {
                Status = RecurringBookingPlanStatus.Completed;
            }
        }
    }

    /// <summary>
    /// "I'm away until <paramref name="resumeOn"/>": no visit is generated before that date. The plan stays
    /// Active - the cursor simply moves to the first occurrence on or after <paramref name="resumeOn"/>, so
    /// nothing has to run to bring the plan back; the scheduler finds nothing due until that date comes
    /// within its lead time.
    ///
    /// <para>
    /// Visits already generated before the cursor are untouched (the caller may cancel them separately).
    /// Skipped dates are not consumed from <see cref="OccurrenceCount"/>, and a plan bounded by
    /// <see cref="EndDate"/> has that date pushed out by the days skipped, so the customer still gets
    /// everything they were promised.
    /// </para>
    /// </summary>
    /// <param name="today">The business-local date today; <paramref name="resumeOn"/> must be after it.</param>
    /// <returns>The number of days the cursor moved.</returns>
    public int SkipVisitsUntil(DateOnly resumeOn, DateOnly today)
    {
        if (Status != RecurringBookingPlanStatus.Active)
        {
            throw new InvalidOperationException($"Visits can only be skipped on an active plan (current status: {Status}).");
        }

        if (PrepaidUpfront)
        {
            throw new InvalidOperationException(
                "A prepaid plan's visits are already booked and paid for - cancel or reschedule those visits instead.");
        }

        if (resumeOn <= today)
        {
            throw new ArgumentOutOfRangeException(nameof(resumeOn), "Choose a date after today.");
        }

        var newCursor = NextOccurrenceOnOrAfter(resumeOn, Frequency, RecurrenceDayOfWeek, RecurrenceDayOfMonth);
        if (newCursor <= NextOccurrenceDate)
        {
            throw new InvalidOperationException("No visit of this plan falls before that date, so there is nothing to skip.");
        }

        int movedDays = newCursor.DayNumber - NextOccurrenceDate.DayNumber;
        if (EndDate is { } end)
        {
            EndDate = end.AddDays(movedDays);
        }

        NextOccurrenceDate = newCursor;
        SkipUntilDate = resumeOn;
        SkipRangesUsed++;
        return movedDays;
    }

    /// <summary>
    /// Moves every visit generated from now on to a different time-of-day window. It deliberately touches
    /// nothing already booked: those few visits (at most the scheduler's lead time ahead) keep their slot
    /// unless the customer reschedules them one by one, so a change here can never fail half-way through a
    /// batch of bookings. The caller is responsible for having checked that the new window can serve
    /// <see cref="NextOccurrenceDate"/>.
    /// </summary>
    public void ChangeSlotWindow(Guid newSlotWindowId)
    {
        if (Status is RecurringBookingPlanStatus.Cancelled or RecurringBookingPlanStatus.Completed)
        {
            throw new InvalidOperationException($"Cannot change the time of a {Status} plan.");
        }

        if (PrepaidUpfront)
        {
            throw new InvalidOperationException(
                "A prepaid plan's visits are already booked; reschedule individual visits instead.");
        }

        if (newSlotWindowId == Guid.Empty)
        {
            throw new ArgumentException("A time window is required.", nameof(newSlotWindowId));
        }

        if (newSlotWindowId == SlotWindowId)
        {
            throw new InvalidOperationException("The plan already uses that time window.");
        }

        SlotWindowId = newSlotWindowId;
    }

    /// <summary>
    /// Toggles the customer's consent to off-session auto-charge. Callable in
    /// any non-terminal status (unlike <see cref="Pause"/>/<see cref="Resume"/>,
    /// this is a standing preference, not a scheduling state) - a customer can
    /// turn it off the moment they change their mind, including while paused,
    /// and a currently-in-flight auto-charge attempt for an occurrence already
    /// created is unaffected (see <c>RecurringOccurrenceAutoChargeJob</c>,
    /// which reads the plan fresh on each attempt rather than caching this
    /// flag).
    /// </summary>
    public void SetAutoCharge(bool enabled)
    {
        if (Status is RecurringBookingPlanStatus.Cancelled or RecurringBookingPlanStatus.Completed)
        {
            throw new InvalidOperationException($"Cannot change auto-charge on a {Status} plan.");
        }

        if (PrepaidUpfront && enabled)
        {
            throw new InvalidOperationException("A prepaid plan is paid for at checkout; auto-charge does not apply to it.");
        }

        AutoChargeEnabled = enabled;
    }

    /// <summary>
    /// Occurrence-count integrity fix (the "no way to edit a plan" half):
    /// lets the customer tighten or loosen how many more occurrences this
    /// plan will generate - <see cref="EndDate"/> and/or
    /// <see cref="OccurrenceCount"/> only. Everything else about the plan
    /// (service, address, slot window, frequency, add-ons) is deliberately
    /// out of scope here: changing what gets booked each time is a
    /// materially different, re-validation-requiring operation (mirroring
    /// why plan creation dry-runs pricing through the booking-summary
    /// orchestration before persisting anything), while the occurrence
    /// budget is pure bookkeeping this aggregate can safely own on its own.
    ///
    /// <para>
    /// Same rules the constructor enforces (<see cref="EndDate"/> not before
    /// <see cref="StartDate"/>, <see cref="OccurrenceCount"/> positive; both
    /// null is allowed and makes the plan open-ended), plus one more specific to
    /// editing: <see cref="OccurrenceCount"/> can never drop below
    /// <see cref="CompletedOccurrenceCount"/> - a customer who already
    /// received 5 visits cannot have their plan's promise cut to 3. Applies
    /// the new bounds and immediately re-evaluates completion against the
    /// unchanged <see cref="NextOccurrenceDate"/>, exactly like
    /// <see cref="AdvanceOrComplete"/> does after every occurrence - without
    /// this, a plan edited to a bound it already exceeds would sit Active
    /// until the scheduler's next tick tried to book past it.
    /// </para>
    /// </summary>
    public void SetOccurrenceBounds(DateOnly? endDate, int? occurrenceCount)
    {
        if (Status is RecurringBookingPlanStatus.Cancelled or RecurringBookingPlanStatus.Completed)
        {
            throw new InvalidOperationException($"Cannot edit a {Status} plan's occurrence bounds.");
        }

        if (PrepaidUpfront && PrepaidCyclesPaid > 0)
        {
            throw new InvalidOperationException("A paid prepaid plan's visits are fixed at purchase; cancel it and buy a new one to change them.");
        }

        if (endDate is { } end && end < StartDate)
        {
            throw new ArgumentOutOfRangeException(nameof(endDate), "End date cannot be before the start date.");
        }

        if (occurrenceCount is { } count)
        {
            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(occurrenceCount), "Occurrence count must be positive.");
            }

            if (count < CompletedOccurrenceCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(occurrenceCount),
                    $"Occurrence count cannot be reduced below the {CompletedOccurrenceCount} occurrence(s) already booked.");
            }
        }

        EndDate = endDate;
        OccurrenceCount = occurrenceCount;

        bool occurrenceBudgetExhausted = OccurrenceCount is { } target && CompletedOccurrenceCount >= target;
        bool pastEndDate = EndDate is { } newEnd && NextOccurrenceDate > newEnd;
        if (occurrenceBudgetExhausted || pastEndDate)
        {
            Status = RecurringBookingPlanStatus.Completed;
        }
    }

    /// <summary>Active or Paused -> Cancelled. Terminal - a cancelled plan can never be resumed (create a new one instead), same one-way-door convention <c>BookingLifecycle</c> uses for its own terminal states.</summary>
    public void Cancel()
    {
        // A prepaid plan reads Completed as soon as its last visit is *booked* (all of them are
        // created at purchase), while most of those visits are still ahead - the customer must
        // still be able to cancel what is left, so Completed is not final for it.
        if (Status == RecurringBookingPlanStatus.Cancelled || (Status == RecurringBookingPlanStatus.Completed && !PrepaidUpfront))
        {
            throw new InvalidOperationException($"A {Status} plan cannot be cancelled.");
        }

        Status = RecurringBookingPlanStatus.Cancelled;
    }

    /// <summary>
    /// Called by the scheduler after the orchestration successfully created a
    /// booking for <paramref name="occurrenceDate"/>. Advances the schedule
    /// and completes the plan once its occurrence budget is exhausted or its
    /// end date has passed.
    /// </summary>
    public void RecordOccurrenceBooked(DateOnly occurrenceDate)
    {
        EnsureIsDueDate(occurrenceDate);

        CompletedOccurrenceCount++;
        AdvanceOrComplete();
    }

    /// <summary>
    /// Called by the scheduler when the occurrence was skipped (slot no
    /// longer available, or the orchestration otherwise rejected the
    /// attempt). Deliberately does not touch <see cref="CompletedOccurrenceCount"/>
    /// - see the class doc comment's OPEN DECISION note: a supply-side miss
    /// is not charged against the customer's occurrence budget, so the plan
    /// effectively extends by one date rather than delivering one fewer
    /// visit than promised.
    /// </summary>
    public void RecordOccurrenceSkipped(DateOnly occurrenceDate)
    {
        EnsureIsDueDate(occurrenceDate);

        AdvanceOrComplete();
    }

    /// <summary>
    /// Occurrence-count integrity fix: reverses one occurrence's contribution
    /// to <see cref="CompletedOccurrenceCount"/> once its booking is
    /// confirmed to have never been delivered (cancelled before the visit,
    /// expired unpaid, or refunded from a pre-visit cancellation - see
    /// <c>RecurringPlanOccurrenceReleaseHandler</c>'s doc comment for the
    /// exact trigger set and why a post-visit refund must NOT reach this
    /// method). Without this, a customer bound by <see cref="OccurrenceCount"/>
    /// could receive fewer real visits than the plan promised: the counter
    /// was incremented at booking-<i>creation</i> time by
    /// <see cref="RecordOccurrenceBooked"/> and, before this method existed,
    /// nothing ever gave it back.
    ///
    /// <para>
    /// If reaching <see cref="OccurrenceCount"/> is what completed this plan,
    /// reopens it to <see cref="RecurringBookingPlanStatus.Active"/> so the
    /// scheduler picks up one more occurrence at the already-advanced
    /// <see cref="NextOccurrenceDate"/> - the plan simply runs one cycle
    /// longer than originally projected, exactly making up the one that
    /// never happened. Left untouched if <see cref="EndDate"/> is what
    /// completed it instead (a hard calendar boundary, not a budget) or if
    /// the plan is <see cref="RecurringBookingPlanStatus.Cancelled"/> (a
    /// deliberate one-way door - see <see cref="Cancel"/> - that a
    /// booking-level event must never reverse).
    /// </para>
    /// </summary>
    public void ReleaseOccurrence()
    {
        if (CompletedOccurrenceCount > 0)
        {
            CompletedOccurrenceCount--;
        }

        // A prepaid plan never makes up a missed visit: it sold exactly the visits it created, and
        // the daily job never generates for it, so "reopening" it would only leave it looking
        // Active with nothing left to do.
        if (Status != RecurringBookingPlanStatus.Completed || PrepaidUpfront)
        {
            return;
        }

        bool occurrenceBudgetExhausted = OccurrenceCount is { } target && CompletedOccurrenceCount >= target;
        bool pastEndDate = EndDate is { } end && NextOccurrenceDate > end;

        if (!occurrenceBudgetExhausted && !pastEndDate)
        {
            Status = RecurringBookingPlanStatus.Active;
        }
    }

    private void EnsureIsDueDate(DateOnly occurrenceDate)
    {
        if (Status != RecurringBookingPlanStatus.Active)
        {
            throw new InvalidOperationException($"Cannot record an occurrence for a plan that is not active (current status: {Status}).");
        }

        if (occurrenceDate != NextOccurrenceDate)
        {
            throw new InvalidOperationException(
                $"Occurrence date {occurrenceDate:yyyy-MM-dd} does not match this plan's next due date {NextOccurrenceDate:yyyy-MM-dd}.");
        }
    }

    private void AdvanceOrComplete()
    {
        var next = NextOccurrenceStrictlyAfter(NextOccurrenceDate, Frequency, RecurrenceDayOfWeek, RecurrenceDayOfMonth);

        bool occurrenceBudgetExhausted = OccurrenceCount is { } target && CompletedOccurrenceCount >= target;
        bool pastEndDate = EndDate is { } end && next > end;

        if (occurrenceBudgetExhausted || pastEndDate)
        {
            Status = RecurringBookingPlanStatus.Completed;
        }

        NextOccurrenceDate = next;
    }

    private static void ValidateRecurrenceFields(RecurringBookingRecurrenceFrequency frequency, DayOfWeek? dayOfWeek, int? dayOfMonth)
    {
        switch (frequency)
        {
            case RecurringBookingRecurrenceFrequency.Weekly:
            case RecurringBookingRecurrenceFrequency.Biweekly:
                if (dayOfWeek is null)
                {
                    throw new ArgumentException("A day of week is required for a weekly or biweekly plan.", nameof(dayOfWeek));
                }

                if (dayOfMonth is not null)
                {
                    throw new ArgumentException("A day of month must not be set for a weekly or biweekly plan.", nameof(dayOfMonth));
                }

                break;

            case RecurringBookingRecurrenceFrequency.Daily:
                if (dayOfWeek is not null || dayOfMonth is not null)
                {
                    throw new ArgumentException("Neither a day of week nor a day of month may be set for a daily plan.");
                }

                break;

            case RecurringBookingRecurrenceFrequency.Monthly:
                if (dayOfMonth is null || dayOfMonth is < 1 or > 31)
                {
                    throw new ArgumentException("A day of month between 1 and 31 is required for a monthly plan.", nameof(dayOfMonth));
                }

                if (dayOfWeek is not null)
                {
                    throw new ArgumentException("A day of week must not be set for a monthly plan.", nameof(dayOfWeek));
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(frequency));
        }
    }

    private static DateOnly NextOccurrenceOnOrAfter(
        DateOnly from, RecurringBookingRecurrenceFrequency frequency, DayOfWeek? dayOfWeek, int? dayOfMonth)
    {
        if (frequency == RecurringBookingRecurrenceFrequency.Daily)
        {
            return from;
        }

        if (frequency is RecurringBookingRecurrenceFrequency.Weekly or RecurringBookingRecurrenceFrequency.Biweekly)
        {
            var candidate = from;
            while (candidate.DayOfWeek != dayOfWeek!.Value)
            {
                candidate = candidate.AddDays(1);
            }

            return candidate;
        }

        var sameMonth = ClampToMonth(from.Year, from.Month, dayOfMonth!.Value);
        if (sameMonth >= from)
        {
            return sameMonth;
        }

        var nextMonth = from.AddDays(1 - from.Day).AddMonths(1);
        return ClampToMonth(nextMonth.Year, nextMonth.Month, dayOfMonth.Value);
    }

    private static DateOnly NextOccurrenceStrictlyAfter(
        DateOnly current, RecurringBookingRecurrenceFrequency frequency, DayOfWeek? dayOfWeek, int? dayOfMonth)
    {
        return frequency switch
        {
            RecurringBookingRecurrenceFrequency.Daily => current.AddDays(1),
            RecurringBookingRecurrenceFrequency.Weekly => current.AddDays(7),
            RecurringBookingRecurrenceFrequency.Biweekly => current.AddDays(14),
            RecurringBookingRecurrenceFrequency.Monthly => NextMonthOccurrence(current, dayOfMonth!.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(frequency)),
        };
    }

    private static DateOnly NextMonthOccurrence(DateOnly current, int dayOfMonth)
    {
        var firstOfNextMonth = current.AddDays(1 - current.Day).AddMonths(1);
        return ClampToMonth(firstOfNextMonth.Year, firstOfNextMonth.Month, dayOfMonth);
    }

    private static DateOnly ClampToMonth(int year, int month, int day) =>
        new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));
}
