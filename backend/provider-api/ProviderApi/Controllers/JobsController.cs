using System.IdentityModel.Tokens.Jwt;
using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Nestly.Application.Bookings;
using Nestly.Application.ProviderJobs;
using Nestly.BuildingBlocks.Extensions;
using Nestly.Infrastructure;

namespace Nestly.ProviderApi.Controllers;

/// <summary>
/// Provider jobs (task 149a, PROVIDER.md API surface "Jobs" - list/detail,
/// accept/reject/start/complete, completion proof upload), wired to a real
/// <see cref="IProviderJobService"/> backed by the <c>BookingProviderAssignment</c>
/// bridge entity (task 147). Every action is scoped to the caller's own
/// provider id taken from the JWT - there is no route or body parameter that
/// could name a different provider (SRS 28.3 IDOR), same pattern as
/// <see cref="ProfileController"/>/<see cref="AvailabilityController"/>.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Authorize(AuthenticationSchemes = DependencyInjection.ProviderJwtBearerScheme)]
[Route("api/v{version:apiVersion}/jobs")]
public class JobsController : ControllerBase
{
    private readonly IProviderJobService _jobService;
    private readonly IValidator<RejectJobRequest> _rejectValidator;
    private readonly IValidator<UploadJobCompletionProofRequest> _completionProofValidator;
    private readonly IValidator<SubmitCompletionProofRequest> _submitCompletionProofValidator;

    public JobsController(
        IProviderJobService jobService,
        IValidator<RejectJobRequest> rejectValidator,
        IValidator<UploadJobCompletionProofRequest> completionProofValidator,
        IValidator<SubmitCompletionProofRequest> submitCompletionProofValidator)
    {
        _jobService = jobService;
        _rejectValidator = rejectValidator;
        _completionProofValidator = completionProofValidator;
        _submitCompletionProofValidator = submitCompletionProofValidator;
    }

    /// <summary>List jobs ever assigned to the caller, optionally filtered by status and/or slot date.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ProviderJobSearchResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] ProviderJobStatus? status, [FromQuery] DateOnly? date)
    {
        var result = await _jobService.ListAsync(CurrentProviderId(), status, date);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Get one job's detail.</summary>
    [HttpGet("{bookingId:guid}")]
    [ProducesResponseType(typeof(ProviderJobDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDetail(Guid bookingId)
    {
        var result = await _jobService.GetDetailAsync(CurrentProviderId(), bookingId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Accept an assigned job.</summary>
    [HttpPost("{bookingId:guid}/accept")]
    [ProducesResponseType(typeof(ProviderJobDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Accept(Guid bookingId)
    {
        var result = await _jobService.AcceptAsync(CurrentProviderId(), bookingId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Reject an assigned job (task 159 - returns the booking to the assignable pool for admin reassignment).</summary>
    [HttpPost("{bookingId:guid}/reject")]
    [ProducesResponseType(typeof(ProviderJobDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Reject(Guid bookingId, [FromBody] RejectJobRequest request)
    {
        var validation = await _rejectValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _jobService.RejectAsync(CurrentProviderId(), bookingId, request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Mark an accepted job as started (provider has arrived / begun work).</summary>
    [HttpPost("{bookingId:guid}/start")]
    [ProducesResponseType(typeof(ProviderJobDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Start(Guid bookingId)
    {
        var result = await _jobService.StartAsync(CurrentProviderId(), bookingId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Mark an in-progress job as completed.</summary>
    [HttpPost("{bookingId:guid}/complete")]
    [ProducesResponseType(typeof(ProviderJobDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Complete(Guid bookingId)
    {
        var result = await _jobService.CompleteAsync(CurrentProviderId(), bookingId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Attach completion proof (photo/file reference) to a job.</summary>
    [HttpPost("{bookingId:guid}/completion-proof")]
    [ProducesResponseType(typeof(ProviderJobDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UploadCompletionProof(Guid bookingId, [FromBody] UploadJobCompletionProofRequest request)
    {
        var validation = await _completionProofValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _jobService.UploadCompletionProofAsync(CurrentProviderId(), bookingId, request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>
    /// Submits (or resubmits) the completion evidence - photos plus
    /// checklist - required before <see cref="Complete"/> will succeed
    /// (tasks 195-197). Distinct from <see cref="UploadCompletionProof"/>'s
    /// single legacy proof-ref field.
    /// </summary>
    [HttpPost("{bookingId:guid}/completion-verification")]
    [ProducesResponseType(typeof(BookingCompletionProofResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SubmitCompletionVerification(Guid bookingId, [FromBody] SubmitCompletionProofRequest request)
    {
        var validation = await _submitCompletionProofValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _jobService.SubmitCompletionProofAsync(CurrentProviderId(), bookingId, request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>The completion evidence submitted for this job, if any (task 198).</summary>
    [HttpGet("{bookingId:guid}/completion-verification")]
    [ProducesResponseType(typeof(BookingCompletionProofResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCompletionVerification(Guid bookingId)
    {
        var result = await _jobService.GetCompletionProofAsync(CurrentProviderId(), bookingId);
        if (result.IsFailure)
        {
            return result.ToProblemResult();
        }

        return result.Value is null ? NoContent() : Ok(result.Value);
    }

    private Guid CurrentProviderId() =>
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
