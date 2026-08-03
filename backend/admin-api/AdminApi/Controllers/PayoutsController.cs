using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Nestly.Application.ProviderManagement;
using Nestly.BuildingBlocks.Extensions;
using Nestly.Domain;
using Nestly.Infrastructure;

namespace Nestly.AdminApi.Controllers;

/// <summary>
/// Admin provider payout batches (PROVIDER.md Financial Domain, API surface
/// "run payout batch, list payouts"; task 148). OPEN DECISIONS #3: v1 is
/// manual bank transfer - a batch is created from the provider's earning
/// ledger for a period, then an admin walks its status Pending -&gt;
/// Processing -&gt; Paid/Failed by hand; there is no gateway integration.
/// Gated "payout.read"/"payout.write" (PROVIDER.md RBAC ADDITIONS).
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/payouts")]
[Authorize(AuthenticationSchemes = DependencyInjection.AdminJwtBearerScheme)]
public class PayoutsController : ControllerBase
{
    private const string ReadPolicy = AdminModules.Payout + ".read";
    private const string WritePolicy = AdminModules.Payout + ".write";

    private readonly IProviderPayoutService _payoutService;
    private readonly IValidator<CreateProviderPayoutRequest> _createValidator;
    private readonly IValidator<UpdateProviderPayoutStatusRequest> _updateStatusValidator;

    public PayoutsController(
        IProviderPayoutService payoutService,
        IValidator<CreateProviderPayoutRequest> createValidator,
        IValidator<UpdateProviderPayoutStatusRequest> updateStatusValidator)
    {
        _payoutService = payoutService;
        _createValidator = createValidator;
        _updateStatusValidator = updateStatusValidator;
    }

    /// <summary>Search/filter payouts by provider and/or status.</summary>
    [HttpGet]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(ProviderPayoutSearchResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(
        [FromQuery] Guid? providerId,
        [FromQuery] ProviderPayoutStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var result = await _payoutService.SearchAsync(providerId, status, page, pageSize);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    [HttpGet("{payoutId:guid}")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(ProviderPayoutResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDetail(Guid payoutId)
    {
        var result = await _payoutService.GetByIdAsync(payoutId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Runs a payout batch for a provider over a period, summing their earning ledger (task 148).</summary>
    [HttpPost("providers/{providerId:guid}")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(ProviderPayoutResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateBatch(Guid providerId, [FromBody] CreateProviderPayoutRequest request)
    {
        var validation = await _createValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _payoutService.CreateBatchAsync(providerId, request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Advances a payout's status: Pending -&gt; Processing -&gt; Paid (with a bank transfer reference), or -&gt; Failed (task 148, OPEN DECISIONS #3 - admin-triggered, not gateway-driven).</summary>
    [HttpPost("{payoutId:guid}/status")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(ProviderPayoutResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UpdateStatus(Guid payoutId, [FromBody] UpdateProviderPayoutStatusRequest request)
    {
        var validation = await _updateStatusValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _payoutService.UpdateStatusAsync(payoutId, request);
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
