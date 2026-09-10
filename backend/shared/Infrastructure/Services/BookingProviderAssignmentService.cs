using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.ProviderManagement;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;

namespace Nestly.Infrastructure.Services;

/// <inheritdoc cref="IBookingProviderAssignmentService"/>
/// <remarks>
/// Task 288 - how far the double-booking guard actually reaches, by provider:
///
/// <list type="bullet">
/// <item>Every provider: <see cref="IProviderScheduleConflictService"/> is
/// consulted inside a Serializable transaction on both assignment paths, so
/// the check and the write it justifies cannot be split by a concurrent
/// assignment on the same connection.</item>
/// <item>PostgreSQL (runtime): additionally backed by the
/// <c>ex_booking_provider_no_double_booking</c> EXCLUDE USING gist constraint
/// added by the <c>AddProviderNoDoubleBooking</c> migration. Serializable
/// Snapshot Isolation aborts one side of two concurrent assignments that both
/// read "no conflict"; the exclusion constraint is the belt-and-braces
/// backstop that also catches any writer that bypasses this service entirely.</item>
/// <item>SQLite (Catalog.Tests' <c>TestDatabase</c>): the schema is built by
/// <c>EnsureCreated</c> from the EF model, which never runs migrations, and
/// SQLite has no exclusion constraints at all - so the database-level
/// backstop simply does not exist there and the tests exercise the
/// application-level check only. This is the same runtime/test provider
/// divergence task 252 recorded for negative OFFSET, documented rather than
/// papered over: a green test suite is not evidence that the Postgres
/// constraint is correct, only that the service-level rule is.</item>
/// </list>
/// </remarks>
public class BookingProviderAssignmentService : IBookingProviderAssignmentService
{
    private readonly IBookingRepository _bookingRepository;
    private readonly IProviderRepository _providerRepository;
    private readonly IServiceRepository _serviceRepository;
    private readonly IBookingProviderAssignmentRepository _assignmentRepository;
    private readonly IProviderScheduleConflictService _scheduleConflictService;
    private readonly IOptions<AutoAssignmentOptions> _autoAssignmentOptions;
    // Matching candidates for GetEligibleProvidersAsync spans Provider,
    // ProviderServiceArea, ProviderSkillMapping, ProviderCapacity and Pincode
    // in one read - no single existing repository owns that join, so this
    // reads directly off the shared context, same as RefundService/
    // PaymentWebhookService do for their own cross-aggregate needs.
    private readonly NestlyDbContext _context;

    public BookingProviderAssignmentService(
        IBookingRepository bookingRepository,
        IProviderRepository providerRepository,
        IServiceRepository serviceRepository,
        IBookingProviderAssignmentRepository assignmentRepository,
        IProviderScheduleConflictService scheduleConflictService,
        IOptions<AutoAssignmentOptions> autoAssignmentOptions,
        NestlyDbContext context)
    {
        _bookingRepository = bookingRepository;
        _providerRepository = providerRepository;
        _serviceRepository = serviceRepository;
        _assignmentRepository = assignmentRepository;
        _scheduleConflictService = scheduleConflictService;
        _autoAssignmentOptions = autoAssignmentOptions;
        _context = context;
    }

    public Task<Result<BookingProviderAssignmentResponse>> AssignAsync(Guid bookingId, Guid adminUserId, AssignProviderRequest request) =>
        AssignInternalAsync(
            bookingId, request.ProviderId, BookingAssignedByType.Admin, adminUserId, request.ResponseDeadline, "Provider assigned by admin.");

