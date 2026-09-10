using Nestly.Application;
using Nestly.Application.Abstractions.Time;
using Nestly.Application.Bookings;
using Nestly.Application.Payments;
using Nestly.Application.ProviderJobs;
using Nestly.Application.ProviderManagement;
using Nestly.Application.RecurringBookings;
using Nestly.Application.Storage;
using Nestly.Application.Tracking;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <inheritdoc cref="IProviderJobService"/>
public class ProviderJobService : IProviderJobService
{
    private readonly IBookingRepository _bookingRepository;
    private readonly IBookingProviderAssignmentRepository _assignmentRepository;
    private readonly IBookingProviderAssignmentService _assignmentService;
    private readonly IBookingCompletionProofRepository _completionProofRepository;
    private readonly IBookingEtaService _etaService;
    private readonly IRecurringBookingPlanRepository _recurringPlanRepository;
    private readonly IFileStorageService _fileStorageService;
    private readonly IProviderActiveJobLimitService _activeJobLimitService;
    private readonly IOverrunReassignmentService _overrunReassignmentService;
    private readonly IPaymentTransactionRepository _paymentTransactionRepository;
    private readonly IBusinessClock _clock;

    private static readonly BookingStatus[] ActiveJobStatuses =
    [
        BookingStatus.ProviderEnRoute,
        BookingStatus.ProviderArrived,
        BookingStatus.InProgress
    ];

    public ProviderJobService(
        IBookingRepository bookingRepository,
        IBookingProviderAssignmentRepository assignmentRepository,
        IBookingProviderAssignmentService assignmentService,
        IBookingCompletionProofRepository completionProofRepository,
        IBookingEtaService etaService,
        IRecurringBookingPlanRepository recurringPlanRepository,
        IFileStorageService fileStorageService,
        IProviderActiveJobLimitService activeJobLimitService,
        IOverrunReassignmentService overrunReassignmentService,
        IPaymentTransactionRepository paymentTransactionRepository,
        IBusinessClock clock)
    {
        _bookingRepository = bookingRepository;
        _assignmentRepository = assignmentRepository;
        _assignmentService = assignmentService;
        _completionProofRepository = completionProofRepository;
        _etaService = etaService;
        _recurringPlanRepository = recurringPlanRepository;
        _fileStorageService = fileStorageService;
        _activeJobLimitService = activeJobLimitService;
        _overrunReassignmentService = overrunReassignmentService;
        _paymentTransactionRepository = paymentTransactionRepository;
        _clock = clock;
    }

    public async Task<Result<ProviderJobSearchResponse>> ListAsync(Guid providerId, ProviderJobStatus? status, DateOnly? date)
    {
        var assignments = await _assignmentRepository.ListByProviderAsync(providerId);

        // Task 255: one GetByIdAsync per assignment - and each of those loaded
        // the booking's items, add-ons and status history, none of which this
        // list renders. Batched into a single summary query instead.
        var bookingsById = (await _bookingRepository.ListSummariesByIdsAsync(
                assignments.Select(a => a.BookingId).Distinct().ToList()))
            .ToDictionary(b => b.Id);

        // Task 300: the cadence of every plan behind this page's jobs, in one
        // more round trip rather than one per recurring row - the same batching
        // task 255 applied to the bookings above. Read live through task 296's
        // FK, never snapshotted onto the booking.
        var frequencyByPlanId = await _recurringPlanRepository.ListFrequenciesByIdsAsync(
            bookingsById.Values
                .Select(b => b.RecurringBookingPlanId)
                .OfType<Guid>()
                .Distinct()
                .ToList());

        // Bug fix (docs/OPEN-FIXES-FEATURES.csv "Payout figure"): payout
        // breakdown per row, batched the same way - see ResolvePayoutAsync's
        // doc comment for why the commission recorded on the transaction is
        // the source of truth rather than recomputing it here.
        var commissionByBookingId = (await _paymentTransactionRepository.ListCommissionSnapshotsByBookingIdsAsync(bookingsById.Keys.ToList()))
            .ToDictionary(s => s.BookingId, s => s.CommissionAmount ?? 0m);

        var items = new List<ProviderJobSummaryResponse>();
        foreach (var assignment in assignments)
        {
            if (!bookingsById.TryGetValue(assignment.BookingId, out var booking))
            {
                continue;
            }

            var jobStatus = ToJobStatus(assignment, booking);
            if (status is not null && jobStatus != status)
            {
                continue;
            }

            if (date is not null && booking.SlotDate != date)
            {
                continue;
            }

            items.Add(new ProviderJobSummaryResponse(
                assignment.Id,
                booking.Id,
                jobStatus,
                booking.CustomerNameSnapshot,
                MaskMobileUntilAccepted(assignment, booking.CustomerMobileSnapshot),
                booking.AddressLine1Snapshot,
                booking.AddressCitySnapshot,
                booking.AddressPincodeSnapshot,
                booking.SlotDate,
                booking.SlotStartTimeSnapshot,
                booking.SlotEndTimeSnapshot,
                booking.TotalPayableSnapshot,
                assignment.AssignedAt,
                assignment.ResponseDeadline,
                booking.RecurringBookingPlanId,
                // Null when the plan row has since been deleted out from under
                // the booking. That cannot happen today - BookingConfiguration
                // puts a Restrict foreign key on the column - but a job that
                // renders as an ordinary one-off is a strictly better failure
                // than one that throws the whole list away.
                booking.RecurringBookingPlanId is { } planId && frequencyByPlanId.TryGetValue(planId, out var frequency)
                    ? frequency
                    : null,
                booking.BookingReference,
                commissionByBookingId.GetValueOrDefault(booking.Id),
                booking.TotalPayableSnapshot - commissionByBookingId.GetValueOrDefault(booking.Id)));
        }

        return new ProviderJobSearchResponse(items);
    }

