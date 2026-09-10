using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Nestly.Application.BookingManagement;
using Nestly.Application.Bookings;
using Nestly.Application.Geography;
using Nestly.Application.ProviderManagement;
using Nestly.Application.Slots;
using Nestly.Application.Tracking;
using Nestly.BuildingBlocks.Extensions;
using Nestly.Domain;
using ResultOutcome = Nestly.BuildingBlocks.Results.Result;
using ErrorOutcome = Nestly.BuildingBlocks.Results.Error;
using Nestly.Infrastructure;

namespace Nestly.AdminApi.Controllers;

/// <summary>
/// Admin booking management (SRS 12.11, 12.13.2-3; tasks 115a-117c):
/// filterable search, full detail/timeline, general operational status
/// updates, and the cancel/reschedule/refund actions. Every mutating action
/// is delegated to <see cref="IBookingManagementService"/>, which in turn
/// composes the existing cancellation/reschedule/refund domain services
/// (tasks 80c, 82d, 75d) rather than reimplementing their policy math.
///
/// Full and partial refunds are both gated behind "bookings.write" rather
/// than two separate tiers - SRS 12.13.2 does not call for a stricter
/// permission on a full refund than a partial one, and
/// <c>AdminPermissionCatalog</c>'s own doc comment explicitly treats
/// splitting a module's Write tier further as a deliberate, not-yet-needed
/// extension (YAGNI) until a controller actually requires the distinction -
/// inventing a new "bookings.refund.full" code here would be exactly that
/// speculative split, and the task brief instructs not to invent new
/// permission codes.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/bookings")]
[Authorize(AuthenticationSchemes = DependencyInjection.AdminJwtBearerScheme)]
public class BookingsController : ControllerBase
{
    private const string ReadPolicy = AdminModules.Bookings + ".read";
    private const string WritePolicy = AdminModules.Bookings + ".write";

    private readonly IBookingManagementService _bookingManagementService;
    private readonly IBookingProviderAssignmentService _assignmentService;
    private readonly IBookingCompletionProofRepository _completionProofRepository;
    private readonly IBookingRepository _bookingRepository;
    private readonly IBookingTrackingQueryService _trackingQueryService;
    private readonly IGeographyQueryService _geographyQueryService;
    private readonly ISlotAvailabilityService _slotAvailabilityService;
    private readonly IValidator<AdminBookingSearchRequest> _searchValidator;
    private readonly IValidator<AdminBookingStatusUpdateRequest> _statusUpdateValidator;
    private readonly IValidator<AdminCancelBookingRequest> _cancelValidator;
    private readonly IValidator<AdminRescheduleBookingRequest> _rescheduleValidator;
    private readonly IValidator<AdminRefundRequest> _refundValidator;
    private readonly IValidator<AdminManualPaymentRequest> _manualPaymentValidator;
    private readonly IValidator<AssignProviderRequest> _assignProviderValidator;
    private readonly IValidator<RejectAssignmentRequest> _rejectAssignmentValidator;

    public BookingsController(
        IBookingManagementService bookingManagementService,
        IBookingProviderAssignmentService assignmentService,
        IBookingCompletionProofRepository completionProofRepository,
        IBookingRepository bookingRepository,
        IBookingTrackingQueryService trackingQueryService,
        IGeographyQueryService geographyQueryService,
        ISlotAvailabilityService slotAvailabilityService,
        IValidator<AdminBookingSearchRequest> searchValidator,
        IValidator<AdminBookingStatusUpdateRequest> statusUpdateValidator,
        IValidator<AdminCancelBookingRequest> cancelValidator,
        IValidator<AdminRescheduleBookingRequest> rescheduleValidator,
        IValidator<AdminRefundRequest> refundValidator,
        IValidator<AdminManualPaymentRequest> manualPaymentValidator,
        IValidator<AssignProviderRequest> assignProviderValidator,
        IValidator<RejectAssignmentRequest> rejectAssignmentValidator)
    {
        _bookingManagementService = bookingManagementService;
        _assignmentService = assignmentService;
        _completionProofRepository = completionProofRepository;
        _bookingRepository = bookingRepository;
        _trackingQueryService = trackingQueryService;
        _geographyQueryService = geographyQueryService;
        _slotAvailabilityService = slotAvailabilityService;
        _searchValidator = searchValidator;
        _statusUpdateValidator = statusUpdateValidator;
        _cancelValidator = cancelValidator;
        _rescheduleValidator = rescheduleValidator;
        _refundValidator = refundValidator;
        _manualPaymentValidator = manualPaymentValidator;
        _assignProviderValidator = assignProviderValidator;
        _rejectAssignmentValidator = rejectAssignmentValidator;
    }

