using Nestly.Application.ProviderRatings;
using Nestly.Application.Reviews;
using Nestly.BuildingBlocks.Results;
using Nestly.Infrastructure.Persistence;

namespace Nestly.Infrastructure.Services;

/// <inheritdoc cref="IProviderRatingsService"/>
public class ProviderRatingsService : IProviderRatingsService
{
    /// <summary>Shown in place of a blank/whitespace-only legacy customer name, so a display name is never empty.</summary>
    private const string FallbackCustomerDisplayName = "A customer";

    private readonly IReviewRepository _reviewRepository;

    public ProviderRatingsService(IReviewRepository reviewRepository)
    {
        _reviewRepository = reviewRepository;
    }

    public async Task<Result<ProviderRatingsSummaryResponse>> GetSummaryAsync(Guid providerId, CancellationToken cancellationToken = default)
    {
        var rating = await _reviewRepository.GetProviderRatingAsync(providerId, cancellationToken);
        return Result.Success(new ProviderRatingsSummaryResponse(rating?.AverageRating, rating?.ReviewCount ?? 0));
    }

    public async Task<Result<ProviderReviewSearchResponse>> GetReviewsAsync(
        Guid providerId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        // Normalized here (not left to the repository) so the response can
        // echo the page/pageSize actually used back to the caller, same
        // convention as ProviderEarningLedgerService.GetJobEarningsAsync.
        (int safePage, int safePageSize) = PagedQueryExtensions.Normalize(page, pageSize);

        var result = await _reviewRepository.SearchVisibleForProviderAsync(providerId, safePage, safePageSize, cancellationToken);

        var items = result.Rows
            .Select(row => new ProviderReviewResponse(
                row.Review.Id,
                row.Review.Rating,
                row.Review.ReviewText,
                ToDisplayName(row.CustomerName),
                row.Review.CreatedAtUtc))
            .ToList();

        return Result.Success(new ProviderReviewSearchResponse(items, result.TotalCount, safePage, safePageSize));
    }

    /// <summary>
    /// PII minimization (docs/OPEN-FIXES-FEATURES.csv "Ratings and feedback"):
    /// a provider sees only the reviewing customer's first name, never the
    /// full name, mobile number, email or customer id the admin moderation
    /// screen shows a moderator (<c>ReviewModerationRow.CustomerName</c> /
    /// <c>ReviewModerationResponse</c>). Enough to make the review feel like
    /// it came from a real person, without handing a professional a
    /// customer's full identity.
    /// </summary>
    private static string ToDisplayName(string customerName)
    {
        string trimmed = customerName.Trim();
        if (trimmed.Length == 0)
        {
            return FallbackCustomerDisplayName;
        }

        int spaceIndex = trimmed.IndexOf(' ');
        return spaceIndex > 0 ? trimmed[..spaceIndex] : trimmed;
    }
}
