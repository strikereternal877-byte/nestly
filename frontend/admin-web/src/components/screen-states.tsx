"use client";

import { useEffect } from "react";
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
/**
 * Route-segment `error.tsx` fallback (task: premium UX audit, "Only one
 * root-level error boundary for the whole app"). Before this, an uncaught
 * exception on any page bubbled all the way to `app/error.tsx`, which
 * replaces the entire tree from the root layout down — sidebar and header
 * included — leaving the admin with no navigation back into the panel except
 * a hard reload.
 *
 * A module's own `error.tsx` (e.g. `(admin)/bookings/error.tsx`) is a more
 * specific boundary: Next.js resets at the nearest one, so this renders
 * *inside* the `(admin)` layout — sidebar and header stay mounted and
 * navigable — while only that module's content area shows the failure. Sized
 * for that content slot (not full-viewport, unlike the root fallback it
 * complements) and reuses the same "Reference: <digest>" support-lookup
 * pattern.
 */
export function RouteErrorFallback({
  error,
  reset,
  title = "This section failed to load",
}: {
  error: Error & { digest?: string };
  reset: () => void;
  title?: string;
}) {
  useEffect(() => {
    console.error("Unhandled admin-web route error:", error);
  }, [error]);

  return (
    <div className="flex w-full max-w-7xl flex-col items-center justify-center rounded-2xl border border-dashed border-line px-6 py-16 text-center">
      <span
        aria-hidden
        className="flex h-12 w-12 items-center justify-center rounded-2xl bg-danger-soft text-danger"
      >
        <svg viewBox="0 0 24 24" fill="none" className="h-6 w-6">
          <path
            d="M12 9v4m0 4h.01M10.3 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.7 3.86a2 2 0 0 0-3.4 0Z"
            stroke="currentColor"
            strokeWidth="1.75"
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        </svg>
      </span>

      <h1 className="mt-4 text-base font-semibold text-fg">{title}</h1>
      <p className="mt-1.5 max-w-sm text-sm leading-relaxed text-fg-muted">
        Trying again usually fixes it — no changes were saved. The rest of the admin panel is still available from the
        sidebar.
      </p>

      {error.digest ? (
        <p className="nums mt-3 text-xs text-fg-subtle">
          Reference: <span className="font-medium text-fg-muted">{error.digest}</span>
        </p>
      ) : null}

      <Button type="button" className="mt-6" onClick={reset}>
        Try again
      </Button>
    </div>
  );
}

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
