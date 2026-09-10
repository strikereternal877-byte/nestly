using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.ProviderRatings;

/// <summary>
/// The provider's own self-service view of how customers have rated them
/// (docs/OPEN-FIXES-FEATURES.csv "Ratings and feedback" - "ratings are
/// captured from customers and used by the business but never shown back to
/// the professional they describe"). A thin, ownership-safe facade over
/// <see cref="Reviews.IReviewRepository"/> - the review aggregate and its
/// moderation state have exactly one owner - same shape as
/// <c>ProviderEarnings.IProviderEarningsService</c>'s relationship to the
/// earnings ledger. Every read is scoped to the caller's own provider id (SRS
/// 28.3 IDOR); there is no id-based lookup here for another provider's rating
/// to leak through.
/// </summary>
public interface IProviderRatingsService
{
    /// <summary>The caller's running average rating and total (visible, unflagged) review count.</summary>
    Task<Result<ProviderRatingsSummaryResponse>> GetSummaryAsync(Guid providerId, CancellationToken cancellationToken = default);

    /// <summary>The caller's own recent reviews, newest first, minimized for PII (see <see cref="ProviderReviewResponse"/>).</summary>
    Task<Result<ProviderReviewSearchResponse>> GetReviewsAsync(Guid providerId, int page, int pageSize, CancellationToken cancellationToken = default);
}
