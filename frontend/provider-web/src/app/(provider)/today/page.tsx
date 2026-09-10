"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { ErrorState, NotYetAvailable } from "@/components/states";
import { Button, Card, EmptyState, PageHeading, Skeleton } from "@/components/ui";
import { isNotImplemented } from "@/lib/api";
import { formatDateTime, formatInr, formatIsoDate, formatTime } from "@/lib/format";
import { listJobs } from "@/lib/jobs-api";
import { pickActiveJob } from "@/lib/jobs-active";
import { JobStatus } from "@/lib/jobs-types";
import { JobStatusBadge } from "../jobs/_components/JobStatusBadge";
import { RecurringJobBadge } from "../jobs/_components/RecurringJobBadge";
import type { JobListItem } from "@/lib/jobs-types";

/**
 * What to call the one thing this job needs from the provider next. Purely a
 * button label - the actual action (with its confirmation modal, deadline
 * extension on failure, completion-verification gating, etc.) lives on the
 * job detail page and is not duplicated here; see this file's header comment
 * for why.
 */
const NEXT_ACTION_LABEL: Partial<Record<JobStatus, string>> = {
  [JobStatus.Assigned]: "Accept or decline",
  [JobStatus.Accepted]: "Start job",
  [JobStatus.EnRoute]: "Mark arrived",
  [JobStatus.Arrived]: "Start job",
  [JobStatus.InProgress]: "Finish job",
};

/**
 * Today / Now screen (docs/OPEN-FIXES-FEATURES.csv, "Provider Web, Proposed
 * new page, Today / Now screen"): the default post-login landing route.
 * Before this existed the app opened straight onto the full filterable
 * `/jobs` list, and a provider mid-shift had to filter and drill in just to
 * reach the job they were actually standing in front of. This screen instead
 * picks that one job (`pickActiveJob`, `lib/jobs-active.ts`) out of the same
 * `GET /jobs` response `/jobs` already fetches and puts it - and its single
 * next action - front and centre, with nothing to filter.
 *
 * Deliberately thin: it never mutates a job itself. The accept/decline
 * decision, the on-the-way/arrived/start/complete transitions, and the
 * completion-verification gate in front of "complete" are all built (with
 * their own confirmation modal, sticky action bar and error handling) on
 * `/jobs/[id]`, and extracting that logic here would mean re-deriving its
 * gating (most visibly: InProgress needs a submitted completion verification
 * before "complete" is even legal) in a second place that could drift from
 * the original. So the card's call to action routes straight into the
 * existing detail page instead of re-implementing any mutation.
 */
export default function TodayPage() {
  const query = useQuery({
    // Same key/query `/jobs` uses with no filters applied - shares its cache
    // rather than firing a second, identical unfiltered fetch.
    queryKey: ["provider-jobs", "", ""],
    queryFn: () => listJobs({}),
  });

  return (
    <div>
      <PageHeading title="Today" subtitle="The job that needs you right now." />

      {query.isPending ? (
        <TodaySkeleton />
      ) : query.isError && isNotImplemented(query.error) ? (
        <NotYetAvailable
          title="Today isn't available yet"
          description="Job assignment is still being built on the platform side. Once your account can receive bookings, they will appear here."
        />
      ) : query.isError ? (
        <ErrorState
          title="Couldn't load your jobs"
          error={query.error}
          onRetry={() => query.refetch()}
          isRetrying={query.isRefetching}
        />
      ) : (
        <TodayContent jobs={query.data} />
      )}
    </div>
  );
}

function TodayContent({ jobs }: { jobs: JobListItem[] }) {
  const activeJob = pickActiveJob(jobs);

  if (!activeJob) {
    return (
      <EmptyState
        title="No active job right now"
        description="Nothing needs your attention this moment. New offers will show up here the instant they're assigned to you."
        action={
          <Link href="/jobs">
            <Button variant="secondary">View all jobs</Button>
          </Link>
        }
      />
    );
  }

  const isRecurring = activeJob.recurringBookingPlanId !== null;
  const nextActionLabel = NEXT_ACTION_LABEL[activeJob.status] ?? "View job";

  return (
    <div className="flex flex-col gap-4">
      <Card>
        <div className="flex flex-col gap-5">
          <div className="flex items-start justify-between gap-3">
            <div className="flex min-w-0 flex-wrap items-center gap-1.5">
              <JobStatusBadge status={activeJob.status} />
              {isRecurring ? (
                <RecurringJobBadge frequency={activeJob.recurringFrequency} />
              ) : null}
            </div>
            {/* Net payout, not the customer's gross booking total - see
                JobListItem.netAmountToProvider's doc comment. */}
            <span className="nums shrink-0 text-base font-semibold text-fg">
              {formatInr(activeJob.netAmountToProvider)}
            </span>
          </div>

          <div className="min-w-0">
            <p className="truncate text-xl font-semibold text-fg">
              {activeJob.customerNameSnapshot}
            </p>
            <p className="mt-1 nums text-sm text-fg-muted">
              {formatIsoDate(activeJob.slotDate)} · {formatTime(activeJob.slotStartTimeSnapshot)}–
              {formatTime(activeJob.slotEndTimeSnapshot)}
            </p>
            <p className="nums mt-0.5 text-xs text-fg-subtle">{activeJob.bookingReference}</p>
          </div>

          <p className="text-sm leading-relaxed text-fg-subtle">
            {activeJob.addressLine1Snapshot}, {activeJob.addressCitySnapshot}{" "}
            {activeJob.addressPincodeSnapshot}
          </p>

          {activeJob.status === JobStatus.Assigned && activeJob.responseDeadline ? (
            <p className="flex items-center gap-1.5 rounded-lg bg-warning-soft px-2.5 py-1.5 text-xs font-medium text-warning">
              <svg
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                strokeWidth="2"
                strokeLinecap="round"
                className="h-3.5 w-3.5 shrink-0"
                aria-hidden
              >
                <circle cx="12" cy="12" r="9" />
                <path d="M12 7v5l3 2" />
              </svg>
              Respond by {formatDateTime(activeJob.responseDeadline)}
            </p>
          ) : null}

          <Link href={`/jobs/${activeJob.bookingId}`}>
            <Button
              type="button"
              size="lg"
              fullWidth
              icon={
                <svg
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2.5"
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  className="h-5 w-5"
                  aria-hidden
                >
                  <path d="M5 12h14M13 6l6 6-6 6" />
                </svg>
              }
            >
              {nextActionLabel}
            </Button>
          </Link>
        </div>
      </Card>

      <Link
        href="/jobs"
        className="self-center text-sm font-medium text-fg-muted transition-colors duration-fast ease-out hover:text-fg"
      >
        View all jobs
      </Link>
    </div>
  );
}

/** Mirrors the real card's shape so nothing jumps when data lands. */
function TodaySkeleton() {
  return (
    <div className="flex flex-col gap-3 rounded-2xl bg-surface p-6 shadow-sm" aria-hidden>
      <div className="flex items-start justify-between gap-3">
        <Skeleton className="h-5 w-20 rounded-full" />
        <Skeleton className="h-5 w-20" />
      </div>
      <Skeleton className="h-6 w-48" />
      <Skeleton className="h-4 w-56" />
      <Skeleton className="h-4 w-full" />
      <Skeleton className="mt-2 h-11 w-full rounded-lg" />
    </div>
  );
}
