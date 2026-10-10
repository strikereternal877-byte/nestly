using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Nestly.Application.ProviderSupport;
using Nestly.BuildingBlocks.Extensions;
using Nestly.Domain;
using Nestly.Infrastructure;

namespace Nestly.AdminApi.Controllers;

/// <summary>
/// Admin workflow over provider support tickets (Provider Management UX
/// pass) - search/detail across every provider, reply, resolve. Gated behind
/// the same "support.read"/"support.write" modules as the customer
/// <see cref="SupportTicketsController"/>: this is the same admin support
/// function, just for the provider side of the marketplace rather than
/// duplicated permission plumbing for what is conceptually one queue.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/provider-support-tickets")]
[Authorize(AuthenticationSchemes = DependencyInjection.AdminJwtBearerScheme)]
public class ProviderSupportTicketsController : ControllerBase
{
    private const string ReadPolicy = AdminModules.Support + ".read";
    private const string WritePolicy = AdminModules.Support + ".write";

    private readonly IAdminProviderSupportTicketService _service;
    private readonly IValidator<AdminProviderSupportTicketSearchRequest> _searchValidator;
    private readonly IValidator<AddProviderSupportTicketCommentRequest> _replyValidator;
    private readonly IValidator<ResolveProviderSupportTicketRequest> _resolveValidator;

    public ProviderSupportTicketsController(
        IAdminProviderSupportTicketService service,
        IValidator<AdminProviderSupportTicketSearchRequest> searchValidator,
        IValidator<AddProviderSupportTicketCommentRequest> replyValidator,
        IValidator<ResolveProviderSupportTicketRequest> resolveValidator)
    {
        _service = service;
        _searchValidator = searchValidator;
        _replyValidator = replyValidator;
        _resolveValidator = resolveValidator;
    }

    /// <summary>Filtered/paginated ticket search across every provider.</summary>
    [HttpGet]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(AdminProviderSupportTicketSearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search(
        [FromQuery] Guid? providerId,
        [FromQuery] ProviderSupportTicketCategory? category,
        [FromQuery] ProviderSupportTicketStatus? status,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var request = new AdminProviderSupportTicketSearchRequest(providerId, category, status, fromUtc, toUtc, page, pageSize);

        var validation = await _searchValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _service.SearchAsync(request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Full ticket detail - comment thread and provider display name.</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = ReadPolicy)]
    [ProducesResponseType(typeof(AdminProviderSupportTicketDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await _service.GetDetailAsync(id);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Appends an admin reply to the ticket's thread. A still-Open ticket moves to InProgress.</summary>
    [HttpPost("{id:guid}/reply")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(AdminProviderSupportTicketDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reply(Guid id, [FromBody] AddProviderSupportTicketCommentRequest request)
    {
        var validation = await _replyValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _service.ReplyAsync(id, request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Moves the ticket to Resolved.</summary>
    [HttpPost("{id:guid}/resolve")]
    [Authorize(Policy = WritePolicy)]
    [ProducesResponseType(typeof(AdminProviderSupportTicketDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Resolve(Guid id, [FromBody] ResolveProviderSupportTicketRequest request)
    {
        var validation = await _resolveValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _service.ResolveAsync(id, request);
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
