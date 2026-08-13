using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Nestly.Application.Bookings;
using Nestly.Application.ProviderJobs;
using Nestly.Application.Storage;
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
    private readonly IProviderLocationIngestService _locationIngestService;
    private readonly IValidator<RejectJobRequest> _rejectValidator;
    private readonly IValidator<UploadJobCompletionProofRequest> _completionProofValidator;
    private readonly IValidator<SubmitCompletionProofRequest> _submitCompletionProofValidator;

    public JobsController(
        IProviderJobService jobService,
        IProviderLocationIngestService locationIngestService,
        IValidator<RejectJobRequest> rejectValidator,
        IValidator<UploadJobCompletionProofRequest> completionProofValidator,
        IValidator<SubmitCompletionProofRequest> submitCompletionProofValidator)
    {
        _jobService = jobService;
        _locationIngestService = locationIngestService;
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

    /// <summary>
    /// Mark an accepted job as en route - the provider has set off for the
    /// customer's address (task 270). Optional: <see cref="Start"/> still works
    /// straight from an accepted job, so a provider who never taps this is not
    /// blocked. Re-tapping while already en route answers 200 with the
    /// unchanged job rather than a conflict, so a client retrying over a bad
    /// connection is not punished for it.
    /// </summary>
    [HttpPost("{bookingId:guid}/en-route")]
    [ProducesResponseType(typeof(ProviderJobDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> MarkEnRoute(Guid bookingId)
    {
        var result = await _jobService.MarkEnRouteAsync(CurrentProviderId(), bookingId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>
    /// Mark an en-route job as arrived - the provider has reached the address
    /// but has not begun the work (task 270). Idempotent on a re-tap, same as
    /// <see cref="MarkEnRoute"/>.
    /// </summary>
    [HttpPost("{bookingId:guid}/arrived")]
    [ProducesResponseType(typeof(ProviderJobDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> MarkArrived(Guid bookingId)
    {
        var result = await _jobService.MarkArrivedAsync(CurrentProviderId(), bookingId);
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

    /// <summary>
    /// Uploads one camera/gallery photo for job-completion evidence and
    /// returns a ref to feed into <see cref="SubmitCompletionVerification"/>'s
    /// <c>photoRefs</c>. Validated here rather than via a FluentValidation
    /// record validator since the payload is a multipart file, not JSON:
    /// content-type is checked against an image allowlist and size is capped
    /// before anything is read into memory or written to disk - both real
    /// trust-boundary checks (SRS "never trust client data"), not just
    /// yak-shaving, since a client can lie about either.
    /// </summary>
    [HttpPost("{bookingId:guid}/completion-photos")]
    [ProducesResponseType(typeof(UploadCompletionPhotoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [RequestSizeLimit(MaxCompletionPhotoBytes)]
    public async Task<IActionResult> UploadCompletionPhoto(Guid bookingId, IFormFile file)
    {
        if (file is null || file.Length == 0)
        {
            return Problem("A photo file is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (file.Length > MaxCompletionPhotoBytes)
        {
            return Problem($"Photos must be {MaxCompletionPhotoBytes / (1024 * 1024)}MB or smaller.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!AllowedPhotoContentTypes.Contains(file.ContentType))
        {
            return Problem("Only JPEG, PNG, or WebP photos are accepted.", statusCode: StatusCodes.Status400BadRequest);
        }

        await using var stream = file.OpenReadStream();
        var result = await _jobService.UploadCompletionPhotoAsync(CurrentProviderId(), bookingId, stream, file.FileName, file.ContentType);
        if (result.IsFailure)
        {
            return result.ToProblemResult();
        }

        // The service's ref may already be absolute (Supabase) or relative
        // to this API's own origin (local disk) - resolved here so it's
        // directly usable as an <img src> by provider-web today and, per
        // BookingCompletionProofResponse's doc comment, by admin-web/
        // customer-web once they render it too.
        var absoluteRef = FileReferenceUrl.ToAbsolute(result.Value.PhotoRef, Request.Scheme, Request.Host.ToString());
        return Ok(new UploadCompletionPhotoResponse(absoluteRef));
    }

    private const long MaxCompletionPhotoBytes = 8 * 1024 * 1024;

    private static readonly HashSet<string> AllowedPhotoContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
    };

    /// <summary>
    /// Report the provider's current position for a job in flight (task 269).
    /// Fails closed: 403 unless the caller is the provider on this booking's
    /// live assignment, 409 unless the job has been accepted and the booking
    /// is still in a trackable state - so no position is ever collected before
    /// the provider accepts or after the job ends. Accepted fixes answer 200;
    /// fixes dropped by the per-booking throttle answer 202, since the client
    /// did nothing wrong and must not retry them.
    /// </summary>
    [HttpPost("{bookingId:guid}/location")]
    [ProducesResponseType(typeof(RecordProviderLocationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RecordProviderLocationResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RecordLocation(Guid bookingId, [FromBody] RecordProviderLocationRequest request)
    {
        var result = await _locationIngestService.RecordAsync(CurrentProviderId(), bookingId, request);
        if (result.IsFailure)
        {
            return result.ToProblemResult();
        }

        return result.Value.Accepted
            ? Ok(result.Value)
            : StatusCode(StatusCodes.Status202Accepted, result.Value);
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
