"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useRef, useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { BannerBreadcrumb, ScreenSkeleton, formatInstant } from "@/components/patterns";
import { PageBanner } from "@/components/PageBanner";
import { RequireAuth } from "@/components/RequireAuth";
import { Alert, Badge, Button, Card, Field, PageHeading, Textarea, cx } from "@/components/ui";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import { ReviewStatus } from "@/lib/types";
import type { ReviewEligibilityResponse, ReviewResponse } from "@/lib/types";

/**
 * Review submission: rating + review form for completed bookings (SRS 25.1,
 * task 91).
 */
export default function ReviewBookingPage() {
  return (
    <RequireAuth>
      <ReviewBookingScreen />
    </RequireAuth>
  );
}

const reviewSchema = z.object({
  reviewText: z.string().max(2000, "Review text must be 2000 characters or fewer.").optional(),
  issueTags: z.string().max(500, "Issue tags must be 500 characters or fewer.").optional(),
});

type ReviewFormValues = z.infer<typeof reviewSchema>;

function reviewStatusLabel(status: ReviewStatus): string {
  switch (status) {
    case ReviewStatus.Visible:
      return "Visible";
    case ReviewStatus.Hidden:
      return "Hidden";
    default:
      return "Unknown";
  }
}

function ReviewBookingScreen() {
  const { id } = useParams<{ id: string }>();
  const queryClient = useQueryClient();

  const eligibilityQuery = useQuery({
    queryKey: ["review-eligibility", id],
    queryFn: () =>
      apiFetch<ReviewEligibilityResponse>(`${API_V1}/bookings/${id}/review/eligibility`, {
        authenticated: true,
      }),
  });

  // No review yet -> 204 -> apiFetch resolves undefined. Normalised to null
  // here rather than left as undefined: TanStack Query treats a query
  // function returning undefined as an error ("Query data cannot be
  // undefined") since undefined is its own internal "no data yet" sentinel -
  // task 140d's E2E suite hit this as a real crash on every booking that
  // doesn't have a review yet, not a test-only issue.
  const reviewQuery = useQuery({
    queryKey: ["review", id],
    queryFn: async () =>
      (await apiFetch<ReviewResponse | undefined>(`${API_V1}/bookings/${id}/review`, {
        authenticated: true,
      })) ?? null,
  });

  if (eligibilityQuery.isPending || reviewQuery.isPending) {
    return (
      <main className="flex w-full flex-col">
        <div className="listing-banner h-[13.5rem] w-full sm:h-[15.5rem]" aria-hidden />
        <ScreenSkeleton cards={1} className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14" />
      </main>
    );
  }

  if (eligibilityQuery.isError || !eligibilityQuery.data) {
    return (
      <main className="mx-auto w-full max-w-7xl px-4 py-12 sm:px-6">
        <PageHeading title="Leave a review" />
        <Alert
          tone="error"
          title="Couldn't check review eligibility"
          action={
            <Button size="sm" variant="secondary" onClick={() => eligibilityQuery.refetch()}>
              Retry
            </Button>
          }
        >
          {describeError(eligibilityQuery.error)}
        </Alert>
      </main>
    );
  }

  if (reviewQuery.isError) {
    return (
      <main className="mx-auto w-full max-w-7xl px-4 py-12 sm:px-6">
        <PageHeading title="Leave a review" />
        <Alert
          tone="error"
          title="Couldn't load your review"
          action={
            <Button size="sm" variant="secondary" onClick={() => reviewQuery.refetch()}>
              Retry
            </Button>
          }
        >
          {describeError(reviewQuery.error)}
        </Alert>
      </main>
    );
  }

  const existingReview = reviewQuery.data;

  if (existingReview) {
    return (
      <main className="flex w-full flex-col animate-rise">
        <PageBanner
          title="Your review"
          description="Thanks — this helps the next customer choose."
          breadcrumb={
            <BannerBreadcrumb
              items={[{ label: "Home", href: "/" }, { label: "My bookings", href: "/bookings" }, { label: "Your review" }]}
            />
          }
        />

        <div className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14">
        <Card title="Submitted review">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <StarDisplay rating={existingReview.rating} />
            <Badge tone={existingReview.status === ReviewStatus.Visible ? "success" : "neutral"}>
              {reviewStatusLabel(existingReview.status)}
            </Badge>
          </div>

          <p className="mt-2 text-xs text-fg-subtle">
            Submitted {formatInstant(existingReview.createdAtUtc)}
          </p>

          {existingReview.reviewText ? (
            <blockquote className="mt-4 border-l-2 border-brand-600/40 pl-4 text-sm leading-relaxed text-fg">
              {existingReview.reviewText}
            </blockquote>
          ) : null}

          {existingReview.issueTags ? (
            <div className="mt-4">
              <p className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-fg-subtle">
                Issue tags
              </p>
              <p className="text-sm text-fg-muted">{existingReview.issueTags}</p>
            </div>
          ) : null}

          <div className="mt-6">
            <Link
              href={`/bookings/${id}`}
              className="text-sm font-medium text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
            >
              Back to booking
            </Link>
          </div>
        </Card>

        {existingReview.rating >= 4 ? <ReferEarnPrompt /> : null}
        </div>
      </main>
    );
  }

  const eligibility = eligibilityQuery.data;

  if (!eligibility.isEligible) {
    return (
      <main className="mx-auto w-full max-w-7xl px-4 py-8 sm:px-6 sm:py-12">
        <PageHeading title="Leave a review" />
        <Alert tone="warning" title="This booking can't be reviewed yet">
          {eligibility.ineligibilityReason ??
            "Reviews open once a professional has completed the job."}
        </Alert>
        <div className="mt-5">
          <Link
            href={`/bookings/${id}`}
            className="text-sm font-medium text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
          >
            Back to booking
          </Link>
        </div>
      </main>
    );
  }

  return (
    <ReviewForm
      bookingId={id}
      onSubmitted={() => queryClient.invalidateQueries({ queryKey: ["review", id] })}
    />
  );
}

