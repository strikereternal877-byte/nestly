import { endOfLocalDayUtc, startOfLocalDayUtc } from "./day-range";
import { ReviewStatus } from "./types";

/**
 * Filter/query-building helpers for the admin review moderation screen (SRS
 * 12.15, task 122). Mirrors lib/audit.ts's shape for the same kind of
 * filter-form-state-to-query-string job.
 */

export type ReviewStatusFilterValue = "" | "Visible" | "Hidden";

/** Order matches `Nestly.Domain.ReviewStatus`'s declaration order - sent as the readable member name; ASP.NET Core's query-string enum binder accepts it case-insensitively (same precedent as lib/audit.ts's AuditOutcome filter). */
export const REVIEW_STATUS_FILTER_OPTIONS: readonly { value: ReviewStatusFilterValue; label: string }[] = [
  { value: "", label: "Any status" },
  { value: "Visible", label: "Visible" },
  { value: "Hidden", label: "Hidden" },
];

export function reviewStatusLabel(status: ReviewStatus): string {
  return status === ReviewStatus.Hidden ? "Hidden" : "Visible";
}

export type FlaggedFilterValue = "" | "true" | "false";

export const FLAGGED_FILTER_OPTIONS: readonly { value: FlaggedFilterValue; label: string }[] = [
  { value: "", label: "Any" },
  { value: "true", label: "Flagged only" },
  { value: "false", label: "Not flagged" },
];

/**
 * Client-side filter form state (SRS 12.15's filter list: status, flagged,
 * rating range, date, service/category). `fromDate`/`toDate` hold plain
 * `yyyy-mm-dd` values straight out of an `<input type="date">`, converted to
 * full UTC instants only when building the query string - same convention as
 * `AuditLogFilters`.
 *
 * `customerId` is form-hidden (no visible input - SRS 12.15 lists no such
 * filter): it exists purely so the Customer 360 view's "Reviews written"
 * link can deep-link here scoped to one customer, same "backend-supported,
 * no visible input" convention as providers/directory's own createdFrom/
 * customers/directory's own minBookingCount.
 */
export interface ReviewModerationFilters {
  status: ReviewStatusFilterValue;
  flagged: FlaggedFilterValue;
  minRating: string;
  maxRating: string;
  fromDate: string;
  toDate: string;
  serviceId: string;
  categoryId: string;
  customerId: string;
}

export const DEFAULT_REVIEW_MODERATION_FILTERS: ReviewModerationFilters = {
  status: "",
  flagged: "",
  minRating: "",
  maxRating: "",
  fromDate: "",
  toDate: "",
  serviceId: "",
  categoryId: "",
  customerId: "",
};

/** Seeds the filter form from the URL's `customerId` query param - the Customer 360 view's "Reviews written" link opens here as `/reviews?customerId=X`. */
export function reviewFiltersFromSearchParams(params: URLSearchParams): ReviewModerationFilters {
  return { ...DEFAULT_REVIEW_MODERATION_FILTERS, customerId: params.get("customerId") ?? "" };
}

/** Builds the query string for `GET {API_V1}/reviews` (and `/reviews/export`), omitting unset filters. */
export function buildReviewModerationQuery(
  filters: ReviewModerationFilters,
  paging: { page: number; pageSize: number },
): string {
  const params = new URLSearchParams();

  if (filters.status) params.set("status", filters.status);
  if (filters.flagged) params.set("isFlagged", filters.flagged);
  if (filters.minRating) params.set("minRating", filters.minRating);
  if (filters.maxRating) params.set("maxRating", filters.maxRating);
  if (filters.serviceId.trim()) params.set("serviceId", filters.serviceId.trim());
  if (filters.categoryId.trim()) params.set("categoryId", filters.categoryId.trim());
  if (filters.customerId.trim()) params.set("customerId", filters.customerId.trim());

  // <input type="date"> yields "yyyy-mm-dd"; the API filters on a full UTC
  // instant, so pin From to the start of that *local* day and To to its end -
  // otherwise "toUtc=2026-07-31" would exclude every review posted that day.
  // Appending "Z" to the raw value (the previous version) makes the boundary
  // a UTC midnight, which in IST is 05:30 on the wrong side of the day.
  const fromUtc = filters.fromDate ? startOfLocalDayUtc(filters.fromDate) : null;
  const toUtc = filters.toDate ? endOfLocalDayUtc(filters.toDate) : null;
  if (fromUtc) params.set("fromUtc", fromUtc);
  if (toUtc) params.set("toUtc", toUtc);

  params.set("page", String(paging.page));
  params.set("pageSize", String(paging.pageSize));

  return params.toString();
}