    /// <summary>
    /// Phase 14 (task 246): the automatic-assignment engine's counterpart to
    /// <see cref="AssignAsync"/> - same validation and PROVIDER.md OPEN
    /// DECISIONS #5 supersede-the-outstanding-row behaviour, but records
    /// <see cref="BookingAssignedByType.System"/> with no acting admin id,
    /// exactly what that column has existed to support since task 147
    /// (PROVIDER.md OPEN DECISIONS #1: "the bridge table's assigned_by
    /// column already distinguishes admin from system, so an automatic
    /// assignment engine can be added later purely as a new writer of that
    /// same table" - this is that later writer). The response deadline is
    /// <see cref="AutoAssignmentOptions.ResponseWindowMinutes"/> from now - the
    /// assignment-response-expiry sweep is what actually moves on to the next
    /// candidate if that passes with no answer; the rejection-retry chain
    /// handles the explicit-decline case identically either way.
    /// </summary>
    public Task<Result<BookingProviderAssignmentResponse>> AssignBySystemAsync(Guid bookingId, Guid providerId) =>
        AssignInternalAsync(
            bookingId, providerId, BookingAssignedByType.System, adminUserId: null,
            responseDeadline: DateTime.UtcNow.AddMinutes(_autoAssignmentOptions.Value.ResponseWindowMinutes),
            "Provider auto-assigned by the matching engine.");

    /// <summary>
    /// Row 33, docs/OPEN-FIXES-FEATURES.csv: an admin can manually push a
    /// paid Confirmed booking straight to a provider instead of waiting for
    /// auto-assignment (which only runs as the slot approaches) - the panel
    /// used to flatly refuse until AwaitingFulfilment. Confirmed is
    /// deliberately allowed only for <see cref="BookingAssignedByType.Admin"/>:
    /// the automatic engine's own gate is unchanged, so this does not alter
    /// when/what auto-assignment picks up.
    /// </summary>
    private static bool IsAssignableStatus(BookingStatus status, BookingAssignedByType assignedByType) =>
        status is BookingStatus.AwaitingFulfilment or BookingStatus.Assigned
        || (assignedByType == BookingAssignedByType.Admin && status == BookingStatus.Confirmed);

