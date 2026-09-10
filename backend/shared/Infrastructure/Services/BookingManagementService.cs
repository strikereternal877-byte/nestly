using System.Text.Json;
using Nestly.Application;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.BookingManagement;
using Nestly.Application.Bookings;
using Nestly.Application.Cancellations;
using Nestly.Application.Payments;
using Nestly.Application.Refunds;
using Nestly.Application.Reschedules;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Admin booking management (SRS 12.11, 12.13.2-3; tasks 115a-117c). Every
/// cancellation/reschedule/refund goes through the existing domain services
/// (<see cref="ICancellationService"/>, <see cref="IRescheduleService"/>,
/// <see cref="IRefundService"/> - tasks 80c, 82d, 75d), so their fee/
/// eligibility policy math is never duplicated here; this service is
/// responsible only for the admin-facing shape, the generic status
/// transition, and the audit trail (<see cref="IAuditLogWriter"/>).
/// </summary>
/// <remarks>
/// Task 132c gap fix: every action here stages its audit row only after the
/// underlying domain service (<see cref="ICancellationService"/>,
/// <see cref="IRescheduleService"/>, <see cref="IRefundService"/>, or this
/// class's own <see cref="IBookingRepository.UpdateAsync"/> call for
/// <see cref="UpdateStatusAsync"/>) has already committed its own
/// <c>SaveChangesAsync</c> - so without an explicit save afterward, the
/// staged <see cref="AuditLog"/> row was silently dropped once the
/// request-scoped <see cref="NestlyDbContext"/> was disposed, despite this
/// class's own doc comment (and SRS 12.13.2/117c) promising every one of
/// these actions is audited. <see cref="_dbContext"/> exists solely to
/// commit that staged row, the same way <c>AdminUserManagementService</c>
/// and <c>PermissionAuthorizationHandler</c> already do for their own
/// audit-only writes.
/// </remarks>
public class BookingManagementService : IBookingManagementService
{
    /// <summary>
    /// Target statuses that must go through their dedicated action instead of
    /// the generic <see cref="UpdateStatusAsync"/> - see
    /// <see cref="AdminBookingStatusUpdateRequest"/>'s doc comment.
    /// </summary>
    private static readonly IReadOnlyCollection<BookingStatus> DisallowedGenericTransitionTargets = new HashSet<BookingStatus>
    {
        BookingStatus.CancelledByCustomer,
        BookingStatus.CancelledByAdmin,
        BookingStatus.Rescheduled,
        BookingStatus.RefundPending,
        BookingStatus.Refunded,
    };

    /// <summary>
    /// Every status that presupposes a provider is actually on the job.
    /// <see cref="BookingLifecycle.IsValidTransition"/> only checks the
    /// state-machine shape - it has no idea whether a real provider exists -
    /// and <see cref="Booking.TransitionTo"/> deliberately never touches
    /// <see cref="Booking.AssignedProviderId"/> (see that method's own doc
    /// comment). Left unguarded, this generic endpoint could move a booking
    /// straight to "Professional Assigned" (and from there to InProgress,
    /// even Completed) with no <c>BookingProviderAssignment</c> row and no
    /// provider anywhere in the city - a real gap, not a hypothetical one,
    /// since <c>Assigned</c> was never in <see cref="DisallowedGenericTransitionTargets"/>
    /// and admin-web's generic status dropdown lists it right alongside every
    /// other status. Reaching any of these must go through
    /// <c>IBookingProviderAssignmentService.AssignAsync</c> first, which is
    /// the one place that actually requires and records a real provider.
    /// </summary>
    private static readonly IReadOnlyCollection<BookingStatus> RequiresAssignedProviderTargets = new HashSet<BookingStatus>
    {
        BookingStatus.Assigned,
        BookingStatus.ProviderEnRoute,
        BookingStatus.ProviderArrived,
        BookingStatus.InProgress,
    };

