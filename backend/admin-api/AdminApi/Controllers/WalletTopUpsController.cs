using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nestly.Application.Wallet;
using Nestly.BuildingBlocks.Extensions;
using Nestly.Domain;
using Nestly.Infrastructure;

namespace Nestly.AdminApi.Controllers;

/// <summary>
/// Admin view of customers' wallet top-ups: a filterable list that says which ones need attention (stuck, or a
/// gateway callback that disagreed with the amount asked for), one top-up's detail, and "Reconcile now" to ask the
/// gateway about a stuck one. Part of the Payments module - reading needs "payments.read", reconciling needs
/// "payments.write" - because it is money moving through the payment gateway, the same surface
/// <see cref="PaymentsController"/> covers for bookings.
///
/// Reconciling cannot invent a credit: it runs the same gateway check, behind the same conditional update, as the
/// background sweep. Crediting a wallet by hand stays the customer page's audited wallet adjustment.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/wallet-top-ups")]
[Authorize(AuthenticationSchemes = DependencyInjection.AdminJwtBearerScheme)]
public class WalletTopUpsController : ControllerBase
{
    private const string ReadPolicy = AdminModules.Payments + ".read";
    private const string WritePolicy = AdminModules.Payments + ".write";

    private readonly IAdminWalletTopUpService _topUpService;

    public WalletTopUpsController(IAdminWalletTopUpService topUpService)
    {
        _topUpService = topUpService;
    }

    /// <summary>Top-ups, newest first, filterable by status, "needs attention", search text and creation date, with the day's summary figures.</summary>
    [HttpGet]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(PagedAdminWalletTopUpResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] AdminWalletTopUpFilterRequest request)
    {
        var result = await _topUpService.SearchAsync(request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>One top-up. 404 when the id is not a top-up.</summary>
    [HttpGet("{topUpId:guid}")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(AdminWalletTopUpResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid topUpId)
    {
        var result = await _topUpService.GetAsync(topUpId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>
    /// Asks the gateway how this top-up ended and applies a definite answer (credit on success, Failed on a declined
    /// payment, nothing while the gateway still says pending). Audited. Safe to repeat.
    /// </summary>
    [HttpPost("{topUpId:guid}/reconcile")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(AdminWalletTopUpReconcileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reconcile(Guid topUpId)
    {
        var result = await _topUpService.ReconcileAsync(topUpId, User.GetSubjectId());
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }
}
