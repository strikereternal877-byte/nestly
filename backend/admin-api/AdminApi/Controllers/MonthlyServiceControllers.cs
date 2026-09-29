using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Nestly.Application.MonthlyService;
using Nestly.BuildingBlocks.Extensions;
using Nestly.Domain;
using Nestly.Domain.MonthlyService;
using Nestly.Infrastructure;

namespace Nestly.AdminApi.Controllers;

internal static class MonthlyServiceControllerHelpers
{
    public static ModelStateDictionary ToModelState(ValidationResult validation)
    {
        var modelState = new ModelStateDictionary();
        foreach (var error in validation.Errors)
        {
            modelState.AddModelError(error.PropertyName, error.ErrorMessage);
        }

        return modelState;
    }
}

/// <summary>
/// Monthly Service plan catalog (docs/MONTHLY-SERVICE.md). Gated like the
/// AMC plan catalog - a commercial offering adjacent to Subscription - so
/// no new admin module is introduced.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/monthly-service-plans")]
[Authorize(AuthenticationSchemes = DependencyInjection.AdminJwtBearerScheme)]
public class MonthlyServicePlansController : ControllerBase
{
    private const string ReadPolicy = AdminModules.Subscription + ".read";
    private const string WritePolicy = AdminModules.Subscription + ".write";

    private readonly IMonthlyServiceAdminService _service;
    private readonly IValidator<MonthlyServicePlanUpsertRequest> _validator;

    public MonthlyServicePlansController(IMonthlyServiceAdminService service, IValidator<MonthlyServicePlanUpsertRequest> validator)
    {
        _service = service;
        _validator = validator;
    }

    [HttpGet]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<MonthlyServicePlanAdminResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List() => Ok(await _service.ListPlansAsync());

    [HttpGet("{id:guid}")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(MonthlyServicePlanAdminResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await _service.GetPlanAsync(id);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    [HttpPost]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(MonthlyServicePlanAdminResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] MonthlyServicePlanUpsertRequest request)
    {
        var validation = await _validator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(MonthlyServiceControllerHelpers.ToModelState(validation));
        }

        var result = await _service.CreatePlanAsync(request, User.GetSubjectId());
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetById), new { id = result.Value.Id }, result.Value)
            : result.ToProblemResult();
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(MonthlyServicePlanAdminResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid id, [FromBody] MonthlyServicePlanUpsertRequest request)
    {
        var validation = await _validator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(MonthlyServiceControllerHelpers.ToModelState(validation));
        }

        var result = await _service.UpdatePlanAsync(id, request, User.GetSubjectId());
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    [HttpPost("{id:guid}/activate")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Activate(Guid id)
    {
        var result = await _service.SetPlanActiveAsync(id, true, User.GetSubjectId());
        return result.IsSuccess ? NoContent() : result.ToProblemResult();
    }

    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Deactivate(Guid id)
    {
        var result = await _service.SetPlanActiveAsync(id, false, User.GetSubjectId());
        return result.IsSuccess ? NoContent() : result.ToProblemResult();
    }
}

/// <summary>
/// Monthly Service engagements, attendance and disputes. Gated behind
/// bookings.* - day-to-day fulfilment operations, the same tier the AMC and
/// recurring-plan contract views use.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/monthly-service-contracts")]
[Authorize(AuthenticationSchemes = DependencyInjection.AdminJwtBearerScheme)]
public class MonthlyServiceContractsController : ControllerBase
{
    private const string ReadPolicy = AdminModules.Bookings + ".read";
    private const string WritePolicy = AdminModules.Bookings + ".write";

    private readonly IMonthlyServiceAdminService _service;
    private readonly IValidator<MonthlyServiceAssignProviderRequest> _assignValidator;
    private readonly IValidator<MonthlyServiceCancelRequest> _cancelValidator;
    private readonly IValidator<MonthlyServiceResolveDisputeRequest> _resolveValidator;
    private readonly IValidator<MonthlyServiceCorrectAttendanceRequest> _correctValidator;

    public MonthlyServiceContractsController(
        IMonthlyServiceAdminService service,
        IValidator<MonthlyServiceAssignProviderRequest> assignValidator,
        IValidator<MonthlyServiceCancelRequest> cancelValidator,
        IValidator<MonthlyServiceResolveDisputeRequest> resolveValidator,
        IValidator<MonthlyServiceCorrectAttendanceRequest> correctValidator)
    {
        _service = service;
        _assignValidator = assignValidator;
        _cancelValidator = cancelValidator;
        _resolveValidator = resolveValidator;
        _correctValidator = correctValidator;
    }

