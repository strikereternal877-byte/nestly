using System.IdentityModel.Tokens.Jwt;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nestly.Application.Referral;
using Nestly.Application.Reports;
using Nestly.BuildingBlocks.Extensions;
using Nestly.Domain;
using Nestly.Infrastructure;

namespace Nestly.AdminApi.Controllers;

/// <summary>
/// Standard admin reports and the async export queue (SRS 12.18, tasks
/// 128a-128d). Read-only report views and their instant CSV export share
/// "reports.read" - same precedent as <c>ReviewsController</c>'s CSV export,
/// which requires only Read since exporting data an admin can already view
/// is not itself a mutation. Requesting an asynchronous export
/// (<see cref="RequestExport"/>) requires "reports.write" instead: unlike an
/// instant export, it creates a persisted <see cref="ExportJob"/> row and
/// occupies a background worker slot, which is a write-shaped action even
/// though the report content itself is read-only.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/reports")]
[Authorize(AuthenticationSchemes = DependencyInjection.AdminJwtBearerScheme)]
public class ReportsController : ControllerBase
{
    private const string ReportsReadPolicy = AdminModules.Reports + ".read";
    private const string ReportsWritePolicy = AdminModules.Reports + ".write";

    private readonly IReportingQueryService _reportingQueryService;
    private readonly IExportJobService _exportJobService;
    private readonly IReferralAdminService _referralAdminService;

    public ReportsController(
        IReportingQueryService reportingQueryService, IExportJobService exportJobService, IReferralAdminService referralAdminService)
    {
        _reportingQueryService = reportingQueryService;
        _exportJobService = exportJobService;
        _referralAdminService = referralAdminService;
    }

