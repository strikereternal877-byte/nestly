namespace Nestly.Application.ProviderRatings;

/// <summary>
/// The provider's own running rating (docs/OPEN-FIXES-FEATURES.csv "Ratings
/// and feedback"). <see cref="AverageRating"/> is null - not zero - when the
/// provider has no visible, unflagged review yet, mirroring
/// <c>Reviews.ProviderRatingSummary</c>'s own "new professional, no rating"
/// convention (see <c>ProviderProfileResponse</c> for the same pattern).
/// <see cref="ReviewCount"/> is always a real number (0 when there is
/// nothing to rate), since a count has no meaningful "unknown" state.
/// </summary>
public sealed record ProviderRatingsSummaryResponse(double? AverageRating, int ReviewCount);

/// <summary>
/// One customer review as shown back to the provider it describes. Deliberately
/// thinner than the admin moderation screen's <c>ReviewModerationResponse</c>:
/// no booking id, no service/category, and the customer is named only by
/// <see cref="CustomerDisplayName"/> (their first name, or "A customer" for a
/// blank/legacy name) rather than their full name, mobile number or customer
/// id - a provider gets enough to recognize the review as genuine without
/// being handed the reviewing customer's identity.
/// </summary>
public sealed record ProviderReviewResponse(
    Guid Id,
    int Rating,
    string? ReviewText,
    string CustomerDisplayName,
    DateTime CreatedAtUtc);

/// <summary>A page of <see cref="ProviderReviewResponse"/>, newest first.</summary>
public sealed record ProviderReviewSearchResponse(
    IReadOnlyList<ProviderReviewResponse> Items,
    int TotalCount,
    int Page,
    int PageSize);