    private async Task<Result<BookingProviderAssignmentResponse>> AssignInternalAsync(
        Guid bookingId, Guid providerId, BookingAssignedByType assignedByType, Guid? adminUserId, DateTime? responseDeadline, string reason)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null)
        {
            return Error.NotFound("BookingProviderAssignment.BookingNotFound", "Booking was not found.");
        }

        var provider = await _providerRepository.GetByIdAsync(providerId);
        if (provider is null)
        {
            return Error.NotFound("BookingProviderAssignment.ProviderNotFound", "Provider was not found.");
        }

        if (provider.Status != ProviderStatus.Active)
        {
            return Error.Business("BookingProviderAssignment.ProviderNotActive", "Only an active provider can be assigned to a booking.");
        }

        if (!IsAssignableStatus(booking.Status, assignedByType))
        {
            return Error.Business(
                "BookingProviderAssignment.InvalidBookingStatus",
                assignedByType == BookingAssignedByType.Admin
                    ? $"A provider can only be assigned while the booking is Confirmed, AwaitingFulfilment or Assigned (current status: {booking.Status})."
                    : $"A provider can only be assigned while the booking is AwaitingFulfilment or Assigned (current status: {booking.Status}).");
        }

        // Task 288: the read this decision depends on and the writes that act
        // on it are one unit of work. Serializable so that two assignments
        // racing for the same provider and overlapping slots cannot both read
        // "no conflict" and both commit - Postgres aborts one of them as a
        // write skew, the same reasoning behind WalletService.DebitAsync's
        // append-only balance check. See this class's doc comment for what
        // this does and does not guarantee per database provider.
        await using var dbTransaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var conflict = await _scheduleConflictService.FindConflictAsync(providerId, booking);
        if (conflict is not null)
        {
            return DoubleBookedError(conflict);
        }

        try
        {
            // Supersede whatever assignment is currently outstanding, if any -
            // PROVIDER.md OPEN DECISIONS #5: only one row is ever "live" per booking.
            var currentAssignment = await _assignmentRepository.GetActiveByBookingAsync(bookingId);
            if (currentAssignment is not null)
            {
                currentAssignment.MarkReassigned(providerId);
                await _assignmentRepository.UpdateAsync(currentAssignment);
            }

            var assignment = new BookingProviderAssignment(
                Guid.NewGuid(), bookingId, providerId, assignedByType, adminUserId, responseDeadline);
            await _assignmentRepository.AddAsync(assignment);

            // A booking that is already Assigned stays Assigned - swapping the
            // provider is not a lifecycle transition. That used to make the
            // swap invisible to everything downstream (task 295): no
            // BookingStatusChangedEvent meant no notification, so a customer
            // who had been told who was coming was never told it had changed.
            // The signal now comes from MarkReassigned's
            // BookingProviderChangedEvent above, which fires on both branches
            // of this if and does not depend on a status change.
            //
            // Row 33, docs/OPEN-FIXES-FEATURES.csv: a paid Confirmed booking
            // manually assigned by admin has no direct Confirmed->Assigned
            // edge in BookingLifecycle (only Confirmed->AwaitingFulfilment
            // does), so it is walked through AwaitingFulfilment first rather
            // than adding a lifecycle shortcut just for this caller.
            if (booking.Status == BookingStatus.Confirmed)
            {
                booking.TransitionTo(BookingStatus.AwaitingFulfilment, "Released to fulfilment by admin assignment.");
                booking.TransitionTo(BookingStatus.Assigned, reason);
            }
            else if (booking.Status == BookingStatus.AwaitingFulfilment)
            {
                booking.TransitionTo(BookingStatus.Assigned, reason);
            }

            booking.AssignProvider(providerId);
            await _bookingRepository.UpdateAsync(booking);

            await dbTransaction.CommitAsync();

            return ToResponse(assignment, provider.DisplayName, provider.OnboardingStatus);
        }
        catch (DbUpdateException)
        {
            // Either Postgres' ex_booking_provider_no_double_booking exclusion
            // constraint rejected the write (see the AddProviderNoDoubleBooking
            // migration) or the serializable transaction lost a race - both
            // mean a competing assignment committed between the check above
            // and this write. Detach so the failed rows can't be resubmitted
            // by a later SaveChangesAsync on this same scoped context (same
            // precaution as SlotCapacityRepository.TryReserveAsync), then
            // re-read and report the real reason rather than a 500.
            await dbTransaction.RollbackAsync();
            DetachPendingAssignmentWrites();

            var racedConflict = await _scheduleConflictService.FindConflictAsync(providerId, booking);
            if (racedConflict is null)
            {
                throw;
            }

            return DoubleBookedError(racedConflict);
        }
    }

    /// <summary>
    /// Task 288. Names the booking that was collided with: an admin who is
    /// stopped from assigning needs to know which other job the provider is
    /// already on, not just that "something" clashed. The automatic engine
    /// gets the same failure and simply moves on to its next candidate
    /// (<c>ProviderAutoAssignmentHandler.TryAssignAsync</c>), so one rule
    /// covers both paths.
    /// </summary>
    private static Error DoubleBookedError(ProviderScheduleConflict conflict) => Error.Conflict(
        "BookingProviderAssignment.ProviderDoubleBooked",
        $"This provider is already assigned to booking {conflict.BookingId} on {conflict.SlotDate:yyyy-MM-dd} "
        + $"from {conflict.StartTime:hh\\:mm} to {conflict.EndTime:hh\\:mm}, which overlaps this booking's slot.");

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

        // Row 38, docs/OPEN-FIXES-FEATURES.csv: idempotent for a provider
        // retrying their own accept - if the first call already succeeded
        // server-side but the response never reached the client (a dropped
        // connection, a background/foreground app switch), a naive retry
        // would previously fail with AlreadyResponded and read as "you lost
        // the job" even though the provider is still assigned. Only the same
        // provider's own already-Accepted state is treated as success here -
        // Rejected/Reassigned/Withdrawn still fail below exactly as before,
        // since those are real terminal outcomes, not a delivery failure.
        if (assignment.Status == BookingProviderAssignmentStatus.Accepted)
        {
            var alreadyAcceptedProvider = await _providerRepository.GetByIdAsync(providerId);
            return ToResponse(
                assignment, alreadyAcceptedProvider?.DisplayName ?? "(unknown provider)",
                alreadyAcceptedProvider?.OnboardingStatus ?? ProviderOnboardingStatus.Registered);
        }

        if (assignment.Status != BookingProviderAssignmentStatus.Assigned)
        {
            return Error.Business("BookingProviderAssignment.AlreadyResponded", $"This assignment was already {assignment.Status}.");
        }

        assignment.Accept();
        await _assignmentRepository.UpdateAsync(assignment);

        var provider = await _providerRepository.GetByIdAsync(providerId);
        return ToResponse(assignment, provider?.DisplayName ?? "(unknown provider)", provider?.OnboardingStatus ?? ProviderOnboardingStatus.Registered);
    }

    /// <inheritdoc />
    public async Task<Result<BookingProviderAssignmentResponse>> ExtendResponseDeadlineAsync(Guid bookingId, Guid providerId)
    {
        var assignment = await _assignmentRepository.GetActiveByBookingAsync(bookingId);
        if (assignment is null || assignment.ProviderId != providerId)
        {
            // Same IDOR-hiding pattern as AcceptAsync/RejectByProviderAsync.
            return Error.NotFound("BookingProviderAssignment.NoOutstandingAssignment", "You have no outstanding assignment for this booking.");
        }

        if (assignment.Status != BookingProviderAssignmentStatus.Assigned)
        {
            return Error.Business("BookingProviderAssignment.AlreadyResponded", $"This assignment was already {assignment.Status}.");
        }

        assignment.ExtendResponseDeadline(TimeSpan.FromMinutes(_autoAssignmentOptions.Value.ResponseWindowMinutes));
        await _assignmentRepository.UpdateAsync(assignment);

        var provider = await _providerRepository.GetByIdAsync(providerId);
        return ToResponse(assignment, provider?.DisplayName ?? "(unknown provider)", provider?.OnboardingStatus ?? ProviderOnboardingStatus.Registered);
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

        await ReturnToAssignablePoolAsync(booking, "Provider rejected assignment; needs reassignment.");

        var provider = await _providerRepository.GetByIdAsync(assignment.ProviderId);
        return ToResponse(assignment, provider?.DisplayName ?? "(unknown provider)", provider?.OnboardingStatus ?? ProviderOnboardingStatus.Registered);
    }

    /// <summary>
    /// Shared by <see cref="RejectInternalAsync"/> and <see cref="ExpireAsync"/>:
    /// clears the display field and returns the booking to the assignable
    /// pool. This used to mean a purely manual admin queue (PROVIDER.md OPEN
    /// DECISIONS #1), but task 247's <c>ProviderAutoAssignmentHandler</c> now
    /// reacts to the AwaitingFulfilment transition raised below identically
    /// to a fresh booking - see that class's doc comment for the
    /// retry-cap/exclusion/standing-provider behaviour. Manual admin
    /// assignment remains the fallback, not the only path: it only takes over
    /// once auto-assignment is disabled (<see cref="AutoAssignmentOptions.Enabled"/>),
    /// exhausts its retry cap, or finds no eligible candidate.
    /// </summary>
    private async Task ReturnToAssignablePoolAsync(Booking booking, string reason)
    {
        booking.AssignProvider(null);
        if (booking.Status == BookingStatus.Assigned)
        {
            booking.TransitionTo(BookingStatus.AwaitingFulfilment, reason);
        }

        await _bookingRepository.UpdateAsync(booking);
    }

    public async Task<Result<BookingProviderAssignmentResponse>> ExpireAsync(Guid assignmentId)
    {
        var assignment = await _assignmentRepository.GetByIdAsync(assignmentId);
        if (assignment is null)
        {
            return Error.NotFound("BookingProviderAssignment.NotFound", "The specified assignment does not exist.");
        }

        // The provider may have responded (or an admin/another writer may
        // have superseded this row) in the gap between the sweep reading it
        // as stale and this call actually running - not an error, just
        // nothing left for the sweep to do here.
        if (assignment.Status != BookingProviderAssignmentStatus.Assigned)
        {
            var current = await _providerRepository.GetByIdAsync(assignment.ProviderId);
            return ToResponse(assignment, current?.DisplayName ?? "(unknown provider)", current?.OnboardingStatus ?? ProviderOnboardingStatus.Registered);
        }

        var booking = await _bookingRepository.GetByIdAsync(assignment.BookingId);
        if (booking is null)
        {
            return Error.NotFound("BookingProviderAssignment.BookingNotFound", "Booking was not found.");
        }

        assignment.Expire();
        await _assignmentRepository.UpdateAsync(assignment);

        await ReturnToAssignablePoolAsync(booking, "Provider did not respond within the response window; needs reassignment.");

        var provider = await _providerRepository.GetByIdAsync(assignment.ProviderId);
        return ToResponse(assignment, provider?.DisplayName ?? "(unknown provider)", provider?.OnboardingStatus ?? ProviderOnboardingStatus.Registered);
    }

    public async Task<Result<IReadOnlyList<BookingProviderAssignmentResponse>>> GetHistoryAsync(Guid bookingId)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null)
        {
            return Error.NotFound("BookingProviderAssignment.BookingNotFound", "Booking was not found.");
        }

        var history = await _assignmentRepository.ListByBookingAsync(bookingId);

        var distinctProviderIds = history.Select(a => a.ProviderId).Distinct().ToList();

        // Task 257: the local dictionary only saved a repeat lookup for a
        // provider already seen in this history - the first assignment for
        // each distinct provider still cost its own round trip.
        var displayNames = await _providerRepository.GetDisplayNamesByIdsAsync(distinctProviderIds);
        var onboardingStatuses = await _providerRepository.GetOnboardingStatusesByIdsAsync(distinctProviderIds);

        return history
            .Select(assignment => ToResponse(
                assignment,
                displayNames.GetValueOrDefault(assignment.ProviderId, "(unknown provider)"),
                onboardingStatuses.GetValueOrDefault(assignment.ProviderId, ProviderOnboardingStatus.Registered)))
            .ToList();
    }

    public async Task<Result<IReadOnlyList<EligibleProviderResponse>>> GetEligibleProvidersAsync(Guid bookingId)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null)
        {
            return Error.NotFound("BookingProviderAssignment.BookingNotFound", "Booking was not found.");
        }

        if (booking.Items.Count == 0)
        {
            return Error.Business("BookingProviderAssignment.NoServiceOnBooking", "This booking has no service line item to match providers against.");
        }

        var serviceId = booking.Items[0].ServiceId;
        var service = await _serviceRepository.GetByIdAsync(serviceId);
        if (service is null)
        {
            return Error.NotFound("BookingProviderAssignment.ServiceNotFound", "The booking's service could not be found.");
        }

        // The booking only ever stores the pincode as a snapshot string (see
        // Booking's doc comment on why every address field is a snapshot,
        // not a live FK) - resolve it back to a real Pincode row so it can be
        // compared against ProviderServiceArea's actual foreign keys.
        var pincode = await _context.Set<Pincode>()
            .FirstOrDefaultAsync(p => p.Code == booking.AddressPincodeSnapshot);
        if (pincode is null)
        {
            return Error.NotFound(
                "BookingProviderAssignment.PincodeNotFound",
                "The booking's address pincode is not a recognised serviceable pincode.");
        }

        // A provider matches if their declared service area covers this
        // pincode specifically (PincodeId set) or the whole city
        // (PincodeId null), and their declared skill covers this service
        // specifically (ServiceId set) or the whole category (ServiceId
        // null) - the same "narrows an optional broader default" shape
        // ProviderServiceArea/ProviderSkillMapping's own doc comments
        // describe, not a distance calculation (this schema has no provider
        // coordinates - see PROVIDER.md OPEN DECISIONS #1's "distance...
        // doesn't exist yet").
        var candidateRows = await (
            from provider in _context.Set<Provider>()
            where provider.Status == ProviderStatus.Active
            join area in _context.Set<ProviderServiceArea>() on provider.Id equals area.ProviderId
            join skill in _context.Set<ProviderSkillMapping>() on provider.Id equals skill.ProviderId
            where area.IsActive && area.CityId == pincode.CityId
                && (area.PincodeId == null || area.PincodeId == pincode.Id)
            where skill.IsActive && skill.CategoryId == service.CategoryId
                && (skill.ServiceId == null || skill.ServiceId == serviceId)
            select new
            {
                ProviderId = provider.Id,
                provider.DisplayName,
                provider.Phone,
                PincodeMatch = area.PincodeId != null,
                ServiceMatch = skill.ServiceId != null,
            }
        ).ToListAsync();

        if (candidateRows.Count == 0)
        {
            return Array.Empty<EligibleProviderResponse>();
        }

        // A provider can satisfy the join above through more than one
        // area/skill row (e.g. both a city-wide and a pincode-specific
        // area) - collapse to one row per provider, keeping the most
        // specific match found on either dimension.
        var bestPerProvider = candidateRows
            .GroupBy(x => x.ProviderId)
            .Select(g => new
            {
                ProviderId = g.Key,
                DisplayName = g.First().DisplayName,
                Phone = g.First().Phone,
                PincodeMatch = g.Any(x => x.PincodeMatch),
                ServiceMatch = g.Any(x => x.ServiceMatch),
            })
            .ToList();

        var providerIds = bestPerProvider.Select(x => x.ProviderId).ToList();

        var capacityByProvider = await _context.Set<ProviderCapacity>()
            .Where(c => providerIds.Contains(c.ProviderId))
            .ToDictionaryAsync(c => c.ProviderId, c => c.MaxJobsPerDay);

        // Advisory load signal only (PROVIDER.md OPEN DECISIONS - AUTOMATIC
        // ASSIGNMENT #2 - manual admin assignment does not hard-enforce
        // ProviderCapacity, only the automatic engine does) - counts this
        // provider's other live jobs on the same slot date, not a hard filter.
        var jobsTodayByProvider = await _context.Set<Booking>()
            .Where(b => b.AssignedProviderId != null
                && providerIds.Contains(b.AssignedProviderId!.Value)
                && b.SlotDate == booking.SlotDate
                && (b.Status == BookingStatus.Assigned || b.Status == BookingStatus.InProgress))
            .GroupBy(b => b.AssignedProviderId!.Value)
            .Select(g => new { ProviderId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ProviderId, x => x.Count);

        var results = bestPerProvider
            .Select(x => new EligibleProviderResponse(
                x.ProviderId,
                x.DisplayName,
                x.Phone,
                x.PincodeMatch,
                x.ServiceMatch,
                capacityByProvider.GetValueOrDefault(x.ProviderId),
                jobsTodayByProvider.GetValueOrDefault(x.ProviderId)))
            // Most specific match first (exact pincode, exact service), then
            // least-loaded today - never by rating (OPEN DECISIONS #4).
            .OrderByDescending(r => r.PincodeMatch)
            .ThenByDescending(r => r.ServiceMatch)
            .ThenBy(r => r.AssignedJobsToday)
            .ToList();

        return results;
    }

    private static BookingProviderAssignmentResponse ToResponse(
        BookingProviderAssignment assignment,
        string providerDisplayName,
        ProviderOnboardingStatus providerOnboardingStatus) => new(
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
        assignment.CompletionProofRef,
        providerOnboardingStatus);
}
