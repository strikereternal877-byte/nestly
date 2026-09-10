/**
 * Typed client for the Provider API's ratings surface (`/api/v1/ratings`)
 * (docs/OPEN-FIXES-FEATURES.csv "Ratings and feedback"). Every call is
 * authenticated and scoped to the caller's own provider id server-side.
 */
import { API_V1, apiFetch } from "./api";
import type { RatingsSummary, ReviewSearchResponse } from "./ratings-types";

const RATINGS_BASE = `${API_V1}/ratings`;

export const getRatingsSummary = () =>
  apiFetch<RatingsSummary>(`${RATINGS_BASE}/summary`, { authenticated: true });

/**
 * GET /ratings/reviews - the caller's own recent reviews, newest first.
 * `pageSize` defaults high enough to cover a typical recent-reviews list in
 * one page, matching `earnings-api.ts`'s `listJobEarnings`'s own scope - this
 * screen does not (yet) build a page-by-page control.
 */
export const listReviews = (params: { page?: number; pageSize?: number } = {}) => {
  const query = new URLSearchParams();
  query.set("page", String(params.page ?? 1));
  query.set("pageSize", String(params.pageSize ?? 50));
  return apiFetch<ReviewSearchResponse>(`${RATINGS_BASE}/reviews?${query.toString()}`, { authenticated: true });
};
