"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useMemo, useState } from "react";
import { ErrorState, NotYetAvailable } from "@/components/states";
import { Badge, Card, IconButton, PageHeading, Skeleton, cx } from "@/components/ui";
import { isNotImplemented } from "@/lib/api";
import { getAvailability } from "@/lib/availability-api";
import { addDays, computeJobConflicts, dayOfWeekOf, startOfWeek, weekDates } from "@/lib/calendar";
import { toLocalIsoDate, todayIsoDate } from "@/lib/date";
import { formatIsoDate, formatTime } from "@/lib/format";
import { listJobs } from "@/lib/jobs-api";
import { isActiveJobStatus } from "@/lib/jobs-active";
import { JobStatusBadge } from "../jobs/_components/JobStatusBadge";
import type { AvailabilityWindow } from "@/lib/availability-types";
import type { JobConflict } from "@/lib/calendar";
import type { JobListItem } from "@/lib/jobs-types";

const WEEKDAY_LABELS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"] as const;

/**
 * Calendar / week view (docs/OPEN-FIXES-FEATURES.csv, "Provider Web, Proposed
 * new page, Calendar and week view"): declared weekly availability and
 * committed jobs were only ever shown on two separate screens
 * (`/availability`, `/jobs`), so spotting a job outside a declared window -
 * or two jobs that overlap - meant a provider cross-checking both by hand.
 * This overlays both over the same seven days and flags either kind of
 * conflict inline (`lib/calendar.ts`'s `computeJobConflicts`).
 *
 * Composed entirely from two endpoints the app already calls elsewhere -
 * `getAvailability` (`/availability`'s `WindowsSection`) and `listJobs({})`
 * (`/today`, `/jobs`, the nav's offer-count badge) - under the exact same
 * query keys, so this never issues a network call any of those screens
 * hasn't already paid for once TanStack Query's cache is warm. See
 * `lib/calendar.ts`'s header comment for why conflict detection stays client-
 * side rather than becoming a new backend endpoint.
 *
 * Rendered as seven stacked day cards rather than an absolute-positioned
 * hour grid: docs/FRONTEND.md's mobile-first rule rules out both a grid wide
 * enough to need horizontal scrolling and a fixed hour axis tall enough to
 * need vertical scrolling within the page on a phone screen, and every job a
 * provider actually has in a week is a handful of slots, not dense enough to
 * need a grid to stay legible.
 */
export default function CalendarPage() {
  const [weekStart, setWeekStart] = useState(() => startOfWeek(new Date()));

  const jobsQuery = useQuery({
    // Same key/query `/today`, `/jobs` and the nav badge use with no filters
    // applied - shares their cache rather than firing a second, identical
    // unfiltered fetch.
    queryKey: ["provider-jobs", "", ""],
    queryFn: () => listJobs({}),
  });
  const availabilityQuery = useQuery({ queryKey: ["provider-availability"], queryFn: getAvailability });

  const dates = useMemo(() => weekDates(weekStart), [weekStart]);
  const isCurrentWeek = dates[0] === toLocalIsoDate(startOfWeek(new Date()));

  const conflicts = useMemo(() => {
    if (!jobsQuery.data || !availabilityQuery.data) return new Map<string, JobConflict>();
    return computeJobConflicts(jobsQuery.data, availabilityQuery.data.windows);
  }, [jobsQuery.data, availabilityQuery.data]);

  return (
    <div className="flex w-full max-w-4xl flex-col gap-6">
      <PageHeading
        title="Calendar"
        subtitle="Your week: declared availability with committed jobs overlaid, conflicts flagged."
      />

      <WeekNav
        weekStart={weekStart}
        isCurrentWeek={isCurrentWeek}
        rangeLabel={`${formatIsoDate(dates[0])} – ${formatIsoDate(dates[6])}`}
        onPrev={() => setWeekStart((current) => addDays(current, -7))}
        onNext={() => setWeekStart((current) => addDays(current, 7))}
        onToday={() => setWeekStart(startOfWeek(new Date()))}
      />

      {jobsQuery.isPending || availabilityQuery.isPending ? (
        <CalendarSkeleton />
      ) : jobsQuery.isError && isNotImplemented(jobsQuery.error) ? (
        <NotYetAvailable
          title="Calendar isn't available yet"
          description="Job assignment is still being built on the platform side. Once your account can receive bookings, your week will show up here."
        />
      ) : jobsQuery.isError || availabilityQuery.isError ? (
        <ErrorState
          title="Couldn't load your calendar"
          error={jobsQuery.error ?? availabilityQuery.error}
          onRetry={() => {
            void jobsQuery.refetch();
            void availabilityQuery.refetch();
          }}
          isRetrying={jobsQuery.isRefetching || availabilityQuery.isRefetching}
        />
      ) : (
        <div className="flex flex-col gap-3">
          {dates.map((date) => (
            <DayCard
              key={date}
              date={date}
              windows={availabilityQuery.data.windows}
              jobs={jobsQuery.data.filter((job) => job.slotDate === date && isActiveJobStatus(job.status))}
              conflicts={conflicts}
            />
          ))}
        </div>
      )}
    </div>
  );
}