    private readonly IBookingRepository _bookingRepository;
    private readonly IPaymentTransactionRepository _paymentRepository;
    private readonly ICancellationRepository _cancellationRepository;
    private readonly IRescheduleRepository _rescheduleRepository;
    private readonly IRefundTransactionRepository _refundRepository;
    private readonly ICancellationService _cancellationService;
    private readonly IRescheduleService _rescheduleService;
    private readonly IRefundService _refundService;
    private readonly IPaymentWebhookService _paymentWebhookService;
    private readonly IAuditLogWriter _auditLogWriter;
    private readonly NestlyDbContext _dbContext;
    private readonly IBookingCompletionProofRepository _completionProofRepository;
    private readonly IProviderRepository _providerRepository;

    public BookingManagementService(
        IBookingRepository bookingRepository,
        IPaymentTransactionRepository paymentRepository,
        ICancellationRepository cancellationRepository,
        IRescheduleRepository rescheduleRepository,
        IRefundTransactionRepository refundRepository,
        ICancellationService cancellationService,
        IRescheduleService rescheduleService,
        IRefundService refundService,
        IPaymentWebhookService paymentWebhookService,
        IAuditLogWriter auditLogWriter,
        NestlyDbContext dbContext,
        IBookingCompletionProofRepository completionProofRepository,
        IProviderRepository providerRepository)
    {
        _bookingRepository = bookingRepository;
        _paymentRepository = paymentRepository;
        _cancellationRepository = cancellationRepository;
        _rescheduleRepository = rescheduleRepository;
        _refundRepository = refundRepository;
        _cancellationService = cancellationService;
        _rescheduleService = rescheduleService;
        _refundService = refundService;
        _paymentWebhookService = paymentWebhookService;
        _auditLogWriter = auditLogWriter;
        _dbContext = dbContext;
        _completionProofRepository = completionProofRepository;
        _providerRepository = providerRepository;
    }

    public async Task<Result<AdminBookingSearchResponse>> SearchAsync(AdminBookingSearchRequest request)
    {
        var filter = new BookingSearchFilter(
            request.BookingId,
            request.CustomerName,
            request.CustomerMobile,
            request.Status,
            request.City,
            request.SlotDateFrom,
            request.SlotDateTo,
            request.CreatedFromUtc,
            request.CreatedToUtc,
            request.ServiceId,
            request.CategoryId,
            request.CouponCode,
            request.Page,
            request.PageSize,
            request.Reference);

        var result = await _bookingRepository.SearchAsync(filter);

        var items = result.Rows.Select(ToListItem).ToList();
        return new AdminBookingSearchResponse(items, result.TotalCount, request.Page, request.PageSize);
    }

