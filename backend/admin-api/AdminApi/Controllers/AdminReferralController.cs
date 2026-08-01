using System.IdentityModel.Tokens.Jwt;
using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Nestly.Application.Referral;
using Nestly.BuildingBlocks.Extensions;
using Nestly.Domain;
using Nestly.Infrastructure;

namespace Nestly.AdminApi.Controllers;

/// <summary>
/// Admin referral program management (REFERRAL.md "API SURFACE", tasks 167,
/// 170): the single program config row, milestone tiers (task 174), the
/// referral list/detail view, and the fraud review queue's approve/reject
/// actions (wired straight to the already-built <see cref="IReferralFraudReviewService"/>
/// from task 166 - this controller adds no new fraud business logic, only
/// the admin-facing surface for it). Gated behind "referral.read"/
/// "referral.write" (task 173's <see cref="AdminModules.Referral"/> policy,
/// already generated for every module - see <c>AdminPermissionCatalog</c>).
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/referral")]
[Authorize(AuthenticationSchemes = DependencyInjection.AdminJwtBearerScheme)]
public class AdminReferralController : ControllerBase
{
    private const string ReferralReadPolicy = AdminModules.Referral + ".read";
    private const string ReferralWritePolicy = AdminModules.Referral + ".write";

    private readonly IReferralProgramConfigAdminService _configService;
    private readonly IReferralAdminService _adminService;
    private readonly IReferralFraudReviewService _fraudReviewService;
    private readonly IValidator<ReferralProgramConfigUpdateRequest> _configValidator;
    private readonly IValidator<ReferralMilestoneCreateRequest> _milestoneValidator;
    private readonly IValidator<ReferralAdminSearchRequest> _searchValidator;
    private readonly IValidator<FraudReviewActionRequest> _fraudActionValidator;

    public AdminReferralController(
        IReferralProgramConfigAdminService configService,
        IReferralAdminService adminService,
        IReferralFraudReviewService fraudReviewService,
        IValidator<ReferralProgramConfigUpdateRequest> configValidator,
        IValidator<ReferralMilestoneCreateRequest> milestoneValidator,
        IValidator<ReferralAdminSearchRequest> searchValidator,
        IValidator<FraudReviewActionRequest> fraudActionValidator)
    {
        _configService = configService;
        _adminService = adminService;
        _fraudReviewService = fraudReviewService;
        _configValidator = configValidator;
        _milestoneValidator = milestoneValidator;
        _searchValidator = searchValidator;
        _fraudActionValidator = fraudActionValidator;
    }

    /// <summary>The single referral program config row (task 167).</summary>
    [HttpGet("config")]
    [Authorize(Policy = ReferralReadPolicy)]
    [ProducesResponseType(typeof(ReferralProgramConfigResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetConfig()
    {
        var result = await _configService.GetAsync();
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Updates the referral program config (task 167) - audited via <see cref="Nestly.Application.Abstractions.Auditing.IAuditLogWriter"/>, same shape as <c>SystemSettingsController</c>'s per-group PUT.</summary>
    [HttpPut("config")]
    [Authorize(Policy = ReferralWritePolicy)]
    [ProducesResponseType(typeof(ReferralProgramConfigResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateConfig([FromBody] ReferralProgramConfigUpdateRequest request)
    {
        var validation = await _configValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _configService.UpdateAsync(request, CurrentAdminUserId());
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Every milestone tier, active and inactive (task 174).</summary>
    [HttpGet("milestones")]
    [Authorize(Policy = ReferralReadPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<ReferralMilestoneResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMilestones() => Ok(await _configService.ListMilestonesAsync());

    /// <summary>Creates a new milestone tier (task 174).</summary>
    [HttpPost("milestones")]
    [Authorize(Policy = ReferralWritePolicy)]
    [ProducesResponseType(typeof(ReferralMilestoneResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateMilestone([FromBody] ReferralMilestoneCreateRequest request)
    {
        var validation = await _milestoneValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _configService.CreateMilestoneAsync(request);
        if (!result.IsSuccess)
        {
            return result.ToProblemResult();
        }

        return CreatedAtAction(nameof(ListMilestones), null, result.Value);
    }

    /// <summary>Activates a milestone tier (task 174).</summary>
    [HttpPost("milestones/{milestoneId:guid}/activate")]
    [Authorize(Policy = ReferralWritePolicy)]
    [ProducesResponseType(typeof(ReferralMilestoneResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ActivateMilestone(Guid milestoneId)
    {
        var result = await _configService.SetMilestoneActiveAsync(milestoneId, true);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Deactivates a milestone tier (task 174).</summary>
    [HttpPost("milestones/{milestoneId:guid}/deactivate")]
    [Authorize(Policy = ReferralWritePolicy)]
    [ProducesResponseType(typeof(ReferralMilestoneResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeactivateMilestone(Guid milestoneId)
    {
        var result = await _configService.SetMilestoneActiveAsync(milestoneId, false);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Referral list, filterable by status/fraud flag and searchable by customer (task 170).</summary>
    [HttpGet]
    [Authorize(Policy = ReferralReadPolicy)]
    [ProducesResponseType(typeof(ReferralAdminSearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search(
        [FromQuery] ReferralStatus? status,
        [FromQuery] bool? isFraudFlagged,
        [FromQuery] string? customerSearch,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var request = new ReferralAdminSearchRequest(status, isFraudFlagged, customerSearch, page, pageSize);
        var validation = await _searchValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        return Ok(await _adminService.SearchAsync(request));
    }

    /// <summary>Referral 360 detail view (task 170).</summary>
    [HttpGet("{referralId:guid}")]
    [Authorize(Policy = ReferralReadPolicy)]
    [ProducesResponseType(typeof(ReferralAdminDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid referralId)
    {
        var result = await _adminService.GetByIdAsync(referralId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Confirms a flagged referral's abuse signal was real (task 166/170) - never auto-reverses the reward, see <see cref="IReferralFraudReviewService.ApproveAsync"/>'s doc comment.</summary>
    [HttpPost("{referralId:guid}/fraud/approve")]
    [Authorize(Policy = ReferralWritePolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ApproveFraud(Guid referralId, [FromBody] FraudReviewActionRequest request)
    {
        var validation = await _fraudActionValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _fraudReviewService.ApproveAsync(referralId, CurrentAdminUserId(), request.Note);
        return result.IsSuccess ? Ok() : result.ToProblemResult();
    }

    /// <summary>Rejects a flagged referral as a false positive (task 166/170).</summary>
    [HttpPost("{referralId:guid}/fraud/reject")]
    [Authorize(Policy = ReferralWritePolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RejectFraud(Guid referralId, [FromBody] FraudReviewActionRequest request)
    {
        var validation = await _fraudActionValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _fraudReviewService.RejectAsync(referralId, CurrentAdminUserId(), request.Note);
        return result.IsSuccess ? Ok() : result.ToProblemResult();
    }

    private Guid CurrentAdminUserId() =>
        Guid.Parse(User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value);

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
