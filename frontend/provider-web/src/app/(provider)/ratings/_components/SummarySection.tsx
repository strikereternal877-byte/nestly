"use client";

import { useQuery } from "@tanstack/react-query";
import { ErrorState } from "@/components/states";
import { Skeleton } from "@/components/ui";
import { getRatingsSummary } from "@/lib/ratings-api";
import { Stars } from "./Stars";

/**
 * The provider's running rating, as the first thing on the screen -
 * "how am I doing" is the single question this page exists to answer.
 * Same brand-panel treatment as earnings' `SummarySection`, for the same
 * reason: on a phone, anything else at the top pushes the headline number
 * below the fold.
 */
export function SummarySection() {
  const query = useQuery({ queryKey: ["provider-ratings-summary"], queryFn: getRatingsSummary });

  if (query.isPending) {
    return <SummarySkeleton />;
  }

  if (query.isError) {
    return (
      <ErrorState
        title="Couldn't load your rating"
        error={query.error}
        onRetry={() => query.refetch()}
        isRetrying={query.isRefetching}
      />
    );
  }

  const { averageRating, reviewCount } = query.data;

  return (
    <section className="animate-fade-in overflow-hidden rounded-2xl bg-brand-gradient p-6 shadow-brand sm:p-7">
      <p className="text-sm font-medium text-fg-on-brand/80">Your rating</p>
      {averageRating === null ? (
        <>
          <p className="mt-1 text-display-md font-semibold text-fg-on-brand">No rating yet</p>
          <p className="mt-3 text-xs leading-relaxed text-fg-on-brand/75">
            Your average will appear here once a customer rates a completed job.
          </p>
        </>
      ) : (
        <>
          <div className="mt-1 flex items-baseline gap-3">
            <p className="nums text-display-md font-semibold text-fg-on-brand">
              {averageRating.toFixed(1)}
            </p>
            <Stars rating={averageRating} />
          </div>
          <p className="mt-3 text-xs leading-relaxed text-fg-on-brand/75">
            From {reviewCount} customer review{reviewCount === 1 ? "" : "s"}.
          </p>
        </>
      )}
    </section>
  );
}

/** Same footprint as the real panel so the page does not jump when it lands. */
function SummarySkeleton() {
  return (
    <div className="overflow-hidden rounded-2xl bg-surface p-6 shadow-sm sm:p-7" aria-hidden>
      <Skeleton className="h-4 w-24" />
      <Skeleton className="mt-2 h-10 w-40" />
      <Skeleton className="mt-4 h-3 w-52" />
    </div>
  );
}
