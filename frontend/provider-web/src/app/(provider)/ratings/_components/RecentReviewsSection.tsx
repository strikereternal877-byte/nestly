"use client";

import { useQuery } from "@tanstack/react-query";
import { ErrorState } from "@/components/states";
import { Card, EmptyState, Skeleton } from "@/components/ui";
import { listReviews } from "@/lib/ratings-api";
import { formatRelativeDate } from "@/lib/format";
import type { ProviderReview } from "@/lib/ratings-types";
import { Stars } from "./Stars";

/**
 * Recent customer feedback (docs/OPEN-FIXES-FEATURES.csv "Ratings and
 * feedback"), newest first. A hidden or flagged review never reaches this
 * list - the backend excludes both before the response leaves provider-api,
 * so there is nothing to filter here.
 */
export function RecentReviewsSection() {
  const query = useQuery({ queryKey: ["provider-reviews"], queryFn: () => listReviews({ pageSize: 50 }) });

  if (query.isPending) {
    return (
      <Card title="Recent reviews">
        <ReviewsSkeleton />
      </Card>
    );
  }

  if (query.isError) {
    return (
      <Card title="Recent reviews">
        <ErrorState
          title="Couldn't load your reviews"
          error={query.error}
          onRetry={() => query.refetch()}
          isRetrying={query.isRefetching}
        />
      </Card>
    );
  }

  const { items } = query.data;

  return (
    <Card
      title="Recent reviews"
      description="What customers have said after a completed job."
    >
      {items.length === 0 ? (
        <EmptyState
          title="No reviews yet"
          description="A customer's rating and comment will show up here once they review a completed job."
        />
      ) : (
        <ul className="flex flex-col divide-y divide-line">
          {items.map((review) => (
            <ReviewRow key={review.id} review={review} />
          ))}
        </ul>
      )}
    </Card>
  );
}

function ReviewRow({ review }: { review: ProviderReview }) {
  return (
    <li className="flex flex-col gap-1.5 py-4 first:pt-0 last:pb-0">
      <div className="flex items-center justify-between gap-3">
        <Stars rating={review.rating} size="sm" />
        <span className="whitespace-nowrap text-xs text-fg-subtle">
          {formatRelativeDate(review.createdAtUtc)}
        </span>
      </div>
      {review.reviewText ? (
        <p className="text-sm leading-relaxed text-fg">{review.reviewText}</p>
      ) : (
        <p className="text-sm italic leading-relaxed text-fg-subtle">No comment left.</p>
      )}
      <p className="text-xs text-fg-muted">— {review.customerDisplayName}</p>
    </li>
  );
}

function ReviewsSkeleton() {
  return (
    <div className="flex flex-col gap-4" aria-hidden>
      {Array.from({ length: 3 }, (_, index) => (
        <div key={index} className="flex flex-col gap-2">
          <Skeleton className="h-3.5 w-24" />
          <Skeleton className="h-4 w-full" />
          <Skeleton className="h-3 w-28" />
        </div>
      ))}
    </div>
  );
}
