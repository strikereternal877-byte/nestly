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
/// Admin provider directory management (PROVIDER.md API surface "Admin-Facing
/// Additions": Provider CRUD, KYC approval, performance; tasks 150a-150c,
/// 160). Read-only actions require "provider.read"; profile/status/KYC/
/// background-check mutations require "provider.write" - manual bank-transfer
/// earning adjustments are gated "payout.write" instead (see the earnings
/// endpoints below), matching the Provider/Payout RBAC split in PROVIDER.md's
/// RBAC ADDITIONS section.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/providers")]
[Authorize(AuthenticationSchemes = DependencyInjection.AdminJwtBearerScheme)]
public class ProvidersController : ControllerBase
{
    private const string ReadPolicy = AdminModules.Provider + ".read";
    private const string WritePolicy = AdminModules.Provider + ".write";
    private const string PayoutWritePolicy = AdminModules.Payout + ".write";

    private readonly IProviderManagementService _providerManagementService;
    private readonly IProviderKycApprovalService _kycApprovalService;
    private readonly IProviderEarningLedgerService _earningLedgerService;
    private readonly IProviderPhotoModerationService _photoModerationService;
    private readonly IValidator<ProviderSearchRequest> _searchValidator;
    private readonly IValidator<CreateProviderRequest> _createValidator;
    private readonly IValidator<UpdateProviderRequest> _updateValidator;
    private readonly IValidator<SuspendProviderRequest> _suspendValidator;
    private readonly IValidator<RejectProviderKycDocumentRequest> _rejectKycValidator;
    private readonly IValidator<RejectProviderPhotoRequest> _rejectPhotoValidator;
    private readonly IValidator<RecordBackgroundCheckRequest> _backgroundCheckValidator;
    private readonly IValidator<RecordProviderEarningAdjustmentRequest> _earningAdjustmentValidator;
    private readonly IValidator<SetProviderCapacityRequest> _setCapacityValidator;
    private readonly IValidator<ProviderPerformanceListRequest> _performanceListValidator;

    public ProvidersController(
        IProviderManagementService providerManagementService,
        IProviderKycApprovalService kycApprovalService,
        IProviderEarningLedgerService earningLedgerService,
        IProviderPhotoModerationService photoModerationService,
        IValidator<ProviderSearchRequest> searchValidator,
        IValidator<CreateProviderRequest> createValidator,
        IValidator<UpdateProviderRequest> updateValidator,
        IValidator<SuspendProviderRequest> suspendValidator,
        IValidator<RejectProviderKycDocumentRequest> rejectKycValidator,
        IValidator<RejectProviderPhotoRequest> rejectPhotoValidator,
        IValidator<RecordBackgroundCheckRequest> backgroundCheckValidator,
        IValidator<RecordProviderEarningAdjustmentRequest> earningAdjustmentValidator,
        IValidator<SetProviderCapacityRequest> setCapacityValidator,
        IValidator<ProviderPerformanceListRequest> performanceListValidator)
    {
        _providerManagementService = providerManagementService;
        _kycApprovalService = kycApprovalService;
        _earningLedgerService = earningLedgerService;
        _photoModerationService = photoModerationService;
        _searchValidator = searchValidator;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _suspendValidator = suspendValidator;
        _rejectKycValidator = rejectKycValidator;
        _rejectPhotoValidator = rejectPhotoValidator;
        _backgroundCheckValidator = backgroundCheckValidator;
        _earningAdjustmentValidator = earningAdjustmentValidator;
        _setCapacityValidator = setCapacityValidator;
        _performanceListValidator = performanceListValidator;
    }

    // ---- CRUD (task 150a) ----