function WeekNav({
  weekStart,
  isCurrentWeek,
  rangeLabel,
  onPrev,
  onNext,
  onToday,
}: {
  weekStart: Date;
  isCurrentWeek: boolean;
  rangeLabel: string;
  onPrev: () => void;
  onNext: () => void;
  onToday: () => void;
}) {
  return (
    <Card flush>
      <div className="flex items-center justify-between gap-3 p-3">
        <IconButton label="Previous week" onClick={onPrev}>
          <ChevronLeftIcon />
        </IconButton>

        <div className="flex flex-col items-center gap-0.5 text-center" aria-live="polite">
          <p className="nums text-sm font-semibold text-fg">{rangeLabel}</p>
          {isCurrentWeek ? (
            <span className="text-xs text-fg-subtle">This week</span>
          ) : (
            <button
              type="button"
              onClick={onToday}
              className="text-xs font-medium text-brand-600 transition-colors duration-fast ease-out hover:underline dark:text-brand-400"
            >
              Jump to this week
            </button>
          )}
        </div>

        <IconButton label="Next week" onClick={onNext}>
          <ChevronRightIcon />
        </IconButton>
      </div>
      {/* weekStart isn't read here - reserved for a future "print this week" /
          export affordance, kept out of scope for this pass. */}
      <span className="sr-only">{weekStart.toString()}</span>
    </Card>
  );
}

/** One day of the visible week: its declared availability plus whatever committed jobs land on it. */
function DayCard({
  date,
  windows,
  jobs,
  conflicts,
}: {
  date: string;
  windows: readonly AvailabilityWindow[];
  jobs: JobListItem[];
  conflicts: ReadonlyMap<string, JobConflict>;
}) {
  const dayWindows = windows.filter((w) => w.dayOfWeek === dayOfWeekOf(date));
  const isToday = date === todayIsoDate();
  const sortedJobs = jobs.slice().sort((a, b) => (a.slotStartTimeSnapshot < b.slotStartTimeSnapshot ? -1 : 1));

  return (
    <section
      className={cx(
        "w-full overflow-hidden rounded-2xl bg-surface p-4 shadow-sm",
        isToday && "ring-1 ring-brand-600/40",
      )}
    >
      <div className="flex flex-wrap items-center gap-2">
        <h2 className="text-sm font-semibold text-fg">{WEEKDAY_LABELS[dayOfWeekOf(date)]}</h2>
        <span className="nums text-sm text-fg-muted">{formatIsoDate(date)}</span>
        {isToday ? <Badge tone="brand">Today</Badge> : null}
      </div>
      <p className="mt-1 text-sm leading-relaxed text-fg-muted">
        {dayWindows.length === 0
          ? "Not available"
          : dayWindows.map((w) => `${formatTime(w.startTime)}–${formatTime(w.endTime)}`).join(", ")}
      </p>

      <div className="mt-3">
        {sortedJobs.length === 0 ? (
          <p className="text-sm text-fg-subtle">No jobs scheduled.</p>
        ) : (
          <div className="flex flex-col gap-2">
            {sortedJobs.map((job) => (
              <JobRow key={job.assignmentId} job={job} conflict={conflicts.get(job.assignmentId)} />
            ))}
          </div>
        )}
      </div>
    </section>
  );
}

/** One committed job on a day card - a compact row, not the full JobCard (this screen's job is spotting the week's shape and its conflicts, not re-showing every field `/jobs` already does). */
function JobRow({ job, conflict }: { job: JobListItem; conflict: JobConflict | undefined }) {
  const hasConflict = conflict !== undefined;

  return (
    <Link
      href={`/jobs/${job.bookingId}`}
      className={cx(
        "flex flex-col gap-1.5 rounded-xl border p-3 transition-colors duration-fast ease-out hover:border-line-strong",
        hasConflict ? "border-danger/50 bg-danger-soft/40" : "border-line bg-surface-2",
      )}
    >
      <div className="flex items-center justify-between gap-2">
        <span className="nums text-sm font-medium text-fg">
          {formatTime(job.slotStartTimeSnapshot)}–{formatTime(job.slotEndTimeSnapshot)}
        </span>
        <JobStatusBadge status={job.status} />
      </div>
      <p className="truncate text-sm text-fg-muted">{job.customerNameSnapshot}</p>
      {hasConflict ? (
        <div className="flex flex-wrap gap-1.5">
          {conflict.outsideAvailability ? <Badge tone="danger">Outside availability</Badge> : null}
          {conflict.overlapsWithAssignmentIds.length > 0 ? <Badge tone="danger">Double-booked</Badge> : null}
        </div>
      ) : null}
    </Link>
  );
}

/** Mirrors the real week's shape (nav bar + 7 day cards) so nothing jumps when data lands. */
function CalendarSkeleton() {
  return (
    <div className="flex flex-col gap-3" aria-hidden>
      {Array.from({ length: 7 }, (_, index) => (
        <div key={index} className="flex flex-col gap-3 rounded-2xl bg-surface p-6 shadow-sm">
          <Skeleton className="h-5 w-40" />
          <Skeleton className="h-4 w-56" />
          <Skeleton className="h-14 w-full rounded-xl" />
        </div>
      ))}
    </div>
  );
}

function ChevronLeftIcon() {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2.25"
      strokeLinecap="round"
      strokeLinejoin="round"
      className="h-4 w-4"
      aria-hidden
    >
      <path d="M15 6l-6 6 6 6" />
    </svg>
  );
}

function ChevronRightIcon() {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2.25"
      strokeLinecap="round"
      strokeLinejoin="round"
      className="h-4 w-4"
      aria-hidden
    >
      <path d="M9 6l6 6-6 6" />
    </svg>
  );
}