    [HttpGet]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(MonthlyServiceContractAdminSearchResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(
        [FromQuery] MonthlyServiceContractStatus? status,
        [FromQuery] string? customerSearch,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20) =>
        Ok(await _service.SearchContractsAsync(status, customerSearch, page, pageSize));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(MonthlyServiceContractAdminDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await _service.GetContractAsync(id);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    [HttpGet("{id:guid}/attendance")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(MonthlyServiceAttendanceMonthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAttendance(Guid id, [FromQuery] int year, [FromQuery] int month)
    {
        if (!MonthlyServiceQuery.IsValidMonth(year, month))
        {
            ModelState.AddModelError(nameof(month), MonthlyServiceQuery.InvalidMonthMessage);
            return ValidationProblem(ModelState);
        }

        var result = await _service.GetAttendanceAsync(id, year, month);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    [HttpGet("{id:guid}/eligible-providers")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<MonthlyServiceEligibleProvider>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> EligibleProviders(Guid id)
    {
        var result = await _service.ListEligibleProvidersAsync(id);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    [HttpPost("{id:guid}/assign-provider")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(MonthlyServiceContractAdminDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AssignProvider(Guid id, [FromBody] MonthlyServiceAssignProviderRequest request)
    {
        var validation = await _assignValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(MonthlyServiceControllerHelpers.ToModelState(validation));
        }

        var result = await _service.AssignProviderAsync(id, request.ProviderId, User.GetSubjectId());
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    [HttpPost("{id:guid}/pause")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Pause(Guid id)
    {
        var result = await _service.PauseContractAsync(id, User.GetSubjectId());
        return result.IsSuccess ? NoContent() : result.ToProblemResult();
    }

    [HttpPost("{id:guid}/resume")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Resume(Guid id)
    {
        var result = await _service.ResumeContractAsync(id, User.GetSubjectId());
        return result.IsSuccess ? NoContent() : result.ToProblemResult();
    }

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] MonthlyServiceCancelRequest request)
    {
        var validation = await _cancelValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(MonthlyServiceControllerHelpers.ToModelState(validation));
        }

        var result = await _service.CancelContractAsync(id, request.Reason, User.GetSubjectId());
        return result.IsSuccess ? NoContent() : result.ToProblemResult();
    }

    [HttpGet("disputes")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<MonthlyServiceDisputeAdminItem>), StatusCodes.Status200OK)]
    public async Task<IActionResult> OpenDisputes() => Ok(await _service.ListOpenDisputesAsync());

    [HttpPost("attendance/{attendanceId:guid}/resolve-dispute")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(MonthlyServiceAttendanceItem), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResolveDispute(Guid attendanceId, [FromBody] MonthlyServiceResolveDisputeRequest request)
    {
        var validation = await _resolveValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(MonthlyServiceControllerHelpers.ToModelState(validation));
        }

        var result = await _service.ResolveDisputeAsync(attendanceId, request, User.GetSubjectId());
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    [HttpPut("attendance/{attendanceId:guid}")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(MonthlyServiceAttendanceItem), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CorrectAttendance(Guid attendanceId, [FromBody] MonthlyServiceCorrectAttendanceRequest request)
    {
        var validation = await _correctValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(MonthlyServiceControllerHelpers.ToModelState(validation));
        }

        var result = await _service.CorrectAttendanceAsync(attendanceId, request, User.GetSubjectId());
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Runs the daily sweep immediately (it is idempotent) - for operations after a correction, and for testing.</summary>
    [HttpPost("run-daily-job")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(MonthlyServiceDailyRunResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> RunDailyJob(CancellationToken cancellationToken) =>
        Ok(await _service.RunDailyJobNowAsync(cancellationToken));
}

/// <summary>Month-end invoices and offline payment recording - gated behind payments.*, like every other money-movement view.</summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/monthly-service-invoices")]
[Authorize(AuthenticationSchemes = DependencyInjection.AdminJwtBearerScheme)]
public class MonthlyServiceInvoicesController : ControllerBase
{
    private const string ReadPolicy = AdminModules.Payments + ".read";
    private const string WritePolicy = AdminModules.Payments + ".write";

    private readonly IMonthlyServiceAdminService _service;
    private readonly IValidator<MonthlyServiceRecordPaymentRequest> _validator;

    public MonthlyServiceInvoicesController(IMonthlyServiceAdminService service, IValidator<MonthlyServiceRecordPaymentRequest> validator)
    {
        _service = service;
        _validator = validator;
    }

    [HttpGet]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(MonthlyServiceInvoiceAdminSearchResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] MonthlyServiceInvoiceStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20) =>
        Ok(await _service.SearchInvoicesAsync(status, page, pageSize));

    [HttpPost("{id:guid}/record-payment")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(MonthlyServiceInvoiceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RecordPayment(Guid id, [FromBody] MonthlyServiceRecordPaymentRequest request)
    {
        var validation = await _validator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(MonthlyServiceControllerHelpers.ToModelState(validation));
        }

        var result = await _service.RecordPaymentAsync(id, request, User.GetSubjectId());
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }
}
