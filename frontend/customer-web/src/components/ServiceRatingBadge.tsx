"use client";

import { useQuery } from "@tanstack/react-query";
import { API_V1, apiFetch } from "@/lib/api";
import type { ServiceReviewSummary } from "@/lib/types";

/**
 * Compact rating/review-count trust badge for the service detail page, shown
 * next to the price right in `PageBanner` (SRS 11.6.1; docs/OPEN-FIXES-FEATURES.csv
 * "Service detail, Trust signals" - previously only a plain "Reviews &
 * ratings" heading existed, well below the fold, with nothing near the
 * title/price giving the page any visual trust weight).
 *
 * Reuses `ReviewsSummary`'s own real per-service aggregate
 * (`GET /services/{slug}/reviews-summary`) under the identical query key, so
 * mounting both here and further down the page costs exactly one network
 * request, not two. Deliberately renders nothing until a real average/count
 * have loaded, and nothing at all for a service with zero reviews - unlike
 * `CategoryTile`'s explicitly-decorative placeholder rating, this is real
 * fetched data, so it only ever shows a number that means something.
 */
export function ServiceRatingBadge({ slug }: { slug: string }) {
  const query = useQuery({
    queryKey: ["service-reviews-summary", slug],
    queryFn: () => apiFetch<ServiceReviewSummary>(`${API_V1}/services/${slug}/reviews-summary`),
  });

  if (!query.data || query.data.totalCount === 0) {
    return null;
  }

  return (
    <span className="mt-1 inline-flex items-center gap-1.5 rounded-full bg-white/15 px-3.5 py-1.5 text-xs font-semibold text-white backdrop-blur-sm">
      <StarIcon />
      <span className="nums">{query.data.averageRating.toFixed(1)}</span>
      <span aria-hidden className="text-white/60">
        &middot;
      </span>
      <span>
        {query.data.totalCount} review{query.data.totalCount === 1 ? "" : "s"}
      </span>
    </span>
  );
}

function StarIcon() {
  return (
    <svg viewBox="0 0 24 24" fill="currentColor" className="h-3.5 w-3.5 text-accent-400" aria-hidden>
      <path d="m12 2.5 2.9 5.9 6.6.9-4.8 4.6 1.2 6.5-5.9-3.1-5.9 3.1 1.2-6.5L2.5 9.3l6.6-.9L12 2.5Z" />
    </svg>
  );
}