function ReviewForm({ bookingId, onSubmitted }: { bookingId: string; onSubmitted: () => void }) {
  const [rating, setRatingState] = useState<number>(0);
  const [ratingError, setRatingError] = useState<string | null>(null);

  /** Synchronous double-submit guard: Review.BookingId is uniquely indexed. */
  const inFlight = useRef(false);

  const form = useForm<ReviewFormValues>({
    resolver: zodResolver(reviewSchema),
    defaultValues: { reviewText: "", issueTags: "" },
  });
  const { errors } = form.formState;

  const submitMutation = useMutation({
    mutationFn: (values: ReviewFormValues) =>
      apiFetch<ReviewResponse>(`${API_V1}/bookings/${bookingId}/review`, {
        method: "POST",
        authenticated: true,
        body: JSON.stringify({
          rating,
          reviewText: values.reviewText?.trim() ? values.reviewText.trim() : null,
          issueTags: values.issueTags?.trim() ? values.issueTags.trim() : null,
        }),
      }),
    onSettled: () => {
      inFlight.current = false;
    },
    onSuccess: onSubmitted,
  });

  // False positive: handleSubmit only *builds* this callback during render;
  // it doesn't invoke it until a real submit event, well outside render.
  // eslint-disable-next-line react-hooks/refs
  const handleSubmit = form.handleSubmit((values) => {
    if (rating < 1 || rating > 5) {
      setRatingError("Please select a rating from 1 to 5.");
      return;
    }
    setRatingError(null);
    if (inFlight.current) return;
    inFlight.current = true;
    submitMutation.mutate(values);
  });

  return (
    <main className="flex w-full flex-col animate-rise">
      <PageBanner
        title="Leave a review"
        description="Tell us how the service went."
        breadcrumb={
          <BannerBreadcrumb
            items={[{ label: "Home", href: "/" }, { label: "My bookings", href: "/bookings" }, { label: "Leave a review" }]}
          />
        }
      />

      <div className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14">
      <Card title="Rate your experience">
        <form onSubmit={handleSubmit} className="flex flex-col gap-5" noValidate>
          {submitMutation.isError ? (
            <Alert tone="error" title="We couldn't post your review">
              {describeError(submitMutation.error)} What you wrote is still here — try again.
            </Alert>
          ) : null}

          <div className="flex flex-col gap-2">
            <span className="text-sm font-medium text-fg">Rating</span>
            {/* The radio accessible names ("5 stars") are addressed by the
                E2E suite - keep the wording exactly. */}
            <StarInput value={rating} onChange={setRatingState} />
            {ratingError ? <p className="text-xs font-medium text-danger">{ratingError}</p> : null}
          </div>

          {/* id="review-text" / id="review-tags" are addressed directly by the
              E2E suite. */}
          <Textarea
            id="review-text"
            label="Review (optional)"
            rows={4}
            maxLength={2000}
            hint="What went well, and what could have been better?"
            error={errors.reviewText?.message}
            {...form.register("reviewText")}
          />

          <Field
            id="review-tags"
            label="Issue tags (optional)"
            type="text"
            placeholder="e.g. late arrival, pricing"
            maxLength={500}
            hint="Comma-separated. Only if something went wrong."
            error={errors.issueTags?.message}
            {...form.register("issueTags")}
          />

          <Button type="submit" size="lg" className="self-start" loading={submitMutation.isPending}>
            Submit review
          </Button>

          <p role="status" aria-live="polite" className="sr-only">
            {submitMutation.isPending ? "Posting your review, please wait." : ""}
          </p>
        </form>
      </Card>
      </div>
    </main>
  );
}