    /// <summary>Booking and revenue report (SRS 12.18.1, task 128a).</summary>
    [HttpGet("booking-revenue")]
    [Authorize(Policy = ReportsReadPolicy)]
    [ProducesResponseType(typeof(BookingRevenueReportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetBookingRevenueReport(
        [FromQuery] DateTime fromUtc, [FromQuery] DateTime toUtc, [FromQuery] string? city, [FromQuery] Guid? categoryId)
    {
        var result = await _reportingQueryService.GetBookingRevenueReportAsync(
            new BookingRevenueReportRequest(fromUtc, toUtc, city, categoryId));
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>CSV export of the booking and revenue report (SRS 12.18.2, task 128a).</summary>
    [HttpGet("booking-revenue/export")]
    [Authorize(Policy = ReportsReadPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ExportBookingRevenueReport(
        [FromQuery] DateTime fromUtc, [FromQuery] DateTime toUtc, [FromQuery] string? city, [FromQuery] Guid? categoryId)
    {
        var result = await _reportingQueryService.ExportBookingRevenueCsvAsync(
            new BookingRevenueReportRequest(fromUtc, toUtc, city, categoryId));
        if (!result.IsSuccess)
        {
            return result.ToProblemResult();
        }

        return File(result.Value, "text/csv", BuildFileName("booking-revenue"));
    }

    /// <summary>Refund report (SRS 12.18.1, task 128b).</summary>
    [HttpGet("refunds")]
    [Authorize(Policy = ReportsReadPolicy)]
    [ProducesResponseType(typeof(RefundReportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetRefundReport([FromQuery] DateTime fromUtc, [FromQuery] DateTime toUtc)
    {
        var result = await _reportingQueryService.GetRefundReportAsync(new RefundReportRequest(fromUtc, toUtc));
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>CSV export of the refund report (SRS 12.18.2, task 128b).</summary>
    [HttpGet("refunds/export")]
    [Authorize(Policy = ReportsReadPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ExportRefundReport([FromQuery] DateTime fromUtc, [FromQuery] DateTime toUtc)
    {
        var result = await _reportingQueryService.ExportRefundCsvAsync(new RefundReportRequest(fromUtc, toUtc));
        if (!result.IsSuccess)
        {
            return result.ToProblemResult();
        }

        return File(result.Value, "text/csv", BuildFileName("refunds"));
    }

    /// <summary>Coupon usage report (SRS 12.18.1, task 128b).</summary>
    [HttpGet("coupon-usage")]
    [Authorize(Policy = ReportsReadPolicy)]
    [ProducesResponseType(typeof(CouponUsageReportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetCouponUsageReport([FromQuery] DateTime fromUtc, [FromQuery] DateTime toUtc)
    {
        var result = await _reportingQueryService.GetCouponUsageReportAsync(new CouponUsageReportRequest(fromUtc, toUtc));
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>CSV export of the coupon usage report (SRS 12.18.2, task 128b).</summary>
    [HttpGet("coupon-usage/export")]
    [Authorize(Policy = ReportsReadPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ExportCouponUsageReport([FromQuery] DateTime fromUtc, [FromQuery] DateTime toUtc)
    {
        var result = await _reportingQueryService.ExportCouponUsageCsvAsync(new CouponUsageReportRequest(fromUtc, toUtc));
        if (!result.IsSuccess)
        {
            return result.ToProblemResult();
        }

        return File(result.Value, "text/csv", BuildFileName("coupon-usage"));
    }

    /// <summary>Customer segmentation report (SRS 12.18.1, task 128c).</summary>
    [HttpGet("customer-segmentation")]
    [Authorize(Policy = ReportsReadPolicy)]
    [ProducesResponseType(typeof(CustomerSegmentationReportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetCustomerSegmentationReport(
        [FromQuery] DateTime? registeredFromUtc, [FromQuery] DateTime? registeredToUtc)
    {
        var result = await _reportingQueryService.GetCustomerSegmentationReportAsync(
            new CustomerSegmentationReportRequest(registeredFromUtc, registeredToUtc));
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>CSV export of the customer segmentation report (SRS 12.18.2, task 128c).</summary>
    [HttpGet("customer-segmentation/export")]
    [Authorize(Policy = ReportsReadPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ExportCustomerSegmentationReport(
        [FromQuery] DateTime? registeredFromUtc, [FromQuery] DateTime? registeredToUtc)
    {
        var result = await _reportingQueryService.ExportCustomerSegmentationCsvAsync(
            new CustomerSegmentationReportRequest(registeredFromUtc, registeredToUtc));
        if (!result.IsSuccess)
        {
            return result.ToProblemResult();
        }

        return File(result.Value, "text/csv", BuildFileName("customer-segmentation"));
    }

    /// <summary>Support ticket volume/resolution-time report (SRS 12.18.1, task 128c).</summary>
    [HttpGet("support-tickets")]
    [Authorize(Policy = ReportsReadPolicy)]
    [ProducesResponseType(typeof(SupportTicketReportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetSupportTicketReport([FromQuery] DateTime fromUtc, [FromQuery] DateTime toUtc)
    {
        var result = await _reportingQueryService.GetSupportTicketReportAsync(new SupportTicketReportRequest(fromUtc, toUtc));
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>CSV export of the support ticket report (SRS 12.18.2, task 128c).</summary>
    [HttpGet("support-tickets/export")]
    [Authorize(Policy = ReportsReadPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ExportSupportTicketReport([FromQuery] DateTime fromUtc, [FromQuery] DateTime toUtc)
    {
        var result = await _reportingQueryService.ExportSupportTicketCsvAsync(new SupportTicketReportRequest(fromUtc, toUtc));
        if (!result.IsSuccess)
        {
            return result.ToProblemResult();
        }

        return File(result.Value, "text/csv", BuildFileName("support-tickets"));
    }

    /// <summary>Referral funnel report - registered/qualified/rewarded counts for the cohort that registered within range (REFERRAL.md "API SURFACE", task 171). Delegates to <see cref="IReferralAdminService"/> rather than duplicating referral-table access here, same "one query service per module, reused rather than re-queried" precedent this controller's other reports already follow.</summary>
    [HttpGet("referral-funnel")]
    [Authorize(Policy = ReportsReadPolicy)]
    [ProducesResponseType(typeof(ReferralFunnelReportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetReferralFunnelReport([FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc)
    {
        var result = await _referralAdminService.GetFunnelReportAsync(fromUtc, toUtc);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Referral program cost report - total wallet-credit/coupon reward cost actually disbursed within range, per-referral and milestone bonuses combined (REFERRAL.md "API SURFACE", task 171).</summary>
    [HttpGet("referral-cost")]
    [Authorize(Policy = ReportsReadPolicy)]
    [ProducesResponseType(typeof(ReferralCostReportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetReferralCostReport([FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc)
    {
        var result = await _referralAdminService.GetCostReportAsync(fromUtc, toUtc);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Requests an asynchronous export (SRS 12.18.2, task 128d) - for a large date range, generated by a background job rather than this request.</summary>
    [HttpPost("exports")]
    [Authorize(Policy = ReportsWritePolicy)]
    [ProducesResponseType(typeof(ExportJobStatusResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RequestExport([FromBody] RequestExportJobRequest request)
    {
        var result = await _exportJobService.RequestExportAsync(request, CurrentAdminUserId());
        if (!result.IsSuccess)
        {
            return result.ToProblemResult();
        }

        return AcceptedAtAction(nameof(GetExportStatus), new { jobId = result.Value.Id }, result.Value);
    }

    /// <summary>Every export job the calling admin has requested, newest first (task 128d).</summary>
    [HttpGet("exports")]
    [Authorize(Policy = ReportsReadPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<ExportJobStatusResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMyExports()
    {
        var result = await _exportJobService.ListMineAsync(CurrentAdminUserId());
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Polls one export job's status (task 128d).</summary>
    [HttpGet("exports/{jobId:guid}")]
    [Authorize(Policy = ReportsReadPolicy)]
    [ProducesResponseType(typeof(ExportJobStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetExportStatus(Guid jobId)
    {
        var result = await _exportJobService.GetStatusAsync(jobId, CurrentAdminUserId());
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Downloads a completed export's CSV (task 128d).</summary>
    [HttpGet("exports/{jobId:guid}/download")]
    [Authorize(Policy = ReportsReadPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> DownloadExport(Guid jobId)
    {
        var result = await _exportJobService.DownloadAsync(jobId, CurrentAdminUserId());
        if (!result.IsSuccess)
        {
            return result.ToProblemResult();
        }

        return File(result.Value.Content, "text/csv", result.Value.FileName);
    }

    private Guid CurrentAdminUserId() =>
        Guid.Parse(User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value);

    private static string BuildFileName(string reportSlug) =>
        $"{reportSlug}-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
}
