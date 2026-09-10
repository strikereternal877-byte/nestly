using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nestly.Application.Payments;
using Nestly.BuildingBlocks.Extensions;
using Nestly.Domain;
using Nestly.Infrastructure;

namespace Nestly.AdminApi.Controllers;

/// <summary>
/// Admin payment transaction view (SRS 12.13.1, task 311): a filterable
/// transaction list and a per-transaction detail (attempts + refunds), the
/// reconciliation surface admins previously only got incidentally through a
/// booking's own detail page (<c>BookingsController.GetDetail</c>'s embedded
/// <c>AdminBookingPaymentSummary</c>/<c>Refunds</c>). Read-only for the
/// list/detail themselves - see <see cref="AdminModules.Payments"/>'s doc
/// comment for why there is still no refund-initiation write endpoint here
/// (SRS 12.13.2-3 remains <c>BookingsController</c>'s "bookings.write"-gated
/// action) - plus the payment reconciliation queue and its one write action
/// (docs/OPEN-FIXES-FEATURES.csv "Payment reconciliation"): stuck-pending,
/// failed and orphaned Awaiting Payment bookings, gateway orders versus
/// booking status, with a void action for a stuck pending order.
///
/// A transaction id that does not exist 404s rather than 403ing (SRS 28.3
/// IDOR guard, same rule <c>docs/API.md</c>'s address-endpoint section
/// documents) - there is no ownership concept to hide behind here since
/// every admin holding "payments.read" may see every transaction, but the
/// convention of never leaking existence via status code is kept consistent
/// with the rest of the admin API regardless.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/payments")]
[Authorize(AuthenticationSchemes = DependencyInjection.AdminJwtBearerScheme)]
public class PaymentsController : ControllerBase
{
    private const string ReadPolicy = AdminModules.Payments + ".read";
    private const string WritePolicy = AdminModules.Payments + ".write";

    private readonly IAdminPaymentQueryService _paymentQueryService;
    private readonly IAdminPaymentReconciliationService _reconciliationService;

    public PaymentsController(IAdminPaymentQueryService paymentQueryService, IAdminPaymentReconciliationService reconciliationService)
    {
        _paymentQueryService = paymentQueryService;
        _reconciliationService = reconciliationService;
    }

    /// <summary>Transaction list, filterable by booking id, status and creation date range (SRS 12.13.1).</summary>
    [HttpGet]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(PagedAdminPaymentTransactionResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] AdminPaymentTransactionFilterRequest request)
    {
        var result = await _paymentQueryService.SearchAsync(request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>
    /// Payment reconciliation queue (docs/OPEN-FIXES-FEATURES.csv "Payment
    /// reconciliation"): stuck-pending, failed and orphaned Awaiting Payment/
    /// Payment Failed bookings, oldest first - see
    /// <see cref="IAdminPaymentReconciliationService.GetReconciliationAsync"/>.
    /// A static route ahead of <see cref="GetDetail"/>'s
    /// <c>{transactionId:guid}</c> route, same safe-by-construction ordering
    /// as <c>BookingsController.ListUnassignedAtRisk</c> - the guid
    /// constraint means "reconciliation" never matches it.
    /// </summary>
    [HttpGet("reconciliation")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(AdminPaymentReconciliationResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReconciliation([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _reconciliationService.GetReconciliationAsync(page, pageSize);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Full transaction detail: attempts and refunds (SRS 12.13.1, 14.3).</summary>
    [HttpGet("{transactionId:guid}")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(AdminPaymentTransactionDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDetail(Guid transactionId)
    {
        var result = await _paymentQueryService.GetDetailAsync(transactionId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>
    /// Voids a stuck pending payment order (docs/OPEN-FIXES-FEATURES.csv
    /// "Payment reconciliation") - marks OUR record only, no gateway call -
    /// see <see cref="IAdminPaymentReconciliationService.VoidAsync"/>. The
    /// module's one write action, gated separately from the read endpoints
    /// above (<see cref="AdminModules.Payments"/>'s doc comment).
    /// </summary>
    [HttpPost("{transactionId:guid}/void")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(AdminPaymentTransactionListItemResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Void(Guid transactionId, [FromBody] AdminVoidPaymentTransactionRequest? request)
    {
        var result = await _reconciliationService.VoidAsync(transactionId, request?.Reason);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }
}
