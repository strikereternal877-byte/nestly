/**
 * Response shapes for the Provider API's ratings surface (`/api/v1/ratings`).
 * Mirrors backend/shared/Application/ProviderRatings/ProviderRatingsContracts.cs
 * (ProviderRatingsSummaryResponse / ProviderReviewResponse /
 * ProviderReviewSearchResponse) field-for-field, camelCased per ASP.NET
 * Core's default JSON naming policy - keep this in sync if that file changes.
 */

/** GET /ratings/summary's response (ProviderRatingsSummaryResponse). */
export interface RatingsSummary {
  /** Null - not zero - when the provider has no visible, unflagged review yet. */
  averageRating: number | null;
  reviewCount: number;
}

/**
 * One customer review as shown back to the provider (ProviderReviewResponse).
 * `customerDisplayName` is deliberately thin - a first name, or "A customer" -
 * never the reviewing customer's full identity.
 */
export interface ProviderReview {
  id: string;
  rating: number;
  reviewText: string | null;
  customerDisplayName: string;
  createdAtUtc: string;
}

/** GET /ratings/reviews's response (ProviderReviewSearchResponse). */
export interface ReviewSearchResponse {
  items: ProviderReview[];
  totalCount: number;
  page: number;
  pageSize: number;
}
