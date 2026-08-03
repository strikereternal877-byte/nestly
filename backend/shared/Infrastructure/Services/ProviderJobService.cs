using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.ProviderJobs;
using Nestly.Application.ProviderManagement;
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

    public ProviderJobService(
        IBookingRepository bookingRepository,
        IBookingProviderAssignmentRepository assignmentRepository,
        IBookingProviderAssignmentService assignmentService,
        IBookingCompletionProofRepository completionProofRepository)
    {
        _bookingRepository = bookingRepository;
        _assignmentRepository = assignmentRepository;
        _assignmentService = assignmentService;
        _completionProofRepository = completionProofRepository;
    }

    public async Task<Result<ProviderJobSearchResponse>> ListAsync(Guid providerId, ProviderJobStatus? status, DateOnly? date)
    {
        var assignments = await _assignmentRepository.ListByProviderAsync(providerId);

        var items = new List<ProviderJobSummaryResponse>();
        foreach (var assignment in assignments)
        {
            var booking = await _bookingRepository.GetByIdAsync(assignment.BookingId);
            if (booking is null)
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
                booking.CustomerMobileSnapshot,
                booking.AddressLine1Snapshot,
                booking.AddressCitySnapshot,
                booking.AddressPincodeSnapshot,
                booking.SlotDate,
                booking.SlotStartTimeSnapshot,
                booking.SlotEndTimeSnapshot,
                booking.TotalPayableSnapshot,
                assignment.AssignedAt,
                assignment.ResponseDeadline));
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

        return ToDetailResponse(resolved.Value.Assignment, resolved.Value.Booking);
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

        return ToDetailResponse(resolved.Value.Assignment, resolved.Value.Booking);
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

        return ToDetailResponse(assignment, booking);
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
        try
        {
            booking.TransitionTo(BookingStatus.InProgress, "Provider started the job.");
        }
        catch (InvalidOperationException ex)
        {
            return Error.Business("ProviderJob.InvalidTransition", ex.Message);
        }

        await _bookingRepository.UpdateAsync(booking);

        return ToDetailResponse(assignment, booking);
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

        await _bookingRepository.UpdateAsync(booking);

        return ToDetailResponse(assignment, booking);
    }

    public async Task<Result<ProviderJobDetailResponse>> UploadCompletionProofAsync(Guid providerId, Guid bookingId, UploadJobCompletionProofRequest request)
    {
        var resolved = await ResolveAcceptedAsync(providerId, bookingId);
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

        return ToDetailResponse(assignment, booking);
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
        var resolved = await ResolveAcceptedAsync(providerId, bookingId);
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

    /// <summary>Same as <see cref="ResolveAsync"/> but only returns a result when the resolved assignment is this provider's currently <see cref="BookingProviderAssignmentStatus.Accepted"/> one - the precondition for start/complete/proof-upload.</summary>
    private async Task<(BookingProviderAssignment Assignment, Booking Booking)?> ResolveAcceptedAsync(Guid providerId, Guid bookingId)
    {
        var resolved = await ResolveAsync(providerId, bookingId);
        if (resolved is null || resolved.Value.Assignment.Status != BookingProviderAssignmentStatus.Accepted)
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
        BookingProviderAssignmentStatus.Accepted => booking.Status switch
        {
            BookingStatus.Completed => ProviderJobStatus.Completed,
            BookingStatus.InProgress => ProviderJobStatus.InProgress,
            _ => ProviderJobStatus.Accepted
        },
        _ => ProviderJobStatus.Assigned
    };

    private static ProviderJobDetailResponse ToDetailResponse(BookingProviderAssignment assignment, Booking booking) => new(
        assignment.Id,
        booking.Id,
        ToJobStatus(assignment, booking),
        booking.CustomerNameSnapshot,
        booking.CustomerMobileSnapshot,
        booking.AddressLabelSnapshot,
        booking.AddressLine1Snapshot,
        booking.AddressLine2Snapshot,
        booking.AddressLandmarkSnapshot,
        booking.AddressCitySnapshot,
        booking.AddressStateSnapshot,
        booking.AddressPincodeSnapshot,
        booking.AddressContactNameSnapshot,
        booking.AddressContactMobileSnapshot,
        booking.SlotDate,
        booking.SlotStartTimeSnapshot,
        booking.SlotEndTimeSnapshot,
        booking.Items.Select(i => new ProviderJobItemResponse(i.NameSnapshot, i.Quantity, i.UnitPriceSnapshot)).ToList(),
        booking.TotalPayableSnapshot,
        assignment.AssignedAt,
        assignment.RespondedAt,
        assignment.ResponseDeadline,
        assignment.Notes,
        assignment.CompletionProofRef);
}
