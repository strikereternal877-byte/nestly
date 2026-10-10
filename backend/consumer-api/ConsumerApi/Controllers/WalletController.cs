using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using Nestly.Application.Wallet;
using Nestly.BuildingBlocks.Extensions;

namespace Nestly.ConsumerApi.Controllers;

/// <summary>
/// Wallet balance and ledger (SRS 11.17.1, 14.5, task 74c), and adding money to it. Every action is scoped to
/// the caller's own customer id.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Authorize]
[Route("api/v{version:apiVersion}/wallet")]
public class WalletController : ControllerBase
{
    private readonly IWalletService _walletService;
    private readonly IWalletTopUpService _topUpService;
    private readonly IValidator<CreateWalletTopUpRequest> _createTopUpValidator;

    public WalletController(
        IWalletService walletService,
        IWalletTopUpService topUpService,
        IValidator<CreateWalletTopUpRequest> createTopUpValidator)
    {
        _walletService = walletService;
        _topUpService = topUpService;
        _createTopUpValidator = createTopUpValidator;
    }

    [HttpGet("balance")]
    [ProducesResponseType(typeof(WalletBalanceResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Balance()
    {
        var result = await _walletService.GetBalanceAsync(CurrentCustomerId());
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    [HttpGet("ledger")]
    [ProducesResponseType(typeof(IReadOnlyList<WalletLedgerEntryResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Ledger()
    {
        var result = await _walletService.GetLedgerAsync(CurrentCustomerId());
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Whether adding money is switched on, and its limits - what the "Add money" screen reads before offering anything.</summary>
    [HttpGet("top-up/config")]
    [ProducesResponseType(typeof(WalletTopUpConfigResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> TopUpConfig() => Ok(await _topUpService.GetConfigAsync());

    /// <summary>Starts adding money: creates the gateway order and returns where to send the customer. 422 when top-ups are off or a limit is hit.</summary>
    [HttpPost("top-ups")]
    [EnableRateLimiting("payment")]
    [ProducesResponseType(typeof(WalletTopUpOrderResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateTopUp([FromBody] CreateWalletTopUpRequest request)
    {
        var validation = await _createTopUpValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _topUpService.CreateAsync(CurrentCustomerId(), request.Amount);
        return result.IsSuccess ? StatusCode(StatusCodes.Status201Created, result.Value) : result.ToProblemResult();
    }

    [HttpGet("top-ups/{id:guid}")]
    [ProducesResponseType(typeof(WalletTopUpResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTopUp(Guid id)
    {
        var result = await _topUpService.GetAsync(CurrentCustomerId(), id);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>
    /// Asks the gateway directly how a still-pending top-up ended - for a checkout the customer abandoned or whose
    /// webhook never arrived. Called by the return page once its own short wait elapses. A safe no-op once resolved.
    /// </summary>
    [HttpPost("top-ups/{id:guid}/verify")]
    [EnableRateLimiting("payment")]
    [ProducesResponseType(typeof(WalletTopUpResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> VerifyTopUp(Guid id)
    {
        var result = await _topUpService.VerifyPendingAsync(CurrentCustomerId(), id);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Sandbox-only: completes a top-up the way the gateway's callback would. 422 when a real gateway is configured.</summary>
    [HttpPost("top-ups/{id:guid}/simulate")]
    [EnableRateLimiting("payment")]
    [ProducesResponseType(typeof(WalletTopUpResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SimulateTopUp(Guid id)
    {
        var result = await _topUpService.SimulateAsync(CurrentCustomerId(), id);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    private Guid CurrentCustomerId() =>
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