    public async Task<Result<AdminBookingDetailResponse>> GetDetailAsync(Guid bookingId)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null)
        {
            return Error.NotFound("Booking.NotFound", "The specified booking does not exist.");
        }

        return await BuildDetailAsync(booking);
    }

    public async Task<Result<AdminBookingDetailResponse>> UpdateStatusAsync(Guid bookingId, Guid adminUserId, AdminBookingStatusUpdateRequest request)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null)
        {
            return Error.NotFound("Booking.NotFound", "The specified booking does not exist.");
        }

        if (DisallowedGenericTransitionTargets.Contains(request.NewStatus))
        {
            return Error.Validation(
                "Booking.UseDedicatedAction",
                $"'{request.NewStatus}' must be set via the dedicated cancel/reschedule/refund action, not a generic status update.");
        }

        if (!BookingLifecycle.IsValidTransition(booking.Status, request.NewStatus))
        {
            return Error.Business(
                "Booking.InvalidTransition",
                $"A booking in status '{booking.Status}' cannot be moved to '{request.NewStatus}'.");
        }

        if (RequiresAssignedProviderTargets.Contains(request.NewStatus) && booking.AssignedProviderId is null)
        {
            return Error.Business(
                "Booking.NoProviderAssigned",
                $"'{request.NewStatus}' requires a provider already assigned to this booking. Use the assign-provider action first.");
        }

        if (request.NewStatus == BookingStatus.Completed)
        {
            var proofError = await _completionProofRepository.EnsureCompletionProofExistsAsync(bookingId);
            if (proofError is not null)
            {
                return proofError;
            }
        }

        var previousStatus = booking.Status;
        booking.TransitionTo(request.NewStatus, request.Reason);
        await _bookingRepository.UpdateAsync(booking);

        await _auditLogWriter.WriteAsync(new AuditEntry(
            "Booking", bookingId.ToString(), "AdminStatusUpdate",
            JsonSerializer.Serialize(new { Status = previousStatus.ToString() }),
            JsonSerializer.Serialize(new { Status = request.NewStatus.ToString(), request.Reason })));
        await _dbContext.SaveChangesAsync();

        return await BuildDetailAsync(booking);
    }

    public async Task<Result<AdminBookingDetailResponse>> CancelAsync(Guid bookingId, Guid adminUserId, AdminCancelBookingRequest request)
    {
        var outcome = await _cancellationService.AdminCancelAsync(bookingId, request.Reason, request.InternalNotes);
        if (outcome.IsFailure)
        {
            return outcome.Error;
        }

        await _auditLogWriter.WriteAsync(new AuditEntry(
            "Booking", bookingId.ToString(), "AdminCancel",
            null,
            JsonSerializer.Serialize(new
            {
                outcome.Value.BookingStatus,
                outcome.Value.CancellationFeeAmount,
                outcome.Value.RefundAmount,
                Reason = request.Reason
            })));
        await _dbContext.SaveChangesAsync();

        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        return booking is null
            ? Error.NotFound("Booking.NotFound", "The specified booking does not exist.")
            : await BuildDetailAsync(booking);
    }

    public async Task<Result<AdminBookingDetailResponse>> RescheduleAsync(Guid bookingId, Guid adminUserId, AdminRescheduleBookingRequest request)
    {
        var rescheduleRequest = new RescheduleBookingRequest(request.LocalityId, request.SlotWindowId, request.SlotDate, request.Reason);
        var outcome = await _rescheduleService.AdminRescheduleAsync(bookingId, rescheduleRequest);
        if (outcome.IsFailure)
        {
            return outcome.Error;
        }

        await _auditLogWriter.WriteAsync(new AuditEntry(
            "Booking", bookingId.ToString(), "AdminReschedule",
            JsonSerializer.Serialize(outcome.Value.PreviousSlot),
            JsonSerializer.Serialize(outcome.Value.NewSlot)));
        await _dbContext.SaveChangesAsync();

        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        return booking is null
            ? Error.NotFound("Booking.NotFound", "The specified booking does not exist.")
            : await BuildDetailAsync(booking);
    }

    public async Task<Result<AdminBookingDetailResponse>> RefundAsync(Guid bookingId, Guid adminUserId, AdminRefundRequest request)
    {
        var refundResult = request.IsFullRefund
            ? await _refundService.InitiateFullRefundAsync(bookingId, request.Reason, request.Method)
            : await _refundService.InitiatePartialRefundAsync(bookingId, request.Amount!.Value, request.Reason, request.Method);

        if (refundResult.IsFailure)
        {
            return refundResult.Error;
        }

        // Every refund action is audited regardless of amount (SRS 12.13.2,
        // task 117c) - full and partial share one action name suffixed by
        // type so the audit-log-viewer's substring "Action" filter can still
        // isolate either.
        await _auditLogWriter.WriteAsync(new AuditEntry(
            "Booking", bookingId.ToString(), request.IsFullRefund ? "AdminRefundFull" : "AdminRefundPartial",
            null,
            JsonSerializer.Serialize(new
            {
                refundResult.Value.Primary.Id,
                Amount = refundResult.Value.TotalAmount,
                refundResult.Value.Primary.Method,
                refundResult.Value.Primary.Status,
                // Task 356: a booking funded from both the gateway and the
                // customer's wallet settles as one row per funding source, so
                // the audited amount is the total across them and the split
                // is spelled out rather than left to be inferred from Id.
                Settlements = refundResult.Value.Settlements
                    .Select(s => new { s.Id, s.FundingSource, s.Method, s.Amount })
                    .ToList(),
                Reason = request.Reason
            })));
        await _dbContext.SaveChangesAsync();

        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        return booking is null
            ? Error.NotFound("Booking.NotFound", "The specified booking does not exist.")
            : await BuildDetailAsync(booking);
    }

    public async Task<Result<AdminBookingDetailResponse>> RecordManualPaymentAsync(Guid bookingId, Guid adminUserId, AdminManualPaymentRequest request)
    {
        var manualPaymentResult = await _paymentWebhookService.RecordManualPaymentAsync(bookingId, request.Method, request.Reference);
        if (manualPaymentResult.IsFailure)
        {
            return manualPaymentResult.Error;
        }

        // Same audited-after-the-domain-service-commits pattern every other
        // action in this class follows (see this class's own doc comment) -
        // RecordManualPaymentAsync already committed its own DB transaction.
        await _auditLogWriter.WriteAsync(new AuditEntry(
            "Booking", bookingId.ToString(), "AdminManualPayment",
            null,
            JsonSerializer.Serialize(new
            {
                manualPaymentResult.Value.Id,
                Amount = manualPaymentResult.Value.Amount,
                request.Method,
                request.Reference
            })));
        await _dbContext.SaveChangesAsync();

        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        return booking is null
            ? Error.NotFound("Booking.NotFound", "The specified booking does not exist.")
            : await BuildDetailAsync(booking);
    }

    private async Task<AdminBookingDetailResponse> BuildDetailAsync(Booking booking)
    {
        var payment = await _paymentRepository.GetByBookingIdAsync(booking.Id);
        var cancellation = await _cancellationRepository.GetByBookingIdAsync(booking.Id);
        var reschedules = await _rescheduleRepository.ListByBookingAsync(booking.Id);
        var refunds = await _refundRepository.ListByBookingAsync(booking.Id);

        return new AdminBookingDetailResponse(
            booking.Id,
            new AdminBookingCustomerSnapshot(booking.CustomerId, booking.CustomerNameSnapshot, booking.CustomerMobileSnapshot),
            new AdminBookingAddressSnapshot(
                booking.AddressLabelSnapshot, booking.AddressLine1Snapshot, booking.AddressLine2Snapshot, booking.AddressLandmarkSnapshot,
                booking.AddressPincodeSnapshot, booking.AddressCitySnapshot, booking.AddressStateSnapshot,
                booking.AddressContactNameSnapshot, booking.AddressContactMobileSnapshot),
            new AdminBookingSlotSnapshot(booking.SlotWindowId, booking.SlotDate, booking.SlotWindowNameSnapshot, booking.SlotStartTimeSnapshot, booking.SlotEndTimeSnapshot),
            booking.Items.Select(i => new AdminBookingItemResponse(
                i.Id, i.ServiceId, i.NameSnapshot, i.UnitPriceSnapshot, i.Quantity, i.LineTotalSnapshot,
                i.AddOns.Select(a => new AdminBookingAddOnResponse(a.Id, a.ServiceAddOnId, a.NameSnapshot, a.UnitPriceSnapshot, a.Quantity, a.LineTotalSnapshot)).ToList())).ToList(),
            new AdminBookingPriceResponse(
                booking.BasePriceSnapshot, booking.QuantitySnapshot, booking.BaseTotalSnapshot, booking.AddOnTotalSnapshot,
                booking.VisitChargeSnapshot, booking.SubtotalSnapshot, booking.TaxPercentageSnapshot, booking.TaxAmountSnapshot,
                booking.PlatformFeeSnapshot, booking.TotalPayableSnapshot, booking.CouponCodeSnapshot, booking.CouponDiscountAmountSnapshot,
                booking.TotalPayableSnapshot),
            booking.Status,
            BookingStatusMapper.LabelFor(booking.Status),
            booking.StatusHistory
                .OrderBy(h => h.ChangedAtUtc)
                .Select(h => new AdminBookingStatusTimelineEntry(h.FromStatus, h.ToStatus, BookingStatusMapper.LabelFor(h.ToStatus), h.Reason, h.ChangedAtUtc))
                .ToList(),
            payment is null ? null : new AdminBookingPaymentSummary(
                payment.Id, payment.Status, payment.Amount, payment.Currency, payment.LatestAttempt?.GatewayPaymentRef, payment.CreatedAtUtc, payment.UpdatedAtUtc),
            cancellation is null ? null : new AdminBookingCancellationResponse(
                cancellation.Id, cancellation.Actor, cancellation.Reason, cancellation.WithinFreeCancellationWindow,
                cancellation.CancellationFeeAmount, cancellation.RefundAmount, cancellation.RefundMethod, cancellation.RefundTransactionId,
                cancellation.InternalNotes, cancellation.CreatedAtUtc),
            reschedules.Select(r => new AdminBookingRescheduleResponse(
                r.Id, r.Actor, r.Reason, r.FromSlotDate, r.FromSlotStartTime, r.ToSlotDate, r.ToSlotStartTime, r.IsLate, r.FeeAmount, r.CreatedAtUtc)).ToList(),
            refunds.Select(r => new AdminBookingRefundResponse(
                r.Id, r.FundingSource, r.Type, r.Method, r.Amount, r.Status, r.GatewayRefundRef, r.Reason, r.CreatedAtUtc, r.ProcessedAtUtc)).ToList(),
            booking.CreatedAtUtc,
            booking.BookingReference);
    }

    /// <inheritdoc/>
    public async Task<Result<AdminUnassignedAtRiskBookingSearchResponse>> ListUnassignedAtRiskAsync(AdminUnassignedAtRiskBookingRequest request)
    {
        var (rows, totalCount) = await _bookingRepository.ListUnassignedAtRiskAsync(request.Page, request.PageSize);

        var items = rows.Select(ToUnassignedAtRiskItem).ToList();
        return new AdminUnassignedAtRiskBookingSearchResponse(items, totalCount, request.Page, request.PageSize);
    }

    private static AdminUnassignedAtRiskBookingResponse ToUnassignedAtRiskItem(Booking booking) => new(
        booking.Id,
        booking.BookingReference,
        booking.CustomerNameSnapshot,
        booking.Items.Count > 0 ? booking.Items[0].NameSnapshot : "(no service)",
        booking.SlotDate,
        booking.SlotStartTimeSnapshot,
        booking.AddressCitySnapshot,
        booking.AddressPincodeSnapshot,
        booking.Status,
        BookingStatusMapper.LabelFor(booking.Status),
        booking.CreatedAtUtc);

    /// <inheritdoc/>
    public async Task<Result<AdminFulfilmentBoardResponse>> GetFulfilmentBoardAsync(AdminFulfilmentBoardRequest request)
    {
        var date = request.Date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var rows = await _bookingRepository.ListForFulfilmentBoardAsync(date);

        var providerIds = rows
            .Where(b => b.AssignedProviderId.HasValue)
            .Select(b => b.AssignedProviderId!.Value)
            .Distinct()
            .ToList();
        var providerNames = providerIds.Count > 0
            ? await _providerRepository.GetDisplayNamesByIdsAsync(providerIds)
            : new Dictionary<Guid, string>();

        var items = rows.Select(booking => ToFulfilmentBoardItem(booking, providerNames)).ToList();
        return new AdminFulfilmentBoardResponse(date, items);
    }

    private static AdminFulfilmentBoardBookingResponse ToFulfilmentBoardItem(
        Booking booking, IReadOnlyDictionary<Guid, string> providerNames) => new(
        booking.Id,
        booking.BookingReference,
        booking.CustomerNameSnapshot,
        booking.Items.Count > 0 ? booking.Items[0].NameSnapshot : "(no service)",
        booking.SlotDate,
        booking.SlotStartTimeSnapshot,
        booking.AddressCitySnapshot,
        booking.AddressPincodeSnapshot,
        booking.Status,
        BookingStatusMapper.LabelFor(booking.Status),
        booking.AssignedProviderId,
        booking.AssignedProviderId.HasValue && providerNames.TryGetValue(booking.AssignedProviderId.Value, out var name) ? name : null,
        booking.CreatedAtUtc);

    private static AdminBookingListItemResponse ToListItem(Booking booking) => new(
        booking.Id,
        booking.CustomerNameSnapshot,
        booking.CustomerMobileSnapshot,
        booking.Items.Count > 0 ? booking.Items[0].NameSnapshot : "(no service)",
        booking.AddressCitySnapshot,
        booking.SlotDate,
        booking.Status,
        BookingStatusMapper.LabelFor(booking.Status),
        booking.TotalPayableSnapshot,
        booking.CouponCodeSnapshot,
        booking.CreatedAtUtc,
        booking.BookingReference);
}