    /// <summary>Search/filter providers (task 150a).</summary>
    [HttpGet]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(ProviderSearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search(
        [FromQuery] string? name,
        [FromQuery] string? phone,
        [FromQuery] ProviderStatus? status,
        [FromQuery] ProviderOnboardingStatus? onboardingStatus,
        [FromQuery] Guid? cityId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var request = new ProviderSearchRequest(name, phone, status, onboardingStatus, cityId, page, pageSize);
        var validation = await _searchValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _providerManagementService.SearchAsync(request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Provider detail: profile, KYC documents, background check history (task 150a/150b).</summary>
    [HttpGet("{providerId:guid}")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(ProviderDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDetail(Guid providerId)
    {
        var result = await _providerManagementService.GetDetailAsync(providerId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Admin-created provider record (task 150a). ProviderType is always Individual - OPEN DECISIONS #2.</summary>
    [HttpPost]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(ProviderDetailResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateProviderRequest request)
    {
        var validation = await _createValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _providerManagementService.CreateAsync(request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Updates a provider's profile (task 150a).</summary>
    [HttpPut("{providerId:guid}")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(ProviderDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid providerId, [FromBody] UpdateProviderRequest request)
    {
        var validation = await _updateValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _providerManagementService.UpdateAsync(providerId, request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Suspends a provider's account (task 150a, PROVIDER.md RBAC ADDITIONS "Suspend").</summary>
    [HttpPost("{providerId:guid}/suspend")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(ProviderDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Suspend(Guid providerId, [FromBody] SuspendProviderRequest request)
    {
        var validation = await _suspendValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _providerManagementService.SuspendAsync(providerId, request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Reactivates a previously suspended provider (task 150a).</summary>
    [HttpPost("{providerId:guid}/reactivate")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(ProviderDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Reactivate(Guid providerId)
    {
        var result = await _providerManagementService.ReactivateAsync(providerId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>
    /// Deletes a provider's account (right-to-erasure request handled on the
    /// provider's behalf by support). Terminal and irreversible - unlike
    /// Suspend/Reactivate there is no "undelete" endpoint.
    /// </summary>
    [HttpPost("{providerId:guid}/delete")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(ProviderDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Delete(Guid providerId)
    {
        var result = await _providerManagementService.DeleteAsync(providerId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    // ---- Profile photo moderation (task 293) ----

    /// <summary>
    /// The photo-moderation queue (task 293): every provider whose profile
    /// photo is awaiting a verdict. A provider photo is user-supplied content
    /// shown to customers, so it goes through the same admin gate this API
    /// already applies to KYC documents and review text - it is not published
    /// on upload.
    /// </summary>
    [HttpGet("photo-moderation/pending")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<ProviderPhotoResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListPendingPhotos(CancellationToken cancellationToken) =>
        Ok(await _photoModerationService.ListPendingAsync(cancellationToken));

    /// <summary>Approves a provider's profile photo - the only transition that makes it visible to customers (task 293).</summary>
    [HttpPost("{providerId:guid}/photo/approve")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(ProviderPhotoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ApprovePhoto(Guid providerId, CancellationToken cancellationToken)
    {
        var result = await _photoModerationService.ApproveAsync(providerId, CurrentAdminUserId(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Rejects a provider's profile photo (task 293). The reason is shown back to the provider so a rejection is actionable.</summary>
    [HttpPost("{providerId:guid}/photo/reject")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(ProviderPhotoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RejectPhoto(Guid providerId, [FromBody] RejectProviderPhotoRequest request, CancellationToken cancellationToken)
    {
        var validation = await _rejectPhotoValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _photoModerationService.RejectAsync(providerId, CurrentAdminUserId(), request, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    // ---- KYC approval and background check / activation (task 150b, 160) ----

    /// <summary>Approves a submitted KYC document (task 150b, the admin-side counterpart to task 146c's submission flow).</summary>
    [HttpPost("kyc-documents/{documentId:guid}/approve")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(ProviderKycDocumentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ApproveKycDocument(Guid documentId)
    {
        var result = await _kycApprovalService.ApproveDocumentAsync(documentId, CurrentAdminUserId());
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Rejects a submitted KYC document (task 150b).</summary>
    [HttpPost("kyc-documents/{documentId:guid}/reject")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(ProviderKycDocumentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RejectKycDocument(Guid documentId, [FromBody] RejectProviderKycDocumentRequest request)
    {
        var validation = await _rejectKycValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _kycApprovalService.RejectDocumentAsync(documentId, CurrentAdminUserId(), request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Records a background/reference check outcome (task 160) - a distinct step from KYC document validation.</summary>
    [HttpPost("{providerId:guid}/background-check")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(ProviderBackgroundCheckResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RecordBackgroundCheck(Guid providerId, [FromBody] RecordBackgroundCheckRequest request)
    {
        var validation = await _backgroundCheckValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _kycApprovalService.RecordBackgroundCheckAsync(providerId, CurrentAdminUserId(), request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Activates a provider once KYC is approved and the background check has passed (task 160's gate).</summary>
    [HttpPost("{providerId:guid}/activate")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(ProviderDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Activate(Guid providerId)
    {
        var result = await _kycApprovalService.ActivateAsync(providerId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    // ---- Capacity limits (task 245 built enforcement; task 308 adds this write path) ----

    /// <summary>
    /// A provider's dispatch capacity limits (task 245/308). Hard-enforced by
    /// the automatic-assignment engine; still only an advisory load signal
    /// on manual admin assignment (PROVIDER.md OPEN DECISIONS - AUTOMATIC
    /// ASSIGNMENT #2). Unlimited (both null) until an admin sets one below.
    /// </summary>
    [HttpGet("{providerId:guid}/capacity")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(ProviderCapacityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCapacity(Guid providerId)
    {
        var result = await _providerManagementService.GetCapacityAsync(providerId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>
    /// Sets (or clears, via null) a provider's <c>MaxJobsPerDay</c>/<c>MaxJobsPerSlot</c>
    /// (task 308). Full-overwrite, same PUT-style convention as <see cref="Update"/>.
    /// </summary>
    [HttpPut("{providerId:guid}/capacity")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(ProviderCapacityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetCapacity(Guid providerId, [FromBody] SetProviderCapacityRequest request)
    {
        var validation = await _setCapacityValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _providerManagementService.SetCapacityAsync(providerId, request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    // ---- Performance (task 150c; ranking list added for docs/OPEN-FIXES-FEATURES.csv "Provider performance") ----

    /// <summary>
    /// The provider-performance ranking list (docs/OPEN-FIXES-FEATURES.csv
    /// "Provider performance"): offers received, acceptance rate, average
    /// response time, completion rate and average rating, per provider, over
    /// a rolling window (default 30 days) - sortable by any of those columns.
    /// A static route ahead of <see cref="GetPerformance"/>'s
    /// <c>{providerId:guid}/performance</c> route, same non-clash reasoning
    /// as <see cref="GetFulfilmentBoard"/> in BookingsController (the guid
    /// constraint there never matches the literal "performance" segment
    /// here either way, but the static route reads clearer listed first).
    /// </summary>
    [HttpGet("performance")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(ProviderPerformanceListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListPerformance(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] int periodDays = 30,
        [FromQuery] ProviderPerformanceSortField sortBy = ProviderPerformanceSortField.OffersReceived,
        [FromQuery] bool sortDescending = true)
    {
        var request = new ProviderPerformanceListRequest(page, pageSize, periodDays, sortBy, sortDescending);
        var validation = await _performanceListValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _providerManagementService.ListPerformanceAsync(request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Single-provider job-fulfilment performance summary, all-time (PROVIDER.md API surface "get provider performance metrics", task 150c).</summary>
    [HttpGet("{providerId:guid}/performance")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(ProviderPerformanceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPerformance(Guid providerId)
    {
        var result = await _providerManagementService.GetPerformanceAsync(providerId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    // ---- Earnings ledger (task 148) ----

    /// <summary>A provider's earning ledger and current balance (task 148).</summary>
    [HttpGet("{providerId:guid}/earnings")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(ProviderEarningsSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEarnings(Guid providerId)
    {
        var result = await _earningLedgerService.GetSummaryAsync(providerId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>
    /// Records a manual credit/debit adjustment to a provider's earning
    /// ledger (task 148 - "credit per completed job ... debit for
    /// penalties"). Gated "payout.write" rather than "provider.write" - this
    /// is a financial-ledger mutation, the same RBAC tier as processing a
    /// payout, not a provider-profile edit.
    /// </summary>
    [HttpPost("{providerId:guid}/earnings")]
    [Authorize(Policy = PayoutWritePolicy)]
    [ProducesResponseType(typeof(ProviderEarningLedgerEntryResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RecordEarningAdjustment(Guid providerId, [FromBody] RecordProviderEarningAdjustmentRequest request)
    {
        var validation = await _earningAdjustmentValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _earningLedgerService.RecordAdjustmentAsync(providerId, request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    private Guid CurrentAdminUserId() =>
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
