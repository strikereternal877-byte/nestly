using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Nestly.Application;
using Nestly.Application.Customers;
using Nestly.BuildingBlocks.Extensions;
using Nestly.Domain;
using Nestly.Infrastructure;

namespace Nestly.AdminApi.Controllers;

/// <summary>
/// Admin customer management (SRS 12.4, tasks 101a-101d): search/filter,
/// the 360 detail view, block/unblock, and internal notes. Read-only actions
/// require "customers.read"; every mutating action requires "customers.write"
/// (task 96b/96c) - a role granted Write always also holds Read (see
/// <c>AdminPermissionCatalog</c>), so the two are applied per-action rather
/// than a single class-level policy.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/customers")]
[Authorize(AuthenticationSchemes = Nestly.Infrastructure.DependencyInjection.AdminJwtBearerScheme)]
public class CustomersController : ControllerBase
{
    private const string CustomersReadPolicy = AdminModules.Customers + ".read";
    private const string CustomersWritePolicy = AdminModules.Customers + ".write";

    private readonly ICustomerManagementService _customerManagementService;
    private readonly IValidator<CustomerSearchRequest> _searchValidator;
    private readonly IValidator<BlockCustomerRequest> _blockValidator;
    private readonly IValidator<AddCustomerNoteRequest> _addNoteValidator;
    private readonly IValidator<CustomerAnalyticsRequest> _analyticsValidator;
    private readonly IValidator<AdjustCustomerWalletRequest> _adjustWalletValidator;

    public CustomersController(
        ICustomerManagementService customerManagementService,
        IValidator<CustomerSearchRequest> searchValidator,
        IValidator<BlockCustomerRequest> blockValidator,
        IValidator<AddCustomerNoteRequest> addNoteValidator,
        IValidator<CustomerAnalyticsRequest> analyticsValidator,
        IValidator<AdjustCustomerWalletRequest> adjustWalletValidator)
    {
        _customerManagementService = customerManagementService;
        _searchValidator = searchValidator;
        _blockValidator = blockValidator;
        _addNoteValidator = addNoteValidator;
        _analyticsValidator = analyticsValidator;
        _adjustWalletValidator = adjustWalletValidator;
    }

    /// <summary>Search/filter customers (SRS 12.4.1, task 101a).</summary>
    [HttpGet]
    [Authorize(Policy = CustomersReadPolicy)]
    [ProducesResponseType(typeof(CustomerSearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search(
        [FromQuery] string? name,
        [FromQuery] string? mobile,
        [FromQuery] string? email,
        [FromQuery] string? city,
        [FromQuery] CustomerStatus? status,
        [FromQuery] DateTime? registeredFromUtc,
        [FromQuery] DateTime? registeredToUtc,
        [FromQuery] int? minBookingCount,
        [FromQuery] int? maxBookingCount,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var request = new CustomerSearchRequest(
            name, mobile, email, city, status, registeredFromUtc, registeredToUtc,
            minBookingCount, maxBookingCount, page, pageSize);

        var validation = await _searchValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _customerManagementService.SearchAsync(request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>
    /// Customer Analytics dashboard (Admin Web new page): total/active/
    /// blocked/unverified/deleted counts, new-today/7-day/trend-window
    /// registration counts, the acquisition-vs-activation booking funnel,
    /// a daily registration-trend series and a top-cities breakdown - see
    /// <see cref="CustomerAnalyticsResponse"/>'s doc comment for exactly
    /// what each field means. Static route declared ahead of
    /// <see cref="GetDetail"/>'s <c>{customerId:guid}</c> route, same
    /// non-clash reasoning as <see cref="ProvidersController.ListPerformance"/>'s own precedent.
    /// </summary>
    [HttpGet("analytics")]
    [Authorize(Policy = CustomersReadPolicy)]
    [ProducesResponseType(typeof(CustomerAnalyticsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetAnalytics([FromQuery] int trendDays = 30)
    {
        var request = new CustomerAnalyticsRequest(trendDays);
        var validation = await _analyticsValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _customerManagementService.GetAnalyticsAsync(request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Customer 360 view - profile, addresses, bookings, wallet, coupons, tickets, notes (SRS 12.4.2, task 101b).</summary>
    [HttpGet("{customerId:guid}")]
    [Authorize(Policy = CustomersReadPolicy)]
    [ProducesResponseType(typeof(CustomerDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDetail(Guid customerId)
    {
        var result = await _customerManagementService.GetDetailAsync(customerId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Blocks a customer's account (SRS 12.4.3, task 101c).</summary>
    [HttpPost("{customerId:guid}/block")]
    [Authorize(Policy = CustomersWritePolicy)]
    [ProducesResponseType(typeof(CustomerDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Block(Guid customerId, [FromBody] BlockCustomerRequest request)
    {
        var validation = await _blockValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _customerManagementService.BlockAsync(customerId, CurrentAdminUserId(), request.Reason);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Restores a blocked customer's account (SRS 12.4.3, task 101c).</summary>
    [HttpPost("{customerId:guid}/unblock")]
    [Authorize(Policy = CustomersWritePolicy)]
    [ProducesResponseType(typeof(CustomerDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Unblock(Guid customerId)
    {
        var result = await _customerManagementService.UnblockAsync(customerId, CurrentAdminUserId());
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>
    /// Deletes a customer's account (right-to-erasure request handled on the
    /// customer's behalf by support). Terminal and irreversible - unlike
    /// Block/Unblock there is no "undelete" endpoint.
    /// </summary>
    [HttpPost("{customerId:guid}/delete")]
    [Authorize(Policy = CustomersWritePolicy)]
    [ProducesResponseType(typeof(CustomerDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Delete(Guid customerId, [FromBody] BlockCustomerRequest request)
    {
        var validation = await _blockValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _customerManagementService.DeleteAsync(customerId, CurrentAdminUserId(), request.Reason);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>
    /// Records a manual wallet credit/debit (SRS 12.4.3 gap: the wallet tab
    /// was read-only) - a goodwill credit or a correction, for when none of
    /// the wallet's normal sources (refund, coupon, referral, Nestly Coins)
    /// apply. A debit fails with a business error rather than letting the
    /// balance go negative (see <see cref="Nestly.Application.Wallet.IWalletService.DebitAsync"/>).
    /// </summary>
    [HttpPost("{customerId:guid}/wallet/adjust")]
    [Authorize(Policy = CustomersWritePolicy)]
    [ProducesResponseType(typeof(CustomerDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> AdjustWallet(Guid customerId, [FromBody] AdjustCustomerWalletRequest request)
    {
        var validation = await _adjustWalletValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _customerManagementService.AdjustWalletAsync(customerId, CurrentAdminUserId(), request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Adds an internal note to a customer's record (SRS 12.4.3, task 101d).</summary>
    [HttpPost("{customerId:guid}/notes")]
    [Authorize(Policy = CustomersWritePolicy)]
    [ProducesResponseType(typeof(CustomerNoteResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddNote(Guid customerId, [FromBody] AddCustomerNoteRequest request)
    {
        var validation = await _addNoteValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _customerManagementService.AddNoteAsync(customerId, CurrentAdminUserId(), request.Note);
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
