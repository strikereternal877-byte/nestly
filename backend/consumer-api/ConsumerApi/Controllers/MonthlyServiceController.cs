using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Nestly.Application.MonthlyService;
using Nestly.BuildingBlocks.Extensions;

namespace Nestly.ConsumerApi.Controllers;

/// <summary>
/// Customer side of the Monthly Service module (docs/MONTHLY-SERVICE.md):
/// browse plans, request an engagement, follow the attendance register
/// (skip / confirm / dispute a day), and pay month-end invoices. Every action
/// is scoped to the caller's own customer id.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Authorize]
public class MonthlyServiceController : ControllerBase
{
    private readonly IMonthlyServiceCustomerService _service;
    private readonly IValidator<MonthlyServiceContractRequest> _requestValidator;
    private readonly IValidator<MonthlyServiceCancelRequest> _cancelValidator;
    private readonly IValidator<MonthlyServiceDisputeRequest> _disputeValidator;

    public MonthlyServiceController(
        IMonthlyServiceCustomerService service,
        IValidator<MonthlyServiceContractRequest> requestValidator,
        IValidator<MonthlyServiceCancelRequest> cancelValidator,
        IValidator<MonthlyServiceDisputeRequest> disputeValidator)
    {
        _service = service;
        _requestValidator = requestValidator;
        _cancelValidator = cancelValidator;
        _disputeValidator = disputeValidator;
    }

    [AllowAnonymous]
    [HttpGet("api/v{version:apiVersion}/monthly-service/plans")]
    [ProducesResponseType(typeof(IReadOnlyList<MonthlyServicePlanBrowseResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> BrowsePlans([FromQuery] Guid? cityId) =>
        Ok(await _service.BrowsePlansAsync(cityId));

    [HttpPost("api/v{version:apiVersion}/monthly-service/contracts")]
    [ProducesResponseType(typeof(MonthlyServiceContractResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RequestContract([FromBody] MonthlyServiceContractRequest request)
    {
        var validation = await _requestValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _service.RequestContractAsync(CurrentCustomerId(), request);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetContract), new { id = result.Value.Id }, result.Value)
            : result.ToProblemResult();
    }

    [HttpGet("api/v{version:apiVersion}/me/monthly-service-contracts")]
    [ProducesResponseType(typeof(IReadOnlyList<MonthlyServiceContractResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListContracts() =>
        Ok(await _service.ListContractsAsync(CurrentCustomerId()));

    [HttpGet("api/v{version:apiVersion}/me/monthly-service-contracts/{id:guid}")]
    [ProducesResponseType(typeof(MonthlyServiceContractResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetContract(Guid id)
    {
        var result = await _service.GetContractAsync(CurrentCustomerId(), id);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    [HttpGet("api/v{version:apiVersion}/me/monthly-service-contracts/{id:guid}/attendance")]
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

        var result = await _service.GetAttendanceAsync(CurrentCustomerId(), id, year, month);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    [HttpPost("api/v{version:apiVersion}/me/monthly-service-contracts/{id:guid}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] MonthlyServiceCancelRequest request)
    {
        var validation = await _cancelValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _service.CancelContractAsync(CurrentCustomerId(), id, request.Reason);
        return result.IsSuccess ? NoContent() : result.ToProblemResult();
    }

    [HttpPost("api/v{version:apiVersion}/me/monthly-service-attendance/{id:guid}/skip")]
    [ProducesResponseType(typeof(MonthlyServiceAttendanceItem), StatusCodes.Status200OK)]
    public async Task<IActionResult> Skip(Guid id) =>
        ToResult(await _service.SkipAsync(CurrentCustomerId(), id));

    [HttpPost("api/v{version:apiVersion}/me/monthly-service-attendance/{id:guid}/unskip")]
    [ProducesResponseType(typeof(MonthlyServiceAttendanceItem), StatusCodes.Status200OK)]
    public async Task<IActionResult> Unskip(Guid id) =>
        ToResult(await _service.UnskipAsync(CurrentCustomerId(), id));

    [HttpPost("api/v{version:apiVersion}/me/monthly-service-attendance/{id:guid}/confirm")]
    [ProducesResponseType(typeof(MonthlyServiceAttendanceItem), StatusCodes.Status200OK)]
    public async Task<IActionResult> Confirm(Guid id) =>
        ToResult(await _service.ConfirmVisitAsync(CurrentCustomerId(), id));

    [HttpPost("api/v{version:apiVersion}/me/monthly-service-attendance/{id:guid}/dispute")]
    [ProducesResponseType(typeof(MonthlyServiceAttendanceItem), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Dispute(Guid id, [FromBody] MonthlyServiceDisputeRequest request)
    {
        var validation = await _disputeValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        return ToResult(await _service.DisputeAsync(CurrentCustomerId(), id, request.Reason));
    }

    [HttpGet("api/v{version:apiVersion}/me/monthly-service-invoices")]
    [ProducesResponseType(typeof(IReadOnlyList<MonthlyServiceInvoiceResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListInvoices() =>
        Ok(await _service.ListInvoicesAsync(CurrentCustomerId()));

    [HttpPost("api/v{version:apiVersion}/me/monthly-service-invoices/{id:guid}/pay")]
    [ProducesResponseType(typeof(MonthlyServiceInvoiceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> PayInvoice(Guid id)
    {
        var result = await _service.PayInvoiceAsync(CurrentCustomerId(), id);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    private IActionResult ToResult(Nestly.BuildingBlocks.Results.Result<MonthlyServiceAttendanceItem> result) =>
        result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();

    private Guid CurrentCustomerId() => User.GetSubjectId();

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
