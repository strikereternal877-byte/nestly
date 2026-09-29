using Asp.Versioning;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Nestly.Application.MonthlyService;
using Nestly.BuildingBlocks.Extensions;
using Nestly.BuildingBlocks.Results;
using Nestly.Infrastructure;

namespace Nestly.ProviderApi.Controllers;

/// <summary>
/// Professional side of the Monthly Service module (docs/MONTHLY-SERVICE.md):
/// their engagements, the day's visits, and attendance marking - check-in
/// with the customer's day code, check-out, leave, and "customer not
/// available". Scoped to the caller's own provider id.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Authorize(AuthenticationSchemes = DependencyInjection.ProviderJwtBearerScheme)]
[Route("api/v{version:apiVersion}/monthly-service")]
public class MonthlyServiceController : ControllerBase
{
    private readonly IMonthlyServiceProviderService _service;
    private readonly IValidator<MonthlyServiceCheckInRequest> _checkInValidator;
    private readonly IValidator<MonthlyServiceNoteRequest> _noteValidator;

    public MonthlyServiceController(
        IMonthlyServiceProviderService service,
        IValidator<MonthlyServiceCheckInRequest> checkInValidator,
        IValidator<MonthlyServiceNoteRequest> noteValidator)
    {
        _service = service;
        _checkInValidator = checkInValidator;
        _noteValidator = noteValidator;
    }

    [HttpGet("contracts")]
    [ProducesResponseType(typeof(IReadOnlyList<MonthlyServiceProviderContractResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListContracts() =>
        Ok(await _service.ListContractsAsync(CurrentProviderId()));

    [HttpGet("contracts/{id:guid}/attendance")]
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

        var result = await _service.GetAttendanceAsync(CurrentProviderId(), id, year, month);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>The visits on one day (defaults to today, business timezone).</summary>
    [HttpGet("visits")]
    [ProducesResponseType(typeof(IReadOnlyList<MonthlyServiceProviderVisitResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListVisits([FromQuery] DateOnly? date) =>
        Ok(await _service.ListVisitsAsync(CurrentProviderId(), date));

    [HttpGet("invoices")]
    [ProducesResponseType(typeof(IReadOnlyList<MonthlyServiceInvoiceResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListInvoices() =>
        Ok(await _service.ListInvoicesAsync(CurrentProviderId()));

    [HttpPost("visits/{id:guid}/check-in")]
    [ProducesResponseType(typeof(MonthlyServiceAttendanceItem), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CheckIn(Guid id, [FromBody] MonthlyServiceCheckInRequest request)
    {
        var validation = await _checkInValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        return ToResult(await _service.CheckInAsync(CurrentProviderId(), id, request));
    }

    [HttpPost("visits/{id:guid}/check-out")]
    [ProducesResponseType(typeof(MonthlyServiceAttendanceItem), StatusCodes.Status200OK)]
    public async Task<IActionResult> CheckOut(Guid id) =>
        ToResult(await _service.CheckOutAsync(CurrentProviderId(), id));

    [HttpPost("visits/{id:guid}/customer-unavailable")]
    [ProducesResponseType(typeof(MonthlyServiceAttendanceItem), StatusCodes.Status200OK)]
    public Task<IActionResult> CustomerUnavailable(Guid id, [FromBody] MonthlyServiceNoteRequest request) =>
        WithNoteAsync(request, () => _service.MarkCustomerUnavailableAsync(CurrentProviderId(), id, request.Note));

    [HttpPost("visits/{id:guid}/leave")]
    [ProducesResponseType(typeof(MonthlyServiceAttendanceItem), StatusCodes.Status200OK)]
    public Task<IActionResult> Leave(Guid id, [FromBody] MonthlyServiceNoteRequest request) =>
        WithNoteAsync(request, () => _service.MarkLeaveAsync(CurrentProviderId(), id, request.Note));

    [HttpPost("visits/{id:guid}/cancel-leave")]
    [ProducesResponseType(typeof(MonthlyServiceAttendanceItem), StatusCodes.Status200OK)]
    public async Task<IActionResult> CancelLeave(Guid id) =>
        ToResult(await _service.CancelLeaveAsync(CurrentProviderId(), id));

    private async Task<IActionResult> WithNoteAsync(MonthlyServiceNoteRequest request, Func<Task<Result<MonthlyServiceAttendanceItem>>> action)
    {
        var validation = await _noteValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        return ToResult(await action());
    }

    private IActionResult ToResult(Result<MonthlyServiceAttendanceItem> result) =>
        result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();

    private Guid CurrentProviderId() => User.GetSubjectId();

    private static ModelStateDictionary ToModelState(FluentValidation.Results.ValidationResult validation)
    {
        var modelState = new ModelStateDictionary();
        foreach (var error in validation.Errors)
        {
            modelState.AddModelError(error.PropertyName, error.ErrorMessage);
        }

        return modelState;
    }
}
