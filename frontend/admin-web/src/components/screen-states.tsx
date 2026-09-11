"use client";

import type { ReactNode } from "react";
import { Alert, Button, PageHeading, Skeleton, SkeletonText } from "@/components/ui";
import { Breadcrumbs } from "@/components/data-table";
import { describeError } from "@/lib/api";

/**
 * The two states every admin *detail* screen needs before it has a record
 * (task 222), matching the three-state contract the list screens get from
 * `DataTable`.
 *
 * Before this, roughly a dozen detail screens each rendered
 * `<p className="text-sm text-neutral-600">Loading…</p>` and a bare `<Alert>`
 * with no way to retry — a transient network failure left the admin on a dead
 * screen whose only exit was a browser reload.
 */

/** Loading placeholder shaped like a heading plus N cards. */
export function DetailSkeleton({
  cards = 2,
  // Left-aligned, not mx-auto: this backs every [id]/page.tsx detail screen
  // (task 222), and centering it inside the wide layout content column made
  // the detail float ~180px right of where the list it was reached from
  // starts - the same left edge the list's own content uses.
  className = "flex w-full max-w-3xl flex-col gap-6",
}: {
  cards?: number;
  className?: string;
}) {
  return (
    <div className={className}>
      <div>
        <Skeleton className="h-3 w-40" />
        <Skeleton className="mt-3 h-8 w-72" />
        <Skeleton className="mt-2 h-4 w-96 max-w-full" />
      </div>
      {Array.from({ length: cards }, (_, index) => (
        <div key={index} className="rounded-2xl bg-surface p-6 shadow-sm">
          <Skeleton className="h-4 w-40" />
          <div className="mt-5">
            <SkeletonText lines={4} />
          </div>
        </div>
      ))}
    </div>
  );
}

/**
 * Terminal error for a detail screen: keeps the page heading and breadcrumbs so
 * the admin still knows where they are and can navigate away, and gives the
 * failed load a working Retry.
 */
export function DetailError({
  title,
  breadcrumbs,
  error,
  message,
  onRetry,
  className = "w-full max-w-3xl",
}: {
  title: string;
  breadcrumbs?: readonly { label: string; href?: string }[];
  error?: unknown;
  /** Overrides the message derived from `error` — for a "not found" with no thrown error. */
  message?: string;
  onRetry?: () => void;
  className?: string;
}) {
  return (
    <div className={className}>
      <PageHeading
        title={title}
        breadcrumbs={breadcrumbs ? <Breadcrumbs items={breadcrumbs} /> : undefined}
      />
      <Alert
        tone="error"
        title="Could not load this record"
        action={
          onRetry ? (
            <Button size="sm" variant="secondary" onClick={onRetry}>
              Retry
            </Button>
          ) : undefined
        }
      >
        {message ?? (error ? describeError(error) : "The record could not be found.")}
      </Alert>
    </div>
  );
}

/** Inline section-level error with a Retry, for a card inside an otherwise-loaded screen. */
export function SectionError({
  error,
  onRetry,
  children,
}: {
  error?: unknown;
  onRetry?: () => void;
  children?: ReactNode;
}) {
  return (
    <Alert
      tone="error"
      action={
        onRetry ? (
          <Button size="sm" variant="secondary" onClick={onRetry}>
            Retry
          </Button>
        ) : undefined
      }
    >
      {children ?? (error ? describeError(error) : "Something went wrong.")}
    </Alert>
  );
}

/**
 * Route-segment `loading.tsx` skeleton (task: premium UX audit, "No
 * per-route loading.tsx anywhere in the app"). Next.js renders this
 * automatically while a route segment's page component (and whatever data it
 * suspends on) is still loading, so a slow navigation shows this instead of a
 * blank/frozen screen.
 *
 * Shaped like the generic list-page anatomy every module screen already
 * shares — `PageHeading` + `FilterBar` + a `DataTable` in its own loading
 * state — since a `loading.tsx` has no props and cannot know the real title
 * or column count for the page it is standing in for. It intentionally
 * mirrors `DataTable`'s own `isLoading` skeleton rows rather than inventing a
 * different shimmer, so the transition from route-skeleton to table-skeleton
 * to real data is one continuous shape, not a visible swap.
 */
export function RouteLoadingSkeleton({ rows = 6 }: { rows?: number }) {
  return (
    <div className="w-full max-w-7xl" aria-hidden>
      <div className="mb-6 flex w-full flex-col gap-4 rounded-2xl bg-surface p-6 shadow-sm sm:flex-row sm:items-end sm:justify-between">
        <div className="min-w-0">
          <Skeleton className="h-3 w-24" />
          <Skeleton className="mt-3 h-4 w-56 max-w-full" />
        </div>
        <Skeleton className="h-10 w-32 shrink-0" />
      </div>

      <div className="rounded-2xl bg-surface p-4 shadow-sm sm:p-5">
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
          {Array.from({ length: 4 }, (_, index) => (
            <Skeleton key={index} className="h-10" />
          ))}
        </div>
      </div>

      <div className="mt-6 overflow-hidden rounded-2xl bg-surface shadow-sm">
        <div className="border-b border-line px-4 py-3 sm:px-5">
          <Skeleton className="h-5 w-32" />
        </div>
        <div className="divide-y divide-line">
          {Array.from({ length: rows }, (_, index) => (
            <div key={index} className="flex items-center gap-4 px-4 py-3 sm:px-5">
              <Skeleton className="h-4 w-40" />
              <Skeleton className="h-4 w-24" />
              <Skeleton className="ml-auto h-4 w-16" />
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
