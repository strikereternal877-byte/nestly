using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nestly.Application;
using Nestly.Application.Abstractions.Time;
using Nestly.Application.Bookings;
using Nestly.Application.Escrow;
using Nestly.Application.Notifications;
using Nestly.Application.Payments;
using Nestly.Application.ProviderManagement;
using Nestly.Application.Refunds;
using Nestly.Application.Reschedules;
using Nestly.Application.Settings;
using Nestly.Application.Slots;
using Nestly.Application.Wallet;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Reschedule eligibility, eligible-slot lookup, and confirmation (SRS
/// 11.15, 32.3, tasks 82a-d, 83). Status eligibility is derived from
/// <see cref="BookingLifecycle"/> the same way <see cref="CancellationService"/>
/// derives cancellation eligibility - see <see cref="Booking.Reschedule"/>.
/// New-slot availability is re-checked through <see cref="ISlotAvailabilityService"/>
/// (Phase 2's slot engine) at confirmation time, never trusted from an
/// earlier lookup (task 82c).
///
/// <para>
/// <b>Task 290 - a reschedule does not automatically drop the assigned
/// provider.</b> <see cref="Booking.Reschedule"/> only moves the slot; it
/// never touches <see cref="Booking.AssignedProviderId"/> or the live
/// <c>BookingProviderAssignment</c> row (see that method's own doc comment,
/// corrected by this task). <see cref="ReconcileProviderAssignmentAfterRescheduleAsync"/>
/// is what decides, after every reschedule, whether the same professional
/// stays on the job: kept when the new slot is still free for them (reusing
/// <see cref="IProviderScheduleConflictService"/>, the exact predicate task
/// 288 built for initial assignment, rather than a second one), dropped back
/// to <see cref="BookingStatus.AwaitingFulfilment"/> - <c>Reschedule</c>'s
/// own destination - when it now conflicts with another job.
/// </para>
/// </summary>
public class RescheduleService : IRescheduleService
{
    private readonly IBookingRepository _bookingRepository;
    private readonly IPaymentTransactionRepository _paymentRepository;
    private readonly IRefundTransactionRepository _refundTransactionRepository;
    private readonly ISlotAvailabilityService _slotAvailabilityService;
    private readonly IRescheduleRepository _rescheduleRepository;
    private readonly IBookingProviderAssignmentRepository _assignmentRepository;
    private readonly IProviderScheduleConflictService _scheduleConflictService;
    private readonly NestlyDbContext _context;
    private readonly IBusinessClock _businessClock;
    private readonly TimeProvider _timeProvider;
    private readonly IBookingPolicyProvider _policies;
    private readonly IProviderNotificationPublisher _providerNotifications;
    private readonly IProviderPlanReservationService _planReservations;
    private readonly IWalletService _wallet;
    private readonly IEscrowService _escrow;
    private readonly ILogger<RescheduleService> _logger;

    public RescheduleService(
        IBookingRepository bookingRepository,
        IPaymentTransactionRepository paymentRepository,
        IRefundTransactionRepository refundTransactionRepository,
        ISlotAvailabilityService slotAvailabilityService,
        IRescheduleRepository rescheduleRepository,
        IBookingProviderAssignmentRepository assignmentRepository,
        IProviderScheduleConflictService scheduleConflictService,
        NestlyDbContext context,
        IBusinessClock businessClock,
        TimeProvider timeProvider,
        IBookingPolicyProvider policies,
        IProviderNotificationPublisher providerNotifications,
        IProviderPlanReservationService planReservations,
        IWalletService wallet,
        IEscrowService escrow,
        ILogger<RescheduleService> logger)
    {
        _bookingRepository = bookingRepository;
        _paymentRepository = paymentRepository;
        _refundTransactionRepository = refundTransactionRepository;
        _slotAvailabilityService = slotAvailabilityService;
        _rescheduleRepository = rescheduleRepository;
        _assignmentRepository = assignmentRepository;
        _scheduleConflictService = scheduleConflictService;
        _context = context;
        _businessClock = businessClock;
        _timeProvider = timeProvider;
        _policies = policies;
        _providerNotifications = providerNotifications;
        _planReservations = planReservations;
        _wallet = wallet;
        _escrow = escrow;
        _logger = logger;
    }

    public async Task<Result<RescheduleEligibilityResponse>> GetEligibilityAsync(Guid customerId, Guid bookingId)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null || booking.CustomerId != customerId)
        {
            return Error.NotFound("Reschedule.BookingNotFound", "The specified booking does not exist.");
        }

        return Result.Success(await EvaluateEligibilityAsync(booking));
    }

    public async Task<Result<SlotAvailabilityResponse>> GetEligibleSlotsAsync(Guid customerId, Guid bookingId, Guid localityId, DateOnly date)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null || booking.CustomerId != customerId)
        {
            return Error.NotFound("Reschedule.BookingNotFound", "The specified booking does not exist.");
        }

        var eligibility = await EvaluateEligibilityAsync(booking);
        if (!eligibility.IsEligible)
        {
            return Error.Business("Reschedule.NotEligible", eligibility.IneligibilityReason!);
        }

        Guid serviceId = booking.Items.Count > 0
            ? booking.Items[0].ServiceId
            : throw new InvalidOperationException($"Booking {bookingId} has no items to resolve a service from.");

        return await _slotAvailabilityService.GetAvailableSlotsAsync(serviceId, localityId, date);
    }

    public async Task<Result<RescheduleOutcomeResponse>> ConfirmRescheduleAsync(Guid customerId, Guid bookingId, RescheduleBookingRequest request)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null || booking.CustomerId != customerId)
        {
            return Error.NotFound("Reschedule.BookingNotFound", "The specified booking does not exist.");
        }

        var eligibility = await EvaluateEligibilityAsync(booking);
        if (!eligibility.IsEligible)
        {
            return Error.Business("Reschedule.NotEligible", eligibility.IneligibilityReason!);
        }

        return await ExecuteRescheduleAsync(booking, request, RescheduleActor.Customer);
    }

    public async Task<Result<RescheduleOutcomeResponse>> AdminRescheduleAsync(Guid bookingId, RescheduleBookingRequest request)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null)
        {
            return Error.NotFound("Reschedule.BookingNotFound", "The specified booking does not exist.");
        }

        // Only the state-machine invariant is enforced here - the count-limit
        // and min-hours-before-slot policy checks in EvaluateEligibilityAsync
        // are deliberately skipped, see this method's doc comment on
        // IRescheduleService.
        if (!BookingLifecycle.IsValidTransition(booking.Status, BookingStatus.Rescheduled))
        {
            return Error.Business("Reschedule.NotEligible", $"A booking in status '{booking.Status}' cannot be rescheduled.");
        }

        return await ExecuteRescheduleAsync(booking, request, RescheduleActor.Admin);
    }

    /// <summary>
    /// Shared confirmation path for both <see cref="ConfirmRescheduleAsync"/>
    /// and <see cref="AdminRescheduleAsync"/>: revalidates the chosen slot
    /// through the slot engine (task 82c - never trusted from an earlier
    /// lookup, for either actor), computes the late-reschedule fee via
    /// <see cref="RescheduleFeeCalculator"/>, applies the booking-level
    /// transition, and records the <see cref="BookingReschedule"/> history
    /// row attributed to <paramref name="actor"/>.
    /// </summary>
    private async Task<Result<RescheduleOutcomeResponse>> ExecuteRescheduleAsync(Booking booking, RescheduleBookingRequest request, RescheduleActor actor)
    {
        Guid serviceId = booking.Items.Count > 0
            ? booking.Items[0].ServiceId
            : throw new InvalidOperationException($"Booking {booking.Id} has no items to resolve a service from.");

        // Slot revalidation via the slot engine (task 82c): the exact same
        // computation RevalidateSlotAsync performs, but this also returns
        // the slot's own name/time details needed for the new snapshot.
        var availability = await _slotAvailabilityService.GetAvailableSlotsAsync(serviceId, request.LocalityId, request.SlotDate);
        if (availability.IsFailure)
        {
            return availability.Error;
        }

        var chosenSlot = availability.Value.Slots.FirstOrDefault(s => s.SlotWindowId == request.SlotWindowId);
        if (!availability.Value.IsServiceable || chosenSlot is null)
        {
            return Error.Business("Reschedule.SlotNotAvailable", "The selected slot is no longer available.");
        }

        var previousSlot = new BookingSlotSummary(booking.SlotWindowId, booking.SlotWindowNameSnapshot, booking.SlotDate, booking.SlotStartTimeSnapshot, booking.SlotEndTimeSnapshot);
        // Who was on the job, and under which assignment row, before anything moves: saving the reschedule below
        // dispatches the auto-assigner in-process, which may hand the job to the same professional again (a new row)
        // or to somebody else, and the reconcile afterwards has to tell those apart.
        Guid? previousProviderId = booking.AssignedProviderId;
        bool hadProfessional = previousProviderId is not null;
        Guid? previousAssignmentId = hadProfessional
            ? (await _assignmentRepository.GetActiveByBookingAsync(booking.Id))?.Id
            : null;

        bool movingSlot = previousSlot.SlotWindowId != chosenSlot.SlotWindowId || previousSlot.Date != request.SlotDate;

        var policy = await _policies.GetRescheduleAsync();
        var cancellationPolicy = await _policies.GetCancellationAsync();

        decimal payableAmount = await ResolvePayableAmountAsync(booking);
        // The snapshot is a business wall-clock time; lifting it to a real
        // instant is what makes "how long until the slot" comparable with UTC
        // now (see IBusinessClock) - subtracting the two directly skewed every
        // late-reschedule fee decision by the business timezone's offset.
        DateTime currentSlotStartUtc = _businessClock.ToUtc(booking.SlotDate, booking.SlotStartTimeSnapshot);
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        var feeOutcome = RescheduleFeeCalculator.Compute(
            payableAmount, currentSlotStartUtc - now, policy.LateFeeThresholdHours, policy.LateRescheduleFeePercentage);

        // A customer's own late reschedule is paid for from their wallet, in the same step as the move. An admin moving a
        // booking never charges the customer, and a "reschedule" that leaves the slot where it was gives them nothing to
        // pay for. Checked before a seat is taken on the new slot, so a wallet that is too empty costs nothing to find out.
        // And only while collection is switched on: off, the fee is recorded on the booking and nothing is taken or refused.
        decimal feeToCollect = policy.CollectLateFeeFromWallet && actor == RescheduleActor.Customer && movingSlot && feeOutcome.IsLate
            ? feeOutcome.FeeAmount
            : 0m;
        if (feeToCollect > 0)
        {
            var balance = await _wallet.GetBalanceAsync(booking.CustomerId);
            if (balance.IsFailure)
            {
                return balance.Error;
            }

            if (balance.Value.Balance < feeToCollect)
            {
                return LateFeeWalletShort(feeToCollect, balance.Value.Balance);
            }
        }

        // Take a seat on the target slot before giving up the current one.
        // Availability above only reports what is free; it does not hold
        // anything, so without this reservation a reschedule could move any
        // number of bookings onto a capped window - the cap was only ever
        // enforced on the create path (BookingService.CreateAsync).
        if (movingSlot)
        {
            var reservation = await _slotAvailabilityService.ReserveSlotAsync(chosenSlot.SlotWindowId, request.SlotDate);
            if (reservation.IsFailure)
            {
                return reservation.Error;
            }
        }

        // Debited only once the seat is held, and handed back if anything below fails: the customer pays for a move that
        // happened, never for one that did not. The debit is the authoritative balance check (the read above was only a
        // cheap early answer) - it is serialisable against every other wallet write.
        Guid rescheduleId = Guid.NewGuid();
        if (feeToCollect > 0)
        {
            var debit = await _wallet.DebitAsync(
                booking.CustomerId, feeToCollect, WalletSourceType.RescheduleFee, rescheduleId,
                $"Late reschedule fee for booking {booking.BookingReference}");
            if (debit.IsFailure)
            {
                // The balance moved between the check and the debit (spent elsewhere in the meantime): give the seat back.
                await _slotAvailabilityService.ReleaseSlotAsync(chosenSlot.SlotWindowId, request.SlotDate);
                if (debit.Error.Code != "Wallet.InsufficientBalance")
                {
                    return debit.Error;
                }

                var latest = await _wallet.GetBalanceAsync(booking.CustomerId);
                return LateFeeWalletShort(feeToCollect, latest.IsSuccess ? latest.Value.Balance : 0m);
            }
        }

        // What cancelling the slot being given up would cost right now, per
        // the cancellation policy CancellationService itself enforces - not
        // the separate reschedule-fee policy above. Booking.Reschedule locks
        // this in as a floor under any future cancellation's fee (see
        // Booking.LockedCancellationFeeSnapshot's doc comment) so moving the
        // slot can never be used to erase a cancellation fee already owed.
        var cancellationOutcomeOnSlotGivenUp = CancellationFeeCalculator.Compute(
            payableAmount, currentSlotStartUtc - now, cancellationPolicy.FreeCancellationWindowHours, cancellationPolicy.LateCancellationFeePercentage);

        try
        {
            booking.Reschedule(
                chosenSlot.SlotWindowId, request.SlotDate, chosenSlot.Name, chosenSlot.StartTime, chosenSlot.EndTime, request.Reason,
                cancellationOutcomeOnSlotGivenUp.FeeAmount);
            await _bookingRepository.UpdateAsync(booking);
        }
        catch
        {
            // The reservation above already committed independently of this
            // write (it is its own atomic conditional UPDATE, not part of
            // any transaction wrapping this method) - if the booking write
            // itself now fails for any reason (a DB error, a timeout), that
            // reservation is not rolled back with it and nothing else will
            // ever give it up. Compensate immediately rather than leaving
            // the new slot's capacity counter permanently decremented for a
            // reschedule that never actually happened.
            if (movingSlot)
            {
                await _slotAvailabilityService.ReleaseSlotAsync(chosenSlot.SlotWindowId, request.SlotDate);
            }

            if (feeToCollect > 0)
            {
                await ReturnLateFeeAsync(booking, feeToCollect, rescheduleId);
            }

            throw;
        }

        // Recorded as soon as the move is saved - before the provider reconcile below, which can still fail - so the money
        // that has moved is never without its history row: that row is what a later cancellation counts against its fee.
        var history = new BookingReschedule(
            rescheduleId,
            booking.Id,
            actor,
            request.Reason,
            previousSlot.SlotWindowId,
            previousSlot.Date,
            previousSlot.StartTime,
            previousSlot.EndTime,
            chosenSlot.SlotWindowId,
            request.SlotDate,
            chosenSlot.StartTime,
            chosenSlot.EndTime,
            feeOutcome.IsLate,
            feeOutcome.FeeAmount,
            feeToCollect);

        await _rescheduleRepository.AddAsync(history);

        if (feeToCollect > 0)
        {
            // Internal bookkeeping only: the wallet debit above is the real money movement and the history row the proof
            // of it, so a failure here is logged for reconciliation rather than turned into a failed reschedule.
            try
            {
                await _escrow.RecordRescheduleFeeAsync(booking.Id, rescheduleId, feeToCollect);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Late-reschedule fee {FeeAmount} for booking {BookingId} (reschedule {RescheduleId}) was taken from the wallet but could not be booked as platform revenue.",
                    feeToCollect, booking.Id, rescheduleId);
            }
        }

        // Task 290: the slot move above always persists regardless of what
        // happens to the assignment - only "keep the same professional" can
        // fail, and it must never take the reschedule itself down with it.
        ProfessionalAfterReschedule professional;
        (booking, professional) = await ReconcileProviderAssignmentAfterRescheduleAsync(
            booking, previousSlot, previousProviderId, previousAssignmentId, respectPlanReservations: actor == RescheduleActor.Customer);

        // Only once the move is committed: releasing first would let a
        // concurrent booking take the seat this reschedule might still need to
        // roll back to.
        if (movingSlot)
        {
            await _slotAvailabilityService.ReleaseSlotAsync(previousSlot.SlotWindowId, previousSlot.Date);
        }

        int reschedulesUsed = await _rescheduleRepository.CountByBookingAsync(booking.Id);

        return Result.Success(new RescheduleOutcomeResponse(
            booking.Id,
            booking.Status,
            previousSlot,
            new BookingSlotSummary(chosenSlot.SlotWindowId, chosenSlot.Name, request.SlotDate, chosenSlot.StartTime, chosenSlot.EndTime),
            feeOutcome.IsLate,
            feeOutcome.FeeAmount,
            reschedulesUsed,
            policy.MaxReschedulesPerBooking,
            history.CreatedAtUtc,
            policy.LateFeeThresholdHours,
            policy.LateRescheduleFeePercentage,
            previousSlot.Date.ToDateTime(TimeOnly.FromTimeSpan(previousSlot.StartTime)).AddHours(-(double)policy.LateFeeThresholdHours),
            payableAmount,
            booking.LockedCancellationFeeSnapshot,
            hadProfessional ? professional : ProfessionalAfterReschedule.NoneAssigned,
            feeToCollect));
    }

    /// <summary>The customer's wallet cannot cover the late fee. 422 with the numbers, so the screen can say exactly what to add.</summary>
    private static Error LateFeeWalletShort(decimal fee, decimal balance) => Error.Business(
        "Reschedule.LateFeeWalletShort",
        $"Rescheduling now is a late reschedule with a fee of \u20B9{fee:0.00}, taken from your wallet. Your wallet has \u20B9{balance:0.00} - " +
        $"add \u20B9{fee - balance:0.00} to reschedule at this time, or cancel the booking instead.");

    /// <summary>
    /// Puts a late fee back in the wallet after the reschedule it was taken for failed to save. Best effort by necessity - this
    /// runs while the original failure is already propagating, and must not replace it - so a failure here is logged loudly
    /// with everything needed to return the money by hand.
    /// </summary>
    private async Task ReturnLateFeeAsync(Booking booking, decimal fee, Guid rescheduleId)
    {
        try
        {
            await _wallet.CreditAsync(
                booking.CustomerId, fee, WalletSourceType.RescheduleFeeReversal, rescheduleId,
                $"Late reschedule fee returned - booking {booking.BookingReference} could not be rescheduled");
        }
        catch (Exception exception)
        {
            _logger.LogCritical(
                exception,
                "Late-reschedule fee {FeeAmount} was taken from customer {CustomerId}'s wallet for booking {BookingId} (reschedule {RescheduleId}) but the reschedule failed and the fee could not be returned - return it by hand.",
                fee, booking.CustomerId, booking.Id, rescheduleId);
        }
    }

    /// <summary>
    /// Task 290. Called after the slot move has already been persisted -
    /// decides what happens to the booking's live provider assignment
    /// against the *new* slot (already set on <paramref name="booking"/> by
    /// this point, which is what makes <see cref="IProviderScheduleConflictService.FindConflictAsync"/>'s
    /// own self-exclusion correct here rather than comparing against the old
    /// slot). Returns the booking to keep using - normally the same instance,
    /// but a fresh reload after the race-losing branch below.
    /// </summary>
    ///
    /// <para>
    /// Two things beyond task 290's original rule. A customer's reschedule is not an admin override, so the professional
    /// is also let go when the new time is one a recurring plan of someone else holds them for
    /// (<see cref="IProviderPlanReservationService"/>) - an admin's own reschedule keeps the older, conflict-only rule.
    /// And the professional is told either way: kept, they are sent the new time; let go, they are told the job is no
    /// longer theirs - without it a job quietly changed time under an accepted assignment, or quietly vanished from a
    /// schedule.
    /// </para>
    ///
    /// <para>
    /// "Who is on the job" is judged against <paramref name="previousProviderId"/>, captured before the move, not read off
    /// the booking now: saving the move has already run the auto-assigner, which offers the job to the professional who was
    /// on it first (<see cref="ProviderAutoAssignmentHandler"/>) and only otherwise to somebody else. So a job that is now
    /// somebody else's is reported as let go, and the professional it was taken from is told - the new one has been sent
    /// the job as an offer by the assigner itself. A professional re-offered the job by the assigner already holds a message
    /// with the new time (<paramref name="previousAssignmentId"/> no longer being the live row is how that shows), so a
    /// second one is not sent.
    /// </para>
    private async Task<(Booking Booking, ProfessionalAfterReschedule Professional)> ReconcileProviderAssignmentAfterRescheduleAsync(
        Booking booking, BookingSlotSummary previousSlot, Guid? previousProviderId, Guid? previousAssignmentId, bool respectPlanReservations)
    {
        if (previousProviderId is not { } formerProviderId)
        {
            // Nobody was on the job, so there is nobody to keep or let go. Anyone on it now was offered it by the
            // auto-assigner just now, and has been told so.
            return (booking, ProfessionalAfterReschedule.NoneAssigned);
        }

        if (booking.AssignedProviderId != formerProviderId)
        {
            // The assigner gave the job to somebody else (the professional could not take the new time and another
            // was offered it) - the one it was taken from has to be told, nothing else would.
            await TellProfessionalAsync(formerProviderId, booking, previousSlot, kept: false, alreadyOfferedTheNewTime: false);
            return (booking, ProfessionalAfterReschedule.Released);
        }

        var providerId = formerProviderId;
        var activeAssignment = await _assignmentRepository.GetActiveByBookingAsync(booking.Id);
        if (activeAssignment is null)
        {
            // AssignedProviderId set with no backing live assignment row -
            // a stale display field, not something this task's fix should
            // carry forward silently.
            booking.AssignProvider(null);
            await _bookingRepository.UpdateAsync(booking);
            return (booking, ProfessionalAfterReschedule.NoneAssigned);
        }

        // Read before the transaction opens: whether another plan holds this professional at the new time.
        bool reservedByAnotherPlan = respectPlanReservations
            && await _planReservations.IsReservedByAnotherPlanAsync(providerId, booking.Id);

        // Task 288's own reasoning applies verbatim: the read this decides on
        // and the write that acts on it must not be split by a concurrent
        // assignment on the same connection. See BookingProviderAssignmentService's
        // class doc comment for what Serializable does and does not guarantee
        // per database provider (Postgres' ex_booking_provider_no_double_booking
        // exclusion constraint is the backstop caught below; SQLite has neither).
        await using var dbTransaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var conflict = await _scheduleConflictService.FindConflictAsync(providerId, booking);

        try
        {
            if (conflict is null && !reservedByAnotherPlan)
            {
                // Only announce the status when the booking is not already
                // sitting on it. Booking.Reschedule leaves it at
                // AwaitingFulfilment, and the UpdateAsync above dispatches
                // that BookingStatusChangedEvent to ProviderAutoAssignmentHandler,
                // which runs in-process and synchronously - so by the time
                // this method is reached the booking has frequently already
                // been promoted to Assigned by the auto-assigner (that is
                // also why AssignedProviderId is set at all). BookingLifecycle
                // deliberately has no Assigned -> Assigned self-edge, so
                // re-announcing it throws InvalidOperationException - which
                // the catch below does not handle (it is scoped to
                // DbUpdateException) and which therefore escaped as a 500,
                // breaking the guarantee ExecuteRescheduleAsync states above:
                // only "keep the same professional" may fail, never the
                // reschedule itself. Leaving it Assigned is exactly the
                // intended end state either way.
                if (booking.Status != BookingStatus.Assigned)
                {
                    booking.TransitionTo(BookingStatus.Assigned, "Reschedule kept the assigned professional; the new slot is still free for them.");
                }
            }
            else
            {
                activeAssignment.Withdraw();
                await _assignmentRepository.UpdateAsync(activeAssignment);
                booking.AssignProvider(null);
            }

            await _bookingRepository.UpdateAsync(booking);
            await dbTransaction.CommitAsync();

            bool kept = conflict is null && !reservedByAnotherPlan;
            await TellProfessionalAsync(
                providerId, booking, previousSlot, kept,
                alreadyOfferedTheNewTime: kept && activeAssignment.Id != previousAssignmentId);
            return (booking, kept ? ProfessionalAfterReschedule.Kept : ProfessionalAfterReschedule.Released);
        }
        catch (DbUpdateException)
        {
            // Either the exclusion constraint rejected the write or the
            // serializable transaction lost a race - a competing assignment
            // committed between the conflict check above and this write.
            // The slot move already persisted before this method ran; only
            // "keep the same professional" failed, so drop the assignment
            // and leave the booking needing reassignment rather than
            // surfacing this as a raw 500 to the caller.
            await dbTransaction.RollbackAsync();
            DetachPendingAssignmentWrites();

            var freshBooking = await _bookingRepository.GetByIdAsync(booking.Id)
                ?? throw new InvalidOperationException($"Booking {booking.Id} disappeared mid-reschedule.");
            var freshAssignment = await _assignmentRepository.GetActiveByBookingAsync(booking.Id);
            if (freshAssignment is not null)
            {
                freshAssignment.Withdraw();
                await _assignmentRepository.UpdateAsync(freshAssignment);
            }

            freshBooking.AssignProvider(null);
            if (freshBooking.Status == BookingStatus.Assigned)
            {
                freshBooking.TransitionTo(BookingStatus.AwaitingFulfilment, "Reassignment needed - the professional was double-booked by a concurrent change.");
            }

            await _bookingRepository.UpdateAsync(freshBooking);
            await TellProfessionalAsync(providerId, freshBooking, previousSlot, kept: false, alreadyOfferedTheNewTime: false);
            return (freshBooking, ProfessionalAfterReschedule.Released);
        }
    }

    /// <summary>
    /// Best effort, after the change is committed (the publisher never throws): a professional is told when a job
    /// assigned to them moves, or when it is taken off them because the new time does not work.
    /// </summary>
    private Task TellProfessionalAsync(
        Guid providerId, Booking booking, BookingSlotSummary previousSlot, bool kept, bool alreadyOfferedTheNewTime)
    {
        if (kept && alreadyOfferedTheNewTime)
        {
            // The assigner re-offered them the job for the new time a moment ago ("New job offer ... respond before it
            // expires"); a second message about the same change is noise.
            return Task.CompletedTask;
        }

        string was = $"{previousSlot.Date:d MMM} at {previousSlot.StartTime:hh\\:mm}-{previousSlot.EndTime:hh\\:mm}";
        string now = $"{booking.SlotDate:d MMM} at {booking.SlotStartTimeSnapshot:hh\\:mm}-{booking.SlotEndTimeSnapshot:hh\\:mm}";

        return kept
            ? _providerNotifications.NotifyAsync(
                providerId,
                ProviderNotificationType.JobRescheduled,
                "Job rescheduled",
                $"The booking was moved from {was} to {now}. It is still assigned to you - please check the new time works.",
                deepLinkPath: $"/jobs/{booking.Id}")
            : _providerNotifications.NotifyAsync(
                providerId,
                ProviderNotificationType.JobUnassigned,
                "Job taken off your schedule",
                $"The booking on {was} was moved to {now}, which does not work with your schedule, so it is no longer assigned to you. Nothing else to do.",
                deepLinkPath: "/jobs");
    }

    private void DetachPendingAssignmentWrites()
    {
        var pending = _context.ChangeTracker.Entries()
            .Where(e => e.Entity is BookingProviderAssignment or Booking)
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .ToList();

        foreach (var entry in pending)
        {
            entry.State = EntityState.Detached;
        }
    }

    private async Task<RescheduleEligibilityResponse> EvaluateEligibilityAsync(Booking booking)
    {
        var policy = await _policies.GetRescheduleAsync();

        if (!BookingLifecycle.IsValidTransition(booking.Status, BookingStatus.Rescheduled))
        {
            return new RescheduleEligibilityResponse(
                false, $"This booking is \"{BookingStatusMapper.LabelFor(booking.Status)}\", so it can't be rescheduled.", 0, policy.MaxReschedulesPerBooking, policy.MinHoursBeforeSlot);
        }

        int reschedulesUsed = await _rescheduleRepository.CountByBookingAsync(booking.Id);
        if (reschedulesUsed >= policy.MaxReschedulesPerBooking)
        {
            return new RescheduleEligibilityResponse(
                false, $"This booking has already been rescheduled the maximum of {policy.MaxReschedulesPerBooking} time(s).",
                reschedulesUsed, policy.MaxReschedulesPerBooking, policy.MinHoursBeforeSlot);
        }

        // Business wall-clock lifted to a real instant before meeting UTC now
        // - see IBusinessClock and the same correction in ExecuteRescheduleAsync.
        DateTime slotStartUtc = _businessClock.ToUtc(booking.SlotDate, booking.SlotStartTimeSnapshot);
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        double hoursUntilSlot = (slotStartUtc - now).TotalHours;

        if (hoursUntilSlot < (double)policy.MinHoursBeforeSlot)
        {
            return new RescheduleEligibilityResponse(
                false, "The reschedule window for this booking's slot has expired.", reschedulesUsed, policy.MaxReschedulesPerBooking, policy.MinHoursBeforeSlot);
        }

        // Eligible: say what a reschedule made right now would be recorded as and what it would lock in.
        decimal payable = await ResolvePayableAmountAsync(booking);
        DateTime slotStartLocal = booking.SlotDate.ToDateTime(TimeOnly.FromTimeSpan(booking.SlotStartTimeSnapshot));
        TimeSpan untilSlot = slotStartUtc - now;
        var cancellationPolicy = await _policies.GetCancellationAsync();
        var lateNow = RescheduleFeeCalculator.Compute(payable, untilSlot, policy.LateFeeThresholdHours, policy.LateRescheduleFeePercentage);
        var cancellationFeeToday = CancellationFeeCalculator.Compute(
            payable, untilSlot, cancellationPolicy.FreeCancellationWindowHours, cancellationPolicy.LateCancellationFeePercentage);

        // The late fee comes out of the wallet when the customer confirms, so say now whether the wallet can cover it.
        decimal walletBalance = 0m;
        decimal shortfall = 0m;
        if (policy.CollectLateFeeFromWallet && lateNow.IsLate && lateNow.FeeAmount > 0)
        {
            var balance = await _wallet.GetBalanceAsync(booking.CustomerId);
            walletBalance = balance.IsSuccess ? balance.Value.Balance : 0m;
            shortfall = Math.Max(0m, lateNow.FeeAmount - walletBalance);
        }

        return new RescheduleEligibilityResponse(
            true, null, reschedulesUsed, policy.MaxReschedulesPerBooking, policy.MinHoursBeforeSlot,
            LateFeeThresholdHours: policy.LateFeeThresholdHours,
            LateRescheduleFeePercentage: policy.LateRescheduleFeePercentage,
            FreeRescheduleEndsAt: slotStartLocal.AddHours(-(double)policy.LateFeeThresholdHours),
            LastRescheduleAt: slotStartLocal.AddHours(-(double)policy.MinHoursBeforeSlot),
            IsLateNow: lateNow.IsLate,
            LateFeeIfRescheduledNow: lateNow.FeeAmount,
            FeeBasisAmount: payable,
            CancellationFeeLockedIn: Math.Max(booking.LockedCancellationFeeSnapshot, cancellationFeeToday.FeeAmount),
            WalletBalance: walletBalance,
            LateFeeShortfall: shortfall,
            LateFeeIsCollected: policy.CollectLateFeeFromWallet);
    }

    /// <summary>
    /// What the booking is still funded by, and therefore the base the
    /// late-reschedule fee percentage applies against - 0 if it was never
    /// funded at all (a 100%-off coupon, a free subscription visit, an AMC
    /// redemption) or has already been fully refunded.
    ///
    /// <para>
    /// Task 364: this is the gateway payment PLUS the wallet balance the
    /// booking consumed at checkout, computed by the same
    /// <see cref="RefundAllocationCalculator"/> <c>CancellationService</c>
    /// charges its fee against and <c>RefundService</c> allocates against, so
    /// no two of the three can compute a fee off a different base. Reading
    /// only the gateway payment (as this did before) understated a part-wallet
    /// booking's recorded late-reschedule fee by exactly the wallet-funded
    /// share, and zeroed it outright on a fully wallet-covered booking, which
    /// has no <see cref="PaymentTransaction"/> at all (task 331).
    /// </para>
    ///
    /// <para>
    /// Prior refunds are listed by booking rather than by payment transaction:
    /// task 356 made a wallet-funded refund a row with no
    /// <c>PaymentTransactionId</c>, so the by-payment query cannot see one and
    /// would count wallet money already returned as still refundable.
    /// </para>
    /// </summary>
    private async Task<decimal> ResolvePayableAmountAsync(Booking booking)
    {
        var payment = await _paymentRepository.GetByBookingIdAsync(booking.Id);
        decimal paymentSettledAmount = payment is { Status: PaymentTransactionStatus.Success } ? payment.Amount : 0m;
        var priorRefunds = await _refundTransactionRepository.ListByBookingAsync(booking.Id);

        return RefundAllocationCalculator
            .ComputeRemaining(paymentSettledAmount, booking.WalletCreditAppliedSnapshot ?? 0m, priorRefunds)
            .Total;
    }
}