const RATING_HINTS = ["", "Poor", "Fair", "Good", "Great", "Excellent"] as const;

/**
 * Clickable 1-5 star rating input - a one-off control, not a shared
 * primitive. Uses the accent (amber) scale, which the token layer reserves
 * for ratings and reward moments.
 */
function StarInput({ value, onChange }: { value: number; onChange: (rating: number) => void }) {
  return (
    <div className="flex items-center gap-3">
      <div role="radiogroup" aria-label="Rating" className="flex gap-1">
        {[1, 2, 3, 4, 5].map((n) => (
          <button
            key={n}
            type="button"
            role="radio"
            aria-checked={value === n}
            aria-label={`${n} star${n === 1 ? "" : "s"}`}
            onClick={() => onChange(n)}
            className="rounded-lg p-1 text-3xl leading-none transition duration-fast ease-out hover:scale-110 active:scale-95"
          >
            <span
              aria-hidden
              className={cx(
                "block transition-colors duration-fast ease-out",
                n <= value ? "text-accent-500" : "text-fg-subtle/40",
              )}
            >
              ★
            </span>
          </button>
        ))}
      </div>
      <span className="text-sm font-medium text-fg-muted" aria-hidden>
        {RATING_HINTS[value] ?? ""}
      </span>
    </div>
  );
}

function StarDisplay({ rating }: { rating: number }) {
  return (
    <span className="flex items-center gap-2">
      {/* The E2E suite finds this by its label ("5 out of 5 stars"). */}
      <span aria-label={`${rating} out of 5 stars`} className="text-2xl leading-none">
        {[1, 2, 3, 4, 5].map((n) => (
          <span
            key={n}
            aria-hidden
            className={n <= rating ? "text-accent-500" : "text-fg-subtle/40"}
          >
            ★
          </span>
        ))}
      </span>
      <span className="nums text-sm font-medium text-fg-muted">{rating}/5</span>
    </span>
  );
}

/**
 * Refer & Earn nudge shown after a completed booking's 4-5 star review
 * (task 176) - a high-intent moment (happy customer, just finished a job)
 * rather than only a static Refer & Earn screen a customer has to seek out.
 * No new delivery mechanism: a plain in-page link to /refer-earn.
 */
function ReferEarnPrompt() {
  return (
    <div className="mt-5 rounded-2xl border border-accent-600/25 bg-accent-100 p-5 dark:bg-accent-500/10">
      <p className="text-base font-semibold text-accent-700 dark:text-accent-300">
        Loved the service?
      </p>
      <p className="mt-1 text-sm leading-relaxed text-fg-muted">
        Share Glavyx with a friend and you&apos;ll both get rewarded once they book.
      </p>
      <Link
        href="/refer-earn"
        className="mt-3 inline-flex h-10 items-center justify-center rounded-lg bg-accent-600 px-4 text-sm font-medium text-fg-on-brand shadow-xs transition duration-fast ease-out hover:bg-accent-700"
      >
        Refer &amp; Earn
      </Link>
    </div>
  );
}
