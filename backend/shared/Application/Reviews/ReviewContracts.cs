using Nestly.Domain;

namespace Nestly.Application.Reviews;

/// <summary>Review eligibility for a booking (SRS 11.16.1, 11.16.3, tasks 85a, 85c).</summary>
public record ReviewEligibilityResponse(bool IsEligible, string? IneligibilityReason);

/// <summary>Customer-submitted review (SRS 11.16.2).</summary>
public record SubmitReviewRequest(int Rating, string? ReviewText, string? IssueTags);

/// <summary>
/// A provider's rating rolled up from the reviews actually written about
/// them (task 293). Absent entirely - the aggregate returns null, not a
/// zero-filled instance - when a provider has no visible provider-scoped
/// review yet, so "new professional, no rating" and "rated 0" stay
/// distinguishable all the way to the screen.
/// </summary>
/// <param name="AverageRating">Rounded to one decimal, which is the precision every surface displaying it renders ("4.8"); rounding once here stops two screens disagreeing over the same underlying rows.</param>
/// <param name="ReviewCount">How many visible reviews the average is over - the honest denominator, so a single five-star job cannot be presented like a hundred of them.</param>
public sealed record ProviderRatingSummary(Guid ProviderId, double AverageRating, int ReviewCount);

/// <summary>A submitted review (SRS 17.1).</summary>
public record ReviewResponse(
    Guid Id,
    Guid BookingId,
    Guid ServiceId,
    int Rating,
    string? ReviewText,
    string? IssueTags,
    ReviewStatus Status,
    DateTime CreatedAtUtc);

/// <summary>
/// One provider-scoped review joined with the reviewing customer's raw name
/// (docs/OPEN-FIXES-FEATURES.csv "Ratings and feedback"), as read by
/// <see cref="IReviewRepository.SearchVisibleForProviderAsync"/> - the
/// repository boundary only, never returned from an API. The full customer
/// name still needs minimizing to a display name before it reaches the
/// provider; that happens one layer up, in
/// <c>ProviderRatings.IProviderRatingsService</c>.
/// </summary>
public sealed record ProviderVisibleReviewRow(Review Review, string CustomerName);

/// <summary>A page of <see cref="ProviderVisibleReviewRow"/> plus the total match count, for pagination.</summary>
public sealed record ProviderVisibleReviewSearchResult(IReadOnlyList<ProviderVisibleReviewRow> Rows, int TotalCount);
