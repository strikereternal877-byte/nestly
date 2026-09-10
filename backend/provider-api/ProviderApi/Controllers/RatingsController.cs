using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nestly.Application.ProviderRatings;
using Nestly.BuildingBlocks.Extensions;
using Nestly.Infrastructure;

namespace Nestly.ProviderApi.Controllers;

/// <summary>
/// Ratings and feedback (docs/OPEN-FIXES-FEATURES.csv "Ratings and
/// feedback"): the provider's own running average rating, review count, and
/// recent customer reviews. Every action is scoped to the caller's own
/// provider id taken from the JWT (SRS 28.3 IDOR), same pattern as
/// <see cref="EarningsController"/>/<see cref="ProfileController"/>.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Authorize(AuthenticationSchemes = DependencyInjection.ProviderJwtBearerScheme)]
[Route("api/v{version:apiVersion}/ratings")]
public class RatingsController : ControllerBase
{
    private readonly IProviderRatingsService _ratingsService;

    public RatingsController(IProviderRatingsService ratingsService)
    {
        _ratingsService = ratingsService;
    }

    /// <summary>The caller's running average rating and total review count.</summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(ProviderRatingsSummaryResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary()
    {
        var result = await _ratingsService.GetSummaryAsync(CurrentProviderId());
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>The caller's own recent reviews, newest first.</summary>
    [HttpGet("reviews")]
    [ProducesResponseType(typeof(ProviderReviewSearchResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListReviews([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _ratingsService.GetReviewsAsync(CurrentProviderId(), page, pageSize);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    private Guid CurrentProviderId() =>
        User.GetSubjectId();
}
