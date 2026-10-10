using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Nestly.Application.RecurringBookings;
using Nestly.BuildingBlocks.Extensions;
using Nestly.Domain;
using Nestly.Infrastructure;

namespace Nestly.AdminApi.Controllers;

/// <summary>
/// Admin visibility into recurring booking plans (task 299,
/// PRODUCT-ENHANCEMENTS.md section 2): the full plan list, the
/// status/cadence/upcoming-volume report behind it, and (Order/Booking
/// Management UX pass) plan-level cancellation - see
/// <see cref="IRecurringBookingPlanAdminService"/> for what cancellation does
/// and does not affect.
///
/// RBAC: read actions are gated behind the EXISTING "bookings.read", with no new
/// <c>AdminModules</c> entry and no "RecurringPlans.View" code. The task brief
/// left that open ("no new RBAC module needed if admin's existing Booking view
/// permission already covers occurrence rows"); it does, for three reasons:
///
/// 1. A recurring plan is a standing instruction to create Bookings, and every
///    row this controller reports on is either a <see cref="RecurringBookingPlan"/>
///    or a <see cref="Booking"/> carrying that plan's id (task 296's
///    <see cref="Booking.RecurringBookingPlanId"/>). An admin holding
///    "bookings.read" can already open every one of those bookings
///    individually through <c>BookingsController</c> and read strictly more
///    about each of them (customer contact details, payment, refunds) than
///    this controller's counts expose. A new permission gating a strictly
///    weaker view of data the holder can already see is not a boundary, it is
///    an inconvenience - and one that fails open, because the underlying
///    bookings stay readable either way.
///
/// 2. <c>BookingsController</c> already set this precedent in the opposite
///    direction: provider assignment lives under "bookings.write" rather than
///    the Provider module's, because assigning a provider is Booking-domain
///    behaviour. Recurrence is likewise a property of how bookings come into
///    existence, not a separate vertical.
///
/// 3. <c>AdminPermissionAction</c>'s own doc comment calls splitting the
///    matrix further "speculative (YAGNI)" until a controller actually needs
///    the distinction, and <c>AdminModules</c> records the same judgement for
///    Referral/Chat/Nestly Coins. A new module here would also cost a seed
///    migration (<c>SeedNestlyCoinsPermissions</c> is the precedent) and a
///    role-grant decision for all nine default roles - real schema and policy
///    churn bought for no additional protection.
///
/// The practical consequence is intended: Operations Admin and Booking Admin,
/// the two roles that own day-to-day fulfilment, see recurring plans on day
/// one without a permission grant, exactly as they see the bookings those
/// plans generate.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/recurring-plans")]
[Authorize(AuthenticationSchemes = DependencyInjection.AdminJwtBearerScheme)]
public class RecurringPlansController : ControllerBase
{
    private const string ReadPolicy = AdminModules.Bookings + ".read";
    private const string WritePolicy = AdminModules.Bookings + ".write";

    private readonly IRecurringBookingPlanAdminService _adminService;
    private readonly IValidator<AdminRecurringPlanSearchRequest> _searchValidator;
    private readonly IValidator<AdminRecurringPlanReportRequest> _reportValidator;
    private readonly IValidator<AdminCancelRecurringPlanRequest> _cancelValidator;
    private readonly IValidator<AdminPauseRecurringPlanRequest> _pauseValidator;
    private readonly IValidator<AdminResumeRecurringPlanRequest> _resumeValidator;

    public RecurringPlansController(
        IRecurringBookingPlanAdminService adminService,
        IValidator<AdminRecurringPlanSearchRequest> searchValidator,
        IValidator<AdminRecurringPlanReportRequest> reportValidator,
        IValidator<AdminCancelRecurringPlanRequest> cancelValidator,
        IValidator<AdminPauseRecurringPlanRequest> pauseValidator,
        IValidator<AdminResumeRecurringPlanRequest> resumeValidator)
    {
        _adminService = adminService;
        _searchValidator = searchValidator;
        _reportValidator = reportValidator;
        _cancelValidator = cancelValidator;
        _pauseValidator = pauseValidator;
        _resumeValidator = resumeValidator;
    }

    /// <summary>Every recurring plan on the platform, newest first, filterable by lifecycle status, cadence, customer, service, pause reason or whether it is paid in advance.</summary>
    [HttpGet]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(AdminRecurringPlanSearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search(
        [FromQuery] RecurringBookingPlanStatus? status,
        [FromQuery] RecurringBookingRecurrenceFrequency? frequency,
        [FromQuery] Guid? customerId,
        [FromQuery] Guid? serviceId,
        [FromQuery] RecurringBookingPauseReason? pauseReason,
        [FromQuery] bool? prepaidUpfront,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var request = new AdminRecurringPlanSearchRequest(status, frequency, customerId, serviceId, page, pageSize, pauseReason, prepaidUpfront);

        var validation = await _searchValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _adminService.SearchAsync(request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Active/paused/cancelled/completed plan counts, the active-plan cadence mix, and upcoming occurrence volume over a horizon (defaults to the next four weeks).</summary>
    [HttpGet("report")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(AdminRecurringPlanReportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> GetReport([FromQuery] DateOnly? fromDate, [FromQuery] DateOnly? toDate)
    {
        var request = new AdminRecurringPlanReportRequest(fromDate, toDate);

        var validation = await _reportValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _adminService.GetReportAsync(request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>One plan with the customer's contact and wallet balance and the visits it has generated (upcoming first, then the latest past ones).</summary>
    [HttpGet("{planId:guid}")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(AdminRecurringPlanDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDetail(Guid planId)
    {
        var result = await _adminService.GetAsync(planId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>
    /// Pauses an active plan on the customer's behalf: no new visits are booked while it is paused, visits already
    /// booked are untouched. The customer is told support paused it and cannot resume it themselves; the reason goes
    /// to the audit trail.
    /// </summary>
    [HttpPost("{planId:guid}/pause")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(AdminRecurringPlanSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Pause(Guid planId, [FromBody] AdminPauseRecurringPlanRequest request)
    {
        var validation = await _pauseValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _adminService.PauseAsync(planId, User.GetSubjectId(), request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Resumes a paused plan, whoever or whatever paused it (including one the system paused for unpaid visits). The customer is told; the reason goes to the audit trail.</summary>
    [HttpPost("{planId:guid}/resume")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(AdminRecurringPlanSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Resume(Guid planId, [FromBody] AdminResumeRecurringPlanRequest request)
    {
        var validation = await _resumeValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _adminService.ResumeAsync(planId, User.GetSubjectId(), request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Cancels the whole standing instruction - no further occurrences are ever generated. Distinct from cancelling the individual bookings it has already produced, which is unaffected and still goes through <c>BookingsController</c>.</summary>
    [HttpPost("{planId:guid}/cancel")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(AdminRecurringPlanSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Cancel(Guid planId, [FromBody] AdminCancelRecurringPlanRequest request)
    {
        var validation = await _cancelValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _adminService.CancelAsync(planId, User.GetSubjectId(), request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

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
