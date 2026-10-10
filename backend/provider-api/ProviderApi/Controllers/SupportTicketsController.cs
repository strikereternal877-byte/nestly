using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Nestly.Application.ProviderSupport;
using Nestly.BuildingBlocks.Extensions;
using Nestly.Infrastructure;

namespace Nestly.ProviderApi.Controllers;

/// <summary>
/// Provider support tickets (Provider Management UX pass) - mirrors
/// consumer-api's <c>SupportTicketsController</c>, scoped to the caller's own
/// provider id from the JWT.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Authorize(AuthenticationSchemes = DependencyInjection.ProviderJwtBearerScheme)]
[Route("api/v{version:apiVersion}/support-tickets")]
public class SupportTicketsController : ControllerBase
{
    private readonly IProviderSupportTicketService _supportTicketService;
    private readonly IValidator<CreateProviderSupportTicketRequest> _createValidator;
    private readonly IValidator<AddProviderSupportTicketCommentRequest> _commentValidator;

    public SupportTicketsController(
        IProviderSupportTicketService supportTicketService,
        IValidator<CreateProviderSupportTicketRequest> createValidator,
        IValidator<AddProviderSupportTicketCommentRequest> commentValidator)
    {
        _supportTicketService = supportTicketService;
        _createValidator = createValidator;
        _commentValidator = commentValidator;
    }

    /// <summary>Raises a new ticket.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ProviderSupportTicketDetailResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateProviderSupportTicketRequest request)
    {
        var validation = await _createValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _supportTicketService.CreateAsync(CurrentProviderId(), request);
        return result.IsSuccess ? CreatedAtAction(nameof(Detail), new { id = result.Value.Id }, result.Value) : result.ToProblemResult();
    }

    /// <summary>Lists all of the caller's tickets, newest first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ProviderSupportTicketSummaryResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List()
    {
        var result = await _supportTicketService.ListAsync(CurrentProviderId());
        return Ok(result.Value);
    }

    /// <summary>Ticket detail with its full comment thread.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ProviderSupportTicketDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Detail(Guid id)
    {
        var result = await _supportTicketService.GetDetailAsync(CurrentProviderId(), id);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Appends a provider follow-up to the ticket's thread.</summary>
    [HttpPost("{id:guid}/comments")]
    [ProducesResponseType(typeof(ProviderSupportTicketDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddComment(Guid id, [FromBody] AddProviderSupportTicketCommentRequest request)
    {
        var validation = await _commentValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _supportTicketService.AddCommentAsync(CurrentProviderId(), id, request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    private Guid CurrentProviderId() =>
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