    public async Task<Result<ProviderJobDetailResponse>> GetDetailAsync(Guid providerId, Guid bookingId)
    {
        var resolved = await ResolveAsync(providerId, bookingId);
        if (resolved is null)
        {
            return NotFoundError();
        }

        return await ToDetailResponseAsync(resolved.Value.Assignment, resolved.Value.Booking);
    }

    public async Task<Result<ProviderJobDetailResponse>> AcceptAsync(Guid providerId, Guid bookingId)
    {
        var acceptResult = await _assignmentService.AcceptAsync(bookingId, providerId);
        if (acceptResult.IsFailure)
        {
            return acceptResult.Error;
        }

        var resolved = await ResolveAsync(providerId, bookingId);
        if (resolved is null)
        {
            return NotFoundError();
        }

        return await ToDetailResponseAsync(resolved.Value.Assignment, resolved.Value.Booking);
    }

    public async Task<Result<ProviderJobDetailResponse>> ExtendResponseDeadlineAsync(Guid providerId, Guid bookingId)
    {
        var extendResult = await _assignmentService.ExtendResponseDeadlineAsync(bookingId, providerId);
        if (extendResult.IsFailure)
        {
            return extendResult.Error;
        }

        var resolved = await ResolveAsync(providerId, bookingId);
        if (resolved is null)
        {
            return NotFoundError();
        }

        return await ToDetailResponseAsync(resolved.Value.Assignment, resolved.Value.Booking);
    }

    public async Task<Result<ProviderJobDetailResponse>> RejectAsync(Guid providerId, Guid bookingId, RejectJobRequest request)
    {
        var rejectResult = await _assignmentService.RejectByProviderAsync(bookingId, providerId, new RejectAssignmentRequest(request.Reason));
        if (rejectResult.IsFailure)
        {
            return rejectResult.Error;
        }

        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null)
        {
            return NotFoundError();
        }

        var assignment = await _assignmentRepository.GetByIdAsync(rejectResult.Value.Id);
        if (assignment is null)
        {
            return NotFoundError();
        }