    /// <summary>Filterable, paginated booking search (SRS 12.11.1, task 115a).</summary>
    [HttpGet]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(AdminBookingSearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search(
        [FromQuery] Guid? bookingId,
        [FromQuery] string? customerName,
        [FromQuery] string? customerMobile,
        [FromQuery] BookingStatus? status,
        [FromQuery] string? city,
        [FromQuery] DateOnly? slotDateFrom,
        [FromQuery] DateOnly? slotDateTo,
        [FromQuery] DateTime? createdFromUtc,
        [FromQuery] DateTime? createdToUtc,
        [FromQuery] Guid? serviceId,
        [FromQuery] Guid? categoryId,
        [FromQuery] string? couponCode,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? reference = null)
    {
        var request = new AdminBookingSearchRequest(
            bookingId, customerName, customerMobile, status, city, slotDateFrom, slotDateTo,
            createdFromUtc, createdToUtc, serviceId, categoryId, couponCode, page, pageSize, reference);

        var validation = await _searchValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _bookingManagementService.SearchAsync(request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Full detail: snapshots, status timeline, payment, cancellation/reschedule/refund history (SRS 12.11.2, tasks 115b-115c).</summary>
    [HttpGet("{bookingId:guid}")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(AdminBookingDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDetail(Guid bookingId)
    {
        var result = await _bookingManagementService.GetDetailAsync(bookingId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>General operational status transition (SRS 12.11.3, task 115d) - see <see cref="AdminBookingStatusUpdateRequest"/> for the restricted target-status set.</summary>
    [HttpPost("{bookingId:guid}/status")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(AdminBookingDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UpdateStatus(Guid bookingId, [FromBody] AdminBookingStatusUpdateRequest request)
    {
        var validation = await _statusUpdateValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _bookingManagementService.UpdateStatusAsync(bookingId, CurrentAdminUserId(), request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Admin-initiated cancellation (SRS 12.11.3, task 117a) via the existing cancellation domain service (task 80c).</summary>
    [HttpPost("{bookingId:guid}/cancel")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(AdminBookingDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Cancel(Guid bookingId, [FromBody] AdminCancelBookingRequest request)
    {
        var validation = await _cancelValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _bookingManagementService.CancelAsync(bookingId, CurrentAdminUserId(), request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>
    /// Active cities for the reschedule panel's locality picker (row 26,
    /// docs/OPEN-FIXES-FEATURES.csv) - the same <see cref="IGeographyQueryService"/>
    /// the customer booking flow's city selector uses, so this never
    /// re-derives its own city list.
    /// </summary>
    [HttpGet("reschedule-cities")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<CityResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRescheduleCities() => Ok(await _geographyQueryService.ListActiveCitiesAsync());

    /// <summary>
    /// Localities matching a name/pincode search within a city (row 26,
    /// docs/OPEN-FIXES-FEATURES.csv) - resolves the <see cref="AdminRescheduleBookingRequest.LocalityId"/>
    /// the reschedule action needs, via the same <see cref="IGeographyQueryService"/>
    /// the customer booking flow's <c>LocalitySelector</c> calls, instead of
    /// asking the admin to paste a raw UUID.
    /// </summary>
    [HttpGet("reschedule-localities")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<LocalityResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRescheduleLocalities([FromQuery] Guid cityId, [FromQuery] string? search)
    {
        var result = await _geographyQueryService.SearchLocalitiesAsync(cityId, search);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>
    /// Available slot windows for this booking's service, at a candidate
    /// locality, on a candidate date (row 26, docs/OPEN-FIXES-FEATURES.csv) -
    /// resolves the <see cref="AdminRescheduleBookingRequest.SlotWindowId"/>
    /// the reschedule action needs, via the same <see cref="ISlotAvailabilityService"/>
    /// the customer booking flow's <c>SlotPicker</c> calls, instead of asking
    /// the admin to paste a raw UUID.
    /// </summary>
    [HttpGet("{bookingId:guid}/reschedule-slots")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(SlotAvailabilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRescheduleSlots(Guid bookingId, [FromQuery] Guid localityId, [FromQuery] DateOnly date)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null)
        {
            return ResultOutcome.Failure(ErrorOutcome.NotFound("Booking.NotFound", "The specified booking does not exist.")).ToProblemResult();
        }

        var serviceId = booking.Items.FirstOrDefault()?.ServiceId;
        if (serviceId is null)
        {
            return ResultOutcome.Failure(ErrorOutcome.NotFound("Booking.NoServiceItem", "This booking has no service line item to check availability for.")).ToProblemResult();
        }

        var result = await _slotAvailabilityService.GetAvailableSlotsAsync(serviceId.Value, localityId, date);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Admin-initiated reschedule (SRS 12.11.3, task 117b) via the existing reschedule domain service (task 82d).</summary>
    [HttpPost("{bookingId:guid}/reschedule")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(AdminBookingDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Reschedule(Guid bookingId, [FromBody] AdminRescheduleBookingRequest request)
    {
        var validation = await _rescheduleValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _bookingManagementService.RescheduleAsync(bookingId, CurrentAdminUserId(), request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Full or partial refund with audit (SRS 12.11.3, 12.13.2-3, task 117c) via the existing refund domain service (task 75d).</summary>
    [HttpPost("{bookingId:guid}/refund")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(AdminBookingDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Refund(Guid bookingId, [FromBody] AdminRefundRequest request)
    {
        var validation = await _refundValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _bookingManagementService.RefundAsync(bookingId, CurrentAdminUserId(), request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Records a manual/offline payment (cash, UPI, bank transfer) against a booking Awaiting Payment (row 25, docs/OPEN-FIXES-FEATURES.csv) - transitions the booking exactly like a successful gateway payment via <see cref="Nestly.Application.Payments.IPaymentWebhookService.RecordManualPaymentAsync"/>.</summary>
    [HttpPost("{bookingId:guid}/manual-payment")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(AdminBookingDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RecordManualPayment(Guid bookingId, [FromBody] AdminManualPaymentRequest request)
    {
        var validation = await _manualPaymentValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _bookingManagementService.RecordManualPaymentAsync(bookingId, CurrentAdminUserId(), request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Assigns (or reassigns) a provider to a booking (task 147, PROVIDER.md OPEN DECISIONS #1 - manual admin-driven assignment). Gated behind "bookings.write" - the existing permission code, per PROVIDER.md's SCOPE BOUNDARY this is Booking-domain behaviour, not a separate Provider-module permission. Returns 409 with "BookingProviderAssignment.ProviderDoubleBooked" when the provider is already on an overlapping job (task 288) - unlike their advisory capacity limits, that one is a hard stop even for an admin.</summary>
    [HttpPost("{bookingId:guid}/assign-provider")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(BookingProviderAssignmentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> AssignProvider(Guid bookingId, [FromBody] AssignProviderRequest request)
    {
        var validation = await _assignProviderValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _assignmentService.AssignAsync(bookingId, CurrentAdminUserId(), request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Rejects the booking's current outstanding assignment (task 159) - clears the assigned provider and returns the booking to AwaitingFulfilment so it needs manual reassignment (no auto-match, PROVIDER.md OPEN DECISIONS #1).</summary>
    [HttpPost("{bookingId:guid}/reject-assignment")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(BookingProviderAssignmentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RejectAssignment(Guid bookingId, [FromBody] RejectAssignmentRequest request)
    {
        var validation = await _rejectAssignmentValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _assignmentService.RejectAsync(bookingId, request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Full provider-assignment history for a booking, newest first (task 147/159) - shows prior rejections/reassignments leading to the current state.</summary>
    [HttpGet("{bookingId:guid}/assignments")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<BookingProviderAssignmentResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAssignmentHistory(Guid bookingId)
    {
        var result = await _assignmentService.GetHistoryAsync(bookingId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>
    /// Candidate providers for manually assigning this booking - matched by
    /// service area (pincode/city) and skill (service/category), ranked by
    /// specificity then current load. Read-only, to inform the admin's own
    /// choice before calling <see cref="AssignProvider"/>: no auto-dispatch
    /// (PROVIDER.md OPEN DECISIONS #1).
    /// </summary>
    [HttpGet("{bookingId:guid}/eligible-providers")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<EligibleProviderResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> GetEligibleProviders(Guid bookingId)
    {
        var result = await _assignmentService.GetEligibleProvidersAsync(bookingId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Completion proof (photos + checklist) for a booking, if any (task 198, SRS 12.11.2 dispute review).</summary>
    [HttpGet("{bookingId:guid}/completion-proof")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(BookingCompletionProofResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCompletionProof(Guid bookingId)
    {
        var result = await _completionProofRepository.GetForAdminAsync(_bookingRepository, bookingId);
        if (result.IsFailure)
        {
            return result.ToProblemResult();
        }

        return result.Value is null ? NoContent() : Ok(result.Value);
    }

    /// <summary>Live tracking snapshot for the admin ops view (task 284) - same shape task 275 built for the customer screen, minus the ownership check.</summary>
    [HttpGet("{bookingId:guid}/tracking")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(BookingTrackingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTracking(Guid bookingId)
    {
        var result = await _trackingQueryService.GetForAdminAsync(bookingId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    private Guid CurrentAdminUserId() =>
        User.GetSubjectId();

    private static ModelStateDictionary ToModelState(ValidationResult validation)
    {
        var modelState = new ModelStateDictionary();
        foreach (var error in validation.Errors)
        {
            modelState.AddModelError(error.PropertyName, error.ErrorMessage);
        }

        return modelState;
    }
}
