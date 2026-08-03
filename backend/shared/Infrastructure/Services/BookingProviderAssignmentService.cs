using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.ProviderManagement;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <inheritdoc cref="IBookingProviderAssignmentService"/>
public class BookingProviderAssignmentService : IBookingProviderAssignmentService
{
    private readonly IBookingRepository _bookingRepository;
    private readonly IProviderRepository _providerRepository;
    private readonly IBookingProviderAssignmentRepository _assignmentRepository;

    public BookingProviderAssignmentService(
        IBookingRepository bookingRepository,
        IProviderRepository providerRepository,
        IBookingProviderAssignmentRepository assignmentRepository)
    {
        _bookingRepository = bookingRepository;
        _providerRepository = providerRepository;
        _assignmentRepository = assignmentRepository;
    }

    public async Task<Result<BookingProviderAssignmentResponse>> AssignAsync(Guid bookingId, Guid adminUserId, AssignProviderRequest request)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null)
        {
            return Error.NotFound("BookingProviderAssignment.BookingNotFound", "Booking was not found.");
        }

        var provider = await _providerRepository.GetByIdAsync(request.ProviderId);
        if (provider is null)
        {
            return Error.NotFound("BookingProviderAssignment.ProviderNotFound", "Provider was not found.");
        }

        if (provider.Status != ProviderStatus.Active)
        {
            return Error.Business("BookingProviderAssignment.ProviderNotActive", "Only an active provider can be assigned to a booking.");
        }

        if (booking.Status != BookingStatus.AwaitingFulfilment && booking.Status != BookingStatus.Assigned)
        {
            return Error.Business(
                "BookingProviderAssignment.InvalidBookingStatus",
                $"A provider can only be assigned while the booking is AwaitingFulfilment or Assigned (current status: {booking.Status}).");
        }

        // Supersede whatever assignment is currently outstanding, if any -
        // PROVIDER.md OPEN DECISIONS #5: only one row is ever "live" per booking.
        var currentAssignment = await _assignmentRepository.GetActiveByBookingAsync(bookingId);
        if (currentAssignment is not null)
        {
            currentAssignment.MarkReassigned();
            await _assignmentRepository.UpdateAsync(currentAssignment);
        }

        var assignment = new BookingProviderAssignment(
            Guid.NewGuid(), bookingId, request.ProviderId, BookingAssignedByType.Admin, adminUserId, request.ResponseDeadline);
        await _assignmentRepository.AddAsync(assignment);

        if (booking.Status == BookingStatus.AwaitingFulfilment)
        {
            booking.TransitionTo(BookingStatus.Assigned, "Provider assigned by admin.");
        }

        booking.AssignProvider(request.ProviderId);
        await _bookingRepository.UpdateAsync(booking);

        return ToResponse(assignment, provider.DisplayName);
    }

    public async Task<Result<BookingProviderAssignmentResponse>> RejectAsync(Guid bookingId, RejectAssignmentRequest request)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null)
        {
            return Error.NotFound("BookingProviderAssignment.BookingNotFound", "Booking was not found.");
        }

        var assignment = await _assignmentRepository.GetActiveByBookingAsync(bookingId);
        if (assignment is null || assignment.Status != BookingProviderAssignmentStatus.Assigned)
        {
            return Error.Business("BookingProviderAssignment.NoOutstandingAssignment", "This booking has no outstanding assignment to reject.");
        }

        return await RejectInternalAsync(booking, assignment, request.Reason);
    }

    public async Task<Result<BookingProviderAssignmentResponse>> AcceptAsync(Guid bookingId, Guid providerId)
    {
        var assignment = await _assignmentRepository.GetActiveByBookingAsync(bookingId);
        if (assignment is null || assignment.ProviderId != providerId)
        {
            // Hides whether the booking/assignment exists at all from a
            // non-owning caller (SRS 28.3 IDOR), same pattern as
            // ProviderAvailabilityService.DeleteBlackoutDateAsync.
            return Error.NotFound("BookingProviderAssignment.NoOutstandingAssignment", "You have no outstanding assignment for this booking.");
        }

        if (assignment.Status != BookingProviderAssignmentStatus.Assigned)
        {
            return Error.Business("BookingProviderAssignment.AlreadyResponded", $"This assignment was already {assignment.Status}.");
        }

        assignment.Accept();
        await _assignmentRepository.UpdateAsync(assignment);

        var provider = await _providerRepository.GetByIdAsync(providerId);
        return ToResponse(assignment, provider?.DisplayName ?? "(unknown provider)");
    }

    public async Task<Result<BookingProviderAssignmentResponse>> RejectByProviderAsync(Guid bookingId, Guid providerId, RejectAssignmentRequest request)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null)
        {
            return Error.NotFound("BookingProviderAssignment.BookingNotFound", "Booking was not found.");
        }

        var assignment = await _assignmentRepository.GetActiveByBookingAsync(bookingId);
        if (assignment is null || assignment.ProviderId != providerId)
        {
            return Error.NotFound("BookingProviderAssignment.NoOutstandingAssignment", "You have no outstanding assignment for this booking.");
        }

        if (assignment.Status != BookingProviderAssignmentStatus.Assigned)
        {
            return Error.Business("BookingProviderAssignment.AlreadyResponded", $"This assignment was already {assignment.Status}.");
        }

        return await RejectInternalAsync(booking, assignment, request.Reason);
    }

    /// <summary>
    /// Shared task 159 handling behind both the admin-recorded
    /// <see cref="RejectAsync"/> and the provider-authenticated
    /// <see cref="RejectByProviderAsync"/>: reject the assignment, clear the
    /// display field, and return the booking to the assignable pool.
    /// </summary>
    private async Task<Result<BookingProviderAssignmentResponse>> RejectInternalAsync(Booking booking, BookingProviderAssignment assignment, string? reason)
    {
        assignment.Reject(reason);
        await _assignmentRepository.UpdateAsync(assignment);

        // Needs reassignment (task 159): clear the display field and return
        // the booking to the assignable pool. No auto-match - PROVIDER.md
        // OPEN DECISIONS #1 - an admin must call AssignAsync again.
        booking.AssignProvider(null);
        if (booking.Status == BookingStatus.Assigned)
        {
            booking.TransitionTo(BookingStatus.AwaitingFulfilment, "Provider rejected assignment; needs reassignment.");
        }

        await _bookingRepository.UpdateAsync(booking);

        var provider = await _providerRepository.GetByIdAsync(assignment.ProviderId);
        return ToResponse(assignment, provider?.DisplayName ?? "(unknown provider)");
    }

    public async Task<Result<IReadOnlyList<BookingProviderAssignmentResponse>>> GetHistoryAsync(Guid bookingId)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null)
        {
            return Error.NotFound("BookingProviderAssignment.BookingNotFound", "Booking was not found.");
        }

        var history = await _assignmentRepository.ListByBookingAsync(bookingId);
        var providerCache = new Dictionary<Guid, string>();
        var items = new List<BookingProviderAssignmentResponse>();
        foreach (var assignment in history)
        {
            if (!providerCache.TryGetValue(assignment.ProviderId, out var displayName))
            {
                var provider = await _providerRepository.GetByIdAsync(assignment.ProviderId);
                displayName = provider?.DisplayName ?? "(unknown provider)";
                providerCache[assignment.ProviderId] = displayName;
            }

            items.Add(ToResponse(assignment, displayName));
        }

        return items;
    }

    private static BookingProviderAssignmentResponse ToResponse(BookingProviderAssignment assignment, string providerDisplayName) => new(
        assignment.Id,
        assignment.BookingId,
        assignment.ProviderId,
        providerDisplayName,
        assignment.AssignedByType,
        assignment.AssignedByUserId,
        assignment.AssignedAt,
        assignment.Status,
        assignment.ResponseDeadline,
        assignment.RespondedAt,
        assignment.Notes,
        assignment.CompletionProofRef);
}