        return await ToDetailResponseAsync(assignment, booking);
    }

    public async Task<Result<ProviderJobDetailResponse>> StartAsync(Guid providerId, Guid bookingId)
    {
        var resolved = await ResolveAcceptedAsync(providerId, bookingId);
        if (resolved is null)
        {
            return NotFoundError();
        }

        if (resolved.Value.Assignment.Status != BookingProviderAssignmentStatus.Accepted)
        {
            return Error.Business("ProviderJob.NotAccepted", "The job must be accepted before it can be started.");
        }

        var (assignment, booking) = resolved.Value;

        // One-active-job rule (provider-queue model): a provider may hold
        // several accepted future jobs, but only ever be actively working one.
        // Checked here rather than at accept time, since holding a queue is
        // the entire point of the model - this is the moment of activation.
        if (!ActiveJobStatuses.Contains(booking.Status)
            && await _activeJobLimitService.HasAnotherActiveJobAsync(providerId, bookingId))
        {
            return Error.Business(
                "ProviderJob.AnotherJobActive",
                "Complete your current active job before starting another one.");
        }

        try
        {
            booking.TransitionTo(BookingStatus.InProgress, "Provider started the job.");
        }
        catch (InvalidOperationException ex)
        {
            return Error.Business("ProviderJob.InvalidTransition", ex.Message);
        }

        await _bookingRepository.UpdateAsync(booking);

        return await ToDetailResponseAsync(assignment, booking);
    }

    public async Task<Result<ProviderJobDetailResponse>> MarkEnRouteAsync(Guid providerId, Guid bookingId)
    {
        var result = await TransitionAcceptedJobAsync(
            providerId, bookingId, BookingStatus.ProviderEnRoute, "Provider is en route to the customer.");

        // Task 271. The one moment an ETA is worth having before any driving
        // has been reported: the provider has just set off, so the customer's
        // screen turns from "assigned" to a live journey and the first estimate
        // should not wait for the first location ping. Refreshing on the
        // idempotent re-tap too is harmless and cheaper than special-casing it
        // - the service's own throttle rejects the second one.
        //
        // Not applied to MarkArrivedAsync: an arrived provider's remaining
        // travel time is zero by definition, and paying a routing provider to
        // be told so would be spending money to learn nothing.
        if (result.IsSuccess)
        {
            await _etaService.RefreshAsync(bookingId);
        }

        return result;
    }

    public Task<Result<ProviderJobDetailResponse>> MarkArrivedAsync(Guid providerId, Guid bookingId) =>
        TransitionAcceptedJobAsync(providerId, bookingId, BookingStatus.ProviderArrived, "Provider arrived at the customer's address.");

    /// <summary>
    /// The shared body of <see cref="MarkEnRouteAsync"/>/<see cref="MarkArrivedAsync"/>
    /// (task 270), guarded exactly like <see cref="StartAsync"/>: the caller
    /// must own this booking's live assignment and that assignment must be
    /// Accepted, or the job is reported as not found rather than refused, so a
    /// non-owning provider learns nothing about whose booking it is (SRS 28.3
    /// IDOR).
    ///
    /// Which target statuses are reachable from where is left entirely to
    /// <see cref="BookingLifecycle"/> via <see cref="Booking.TransitionTo"/> -
    /// no local status set is re-stated here, so this cannot drift from the
    /// table that task 264 wrote (in particular: en-route stays optional
    /// because Assigned -&gt; InProgress is still legal there, and arrived is
    /// mandatory before InProgress because ProviderEnRoute -&gt; InProgress is
    /// not).
    /// </summary>
    private async Task<Result<ProviderJobDetailResponse>> TransitionAcceptedJobAsync(
        Guid providerId,
        Guid bookingId,
        BookingStatus targetStatus,
        string reason)
    {
        var resolved = await ResolveAcceptedAsync(providerId, bookingId);
        if (resolved is null)
        {
            return NotFoundError();
        }

        var (assignment, booking) = resolved.Value;

        // IDEMPOTENT RE-TAP, and the reason this is not a 409. These two taps
        // come from a phone on the move, where the request can succeed and the
        // response still be lost; the client's only sane recovery is to send it
        // again. Answering the retry with a conflict would turn every flaky
        // moment into an error the provider has to interpret, so a re-tap that
        // asks for the state the booking is already in is treated as satisfied
        // and answers 200 with the unchanged job - the same body a first tap
        // returns, so the client needs no special case.
        //
        // Returning early rather than re-transitioning is the point: it is what
        // keeps a retry from appending a second status-history row and raising
        // a second ProviderEnRouteEvent/ProviderArrivedEvent (task 272 raises
        // those from the transition itself - nothing here raises them).
        //
        // Only the no-op is forgiven. A tap for a state the booking has already
        // moved past (en-route after arrival) is a real client error and still
        // fails on the transition table below.
        if (booking.Status == targetStatus)
        {
            return await ToDetailResponseAsync(assignment, booking);
        }

        // One-active-job rule (provider-queue model). Only relevant for the
        // en-route target: arrived's own precondition (en-route) is already an
        // active status, so this is always false by the time MarkArrivedAsync
        // reaches here - the check still runs uniformly rather than special-
        // casing by target, since "already active" is exactly the condition
        // that should let it through either way.
        if (!ActiveJobStatuses.Contains(booking.Status)
            && await _activeJobLimitService.HasAnotherActiveJobAsync(providerId, bookingId))
        {
            return Error.Business(
                "ProviderJob.AnotherJobActive",
                "Complete your current active job before starting another one.");
        }

        try
        {
            booking.TransitionTo(targetStatus, reason);
        }
        catch (InvalidOperationException ex)
        {
            return Error.Business("ProviderJob.InvalidTransition", ex.Message);
        }

        await _bookingRepository.UpdateAsync(booking);

        return await ToDetailResponseAsync(assignment, booking);
    }

    public async Task<Result<ProviderJobDetailResponse>> CompleteAsync(Guid providerId, Guid bookingId)
    {
        var resolved = await ResolveAcceptedAsync(providerId, bookingId);
        if (resolved is null)
        {
            return NotFoundError();
        }

        var (assignment, booking) = resolved.Value;
        if (booking.Status != BookingStatus.InProgress)
        {
            return Error.Business("ProviderJob.NotStarted", "The job must be started before it can be marked completed.");
        }

        var proofError = await _completionProofRepository.EnsureCompletionProofExistsAsync(bookingId);
        if (proofError is not null)
        {
            return proofError;
        }

        try
        {
            // Raises BookingStatusChangedEvent -> EscrowReleaseOnCompletionHandler,
            // which releases escrow to this provider and credits their
            // earning ledger (task 148) once this save commits.
            booking.TransitionTo(BookingStatus.Completed, "Provider marked the job completed.");
        }
        catch (InvalidOperationException ex)
        {
            return Error.Business("ProviderJob.InvalidTransition", ex.Message);
        }

        // The completion is verified at this point (booking is InProgress and a
        // completion proof exists, checked above), so move the assignment to its
        // terminal Completed state and stamp the finish time. This is what frees
        // the provider from occupying the rest of this job's slot window, so the
        // eligibility engine can offer them the next order (task: verified-
        // completion release, subject to travel/buffer/duration).
        var completedAtUtc = DateTime.UtcNow;
        assignment.Complete(completedAtUtc);

        await _bookingRepository.UpdateAsync(booking);
        await _assignmentRepository.UpdateAsync(assignment);

        // Overrun handling: a duration-based job's occupancy is always its
        // booked slot regardless of actual finish time (see
        // IProviderJobOccupancyService), so only a non-duration-based job that
        // finished after its slot's own end can have overrun anyone else's
        // schedule - re-check this provider's other same-day queued jobs only
        // in that case.
        if (!booking.IsDurationBasedSnapshot)
        {
            var completedLocal = _clock.ToBusinessLocal(completedAtUtc);
            bool overran = DateOnly.FromDateTime(completedLocal) > booking.SlotDate
                || (DateOnly.FromDateTime(completedLocal) == booking.SlotDate && completedLocal.TimeOfDay > booking.SlotEndTimeSnapshot);

            if (overran)
            {
                await _overrunReassignmentService.ReassignInfeasibleQueuedJobsAsync(providerId, booking.SlotDate, booking.Id);
            }
        }

        return await ToDetailResponseAsync(assignment, booking);
    }

    public async Task<Result<ProviderJobDetailResponse>> UploadCompletionProofAsync(Guid providerId, Guid bookingId, UploadJobCompletionProofRequest request)
    {
        // Accepted-or-Completed: supplementary evidence uploaded moments after
        // tapping "complete" is a real flow (see BookingProviderAssignment.SetCompletionProof).
        var resolved = await ResolveAcceptedOrCompletedAsync(providerId, bookingId);
        if (resolved is null)
        {
            return NotFoundError();
        }

        var (assignment, booking) = resolved.Value;
        try
        {
            assignment.SetCompletionProof(request.ProofRef);
        }
        catch (InvalidOperationException ex)
        {
            return Error.Business("ProviderJob.InvalidTransition", ex.Message);
        }

        await _assignmentRepository.UpdateAsync(assignment);

        return await ToDetailResponseAsync(assignment, booking);
    }

    public async Task<Result<UploadCompletionPhotoResponse>> UploadCompletionPhotoAsync(Guid providerId, Guid bookingId, Stream content, string fileNameHint, string contentType)
    {
        var resolved = await ResolveAcceptedOrCompletedAsync(providerId, bookingId);
        if (resolved is null)
        {
            return NotFoundError();
        }

        var photoRef = await _fileStorageService.SaveAsync(content, fileNameHint, contentType);
        return new UploadCompletionPhotoResponse(photoRef);
    }

    public async Task<Result<BookingCompletionProofResponse>> SubmitCompletionProofAsync(Guid providerId, Guid bookingId, SubmitCompletionProofRequest request)
    {
        var resolved = await ResolveAcceptedAsync(providerId, bookingId);
        if (resolved is null)
        {
            return NotFoundError();
        }

        var checklistAnswers = request.ChecklistAnswers
            .Select(a => new CompletionChecklistAnswer(a.Item, a.Completed, a.Notes))
            .ToList();

        var existing = await _completionProofRepository.GetByBookingIdAsync(bookingId);
        if (existing is null)
        {
            var proof = new BookingCompletionProof(Guid.NewGuid(), bookingId, providerId, request.PhotoRefs, checklistAnswers);
            await _completionProofRepository.AddAsync(proof);
            return proof.ToResponse()!;
        }

        existing.Update(request.PhotoRefs, checklistAnswers);
        await _completionProofRepository.UpdateAsync(existing);
        return existing.ToResponse()!;
    }

    public async Task<Result<BookingCompletionProofResponse?>> GetCompletionProofAsync(Guid providerId, Guid bookingId)
    {
        var resolved = await ResolveAcceptedOrCompletedAsync(providerId, bookingId);
        if (resolved is null)
        {
            return NotFoundError();
        }

        var proof = await _completionProofRepository.GetByBookingIdAsync(bookingId);
        return proof.ToResponse();
    }

    /// <summary>Resolves the most recent assignment this provider ever had on this booking, alongside the booking itself. Null if the booking doesn't exist or was never assigned to this provider (SRS 28.3 IDOR - the caller maps this to a 404, hiding the booking's existence from a non-owning provider).</summary>
    private async Task<(BookingProviderAssignment Assignment, Booking Booking)?> ResolveAsync(Guid providerId, Guid bookingId)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null)
        {
            return null;
        }

        var history = await _assignmentRepository.ListByBookingAsync(bookingId);
        var assignment = history.Where(a => a.ProviderId == providerId).MaxBy(a => a.AssignedAt);
        if (assignment is null)
        {
            return null;
        }

        return (assignment, booking);
    }

    /// <summary>Same as <see cref="ResolveAsync"/> but only returns a result when the resolved assignment is this provider's currently <see cref="BookingProviderAssignmentStatus.Accepted"/> one - the precondition for start/en-route/arrived/complete, none of which make sense to repeat once the job is done.</summary>
    private async Task<(BookingProviderAssignment Assignment, Booking Booking)?> ResolveAcceptedAsync(Guid providerId, Guid bookingId)
    {
        var resolved = await ResolveAsync(providerId, bookingId);
        if (resolved is null || resolved.Value.Assignment.Status != BookingProviderAssignmentStatus.Accepted)
        {
            return null;
        }

        return resolved;
    }

    /// <summary>Same as <see cref="ResolveAcceptedAsync"/> but also allows <see cref="BookingProviderAssignmentStatus.Completed"/> - the precondition for viewing or attaching completion evidence, which a provider must still be able to do for a job they just finished.</summary>
    private async Task<(BookingProviderAssignment Assignment, Booking Booking)?> ResolveAcceptedOrCompletedAsync(Guid providerId, Guid bookingId)
    {
        var resolved = await ResolveAsync(providerId, bookingId);
        if (resolved is null || resolved.Value.Assignment.Status is not
            (BookingProviderAssignmentStatus.Accepted or BookingProviderAssignmentStatus.Completed))
        {
            return null;
        }

        return resolved;
    }

    private static Error NotFoundError() =>
        Error.NotFound("ProviderJob.NotFound", "No job was found for this booking.");

    private static ProviderJobStatus ToJobStatus(BookingProviderAssignment assignment, Booking booking) => assignment.Status switch
    {
        BookingProviderAssignmentStatus.Assigned => ProviderJobStatus.Assigned,
        BookingProviderAssignmentStatus.Rejected => ProviderJobStatus.Rejected,
        BookingProviderAssignmentStatus.Reassigned => ProviderJobStatus.Reassigned,
        BookingProviderAssignmentStatus.Withdrawn => ProviderJobStatus.Withdrawn,
        // The assignment now carries its own terminal Completed state (set when
        // the job is verified-complete), so it maps straight through rather than
        // relying on the booking-status fallback below.
        BookingProviderAssignmentStatus.Completed => ProviderJobStatus.Completed,
        BookingProviderAssignmentStatus.Accepted => booking.Status switch
        {
            BookingStatus.Completed => ProviderJobStatus.Completed,
            BookingStatus.InProgress => ProviderJobStatus.InProgress,
            BookingStatus.ProviderArrived => ProviderJobStatus.Arrived,
            BookingStatus.ProviderEnRoute => ProviderJobStatus.EnRoute,
            _ => ProviderJobStatus.Accepted
        },
        _ => ProviderJobStatus.Assigned
    };

    private async Task<ProviderJobDetailResponse> ToDetailResponseAsync(BookingProviderAssignment assignment, Booking booking)
    {
        var (commissionAmount, netAmountToProvider) = await ResolvePayoutAsync(booking.Id, booking.TotalPayableSnapshot);

        return new(
            assignment.Id,
            booking.Id,
            ToJobStatus(assignment, booking),
            booking.CustomerNameSnapshot,
            MaskMobileUntilAccepted(assignment, booking.CustomerMobileSnapshot),
            booking.AddressLabelSnapshot,
            booking.AddressLine1Snapshot,
            booking.AddressLine2Snapshot,
            booking.AddressLandmarkSnapshot,
            booking.AddressCitySnapshot,
            booking.AddressStateSnapshot,
            booking.AddressPincodeSnapshot,
            booking.AddressContactNameSnapshot,
            MaskMobileUntilAccepted(assignment, booking.AddressContactMobileSnapshot),
            booking.SlotDate,
            booking.SlotStartTimeSnapshot,
            booking.SlotEndTimeSnapshot,
            booking.Items.Select(i => new ProviderJobItemResponse(i.NameSnapshot, i.Quantity, i.UnitPriceSnapshot)).ToList(),
            booking.TotalPayableSnapshot,
            assignment.AssignedAt,
            assignment.RespondedAt,
            assignment.ResponseDeadline,
            assignment.Notes,
            assignment.CompletionProofRef,
            booking.BookingReference,
            commissionAmount,
            netAmountToProvider);
    }

    /// <summary>
    /// Resolves one booking's provider payout breakdown (bug fix,
    /// docs/OPEN-FIXES-FEATURES.csv "Payout figure") from the commission
    /// <see cref="PaymentWebhookService"/> already records on its payment
    /// transaction at confirmation time - the same figure
    /// <see cref="EscrowReleaseOnCompletionHandler"/> actually deducts at
    /// settlement, so this can never show the provider a number the ledger
    /// later contradicts.
    ///
    /// A job is only ever assignable once its booking reaches Confirmed
    /// (see the auto-assignment job's gate), which is exactly when commission
    /// gets recorded - so a provider-visible job always has one. The
    /// zero-both fallback below only fires for the one legitimate case where
    /// it's still correct: a booking with nothing payable (fully wallet/AMC
    /// covered) never gets a payment transaction at all, and 0 commission on
    /// 0 gross is exactly right.
    /// </summary>
    private async Task<(decimal CommissionAmount, decimal NetAmountToProvider)> ResolvePayoutAsync(Guid bookingId, decimal totalPayable)
    {
        var transaction = await _paymentTransactionRepository.GetByBookingIdAsync(bookingId);
        decimal commissionAmount = transaction?.CommissionAmount ?? 0m;
        return (commissionAmount, totalPayable - commissionAmount);
    }

    /// <summary>
    /// Privacy gate for the customer's phone number(s): a provider can see a
    /// job's summary/detail before deciding whether to accept it (so they can
    /// judge location/payout), but should not be able to contact the customer
    /// until they have actually committed to the job by accepting it. Masked
    /// for every non-accepted state (Assigned/Rejected/Reassigned/Withdrawn),
    /// not just the "awaiting response" one - a provider who declined or was
    /// reassigned away from a job never earned visibility into it either.
    /// </summary>
    private static string MaskMobileUntilAccepted(BookingProviderAssignment assignment, string mobile) =>
        assignment.Status is BookingProviderAssignmentStatus.Accepted or BookingProviderAssignmentStatus.Completed
            ? mobile
            : "Hidden until accepted";
}
