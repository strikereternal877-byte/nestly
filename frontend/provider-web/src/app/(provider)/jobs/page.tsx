"use client";

import { useQuery } from "@tanstack/react-query";
import { motion } from "motion/react";
import Link from "next/link";
import { useState } from "react";
import type { FormEvent } from "react";
import { Reveal, RevealItem, SPRING } from "@/components/motion";
import { ErrorState, NotYetAvailable } from "@/components/states";
import {
  Button,
  Card,
  EmptyState,
  Field,
  PageHeading,
  Select,
  Skeleton,
  cx,
} from "@/components/ui";
import { isNotImplemented } from "@/lib/api";
import { formatDateTime, formatInr, formatIsoDate, formatTime } from "@/lib/format";
import { listJobs } from "@/lib/jobs-api";
import { JobStatus } from "@/lib/jobs-types";
import { JobStatusBadge } from "./_components/JobStatusBadge";
import { RecurringJobBadge } from "./_components/RecurringJobBadge";
import type { JobListItem } from "@/lib/jobs-types";

const STATUS_OPTIONS = [
  { value: "", label: "Any status" },
  { value: "Assigned", label: "Assigned" },
  { value: "Accepted", label: "Accepted" },
  { value: "Rejected", label: "Rejected" },
  { value: "Reassigned", label: "Reassigned" },
  { value: "InProgress", label: "In progress" },
  { value: "Completed", label: "Completed" },
];

/**
 * Job list (docs/PROVIDER.md's `booking_provider_assignment` bridge table):
 * filterable by status/date. The backend behind this surface can answer HTTP
 * 501 while sibling task #147 is undeployed - that renders as its own
 * "not yet available" state rather than a hard error, since it is an
 * expected, temporary condition rather than a failure.
 *
 * Rows are cards, not a table: providers read this on a phone in the field,
 * and the fields that decide whether to accept a job (who, where, when, how
 * much, and how long they have to answer) do not fit a phone-width table.
 */
export default function JobsPage() {
  const [status, setStatus] = useState("");
  const [date, setDate] = useState("");
  const [appliedStatus, setAppliedStatus] = useState("");
  const [appliedDate, setAppliedDate] = useState("");

  const query = useQuery({
    queryKey: ["provider-jobs", appliedStatus, appliedDate],
    queryFn: () => listJobs({ status: appliedStatus || undefined, date: appliedDate || undefined }),
  });

  const hasFilters = appliedStatus !== "" || appliedDate !== "";

  const onSubmit = (e: FormEvent) => {
    e.preventDefault();
    setAppliedStatus(status);
    setAppliedDate(date);
  };

  const onClear = () => {
    setStatus("");
    setDate("");
    setAppliedStatus("");
    setAppliedDate("");
  };

  return (
    <div>
      <PageHeading
        title="Jobs"
        subtitle="Bookings assigned to you — accept, decline and track progress."
      />

      <Card title="Filters" description="Narrow the list to a status or a single day.">
        <form onSubmit={onSubmit} className="flex flex-col gap-3 sm:flex-row sm:items-end">
          <div className="sm:w-48">
            <Select
              label="Status"
              options={STATUS_OPTIONS}
              value={status}
              onChange={(e) => setStatus(e.target.value)}
            />
          </div>
          <div className="sm:w-48">
            <Field
              label="Date"
              type="date"
              value={date}
              onChange={(e) => setDate(e.target.value)}
            />
          </div>
          <div className="flex gap-2 sm:pb-0">
            <Button type="submit" className="flex-1 sm:flex-none">
              Search
            </Button>
            <Button
              type="button"
              variant="secondary"
              onClick={onClear}
              disabled={status === "" && date === "" && !hasFilters}
              className="flex-1 sm:flex-none"
            >
              Clear
            </Button>
          </div>
        </form>
      </Card>

      <div className="mt-6">
        {query.isPending ? (
          <JobListSkeleton />
        ) : query.isError && isNotImplemented(query.error) ? (
          <NotYetAvailable
            title="Jobs aren't available yet"
            description="Job assignment is still being built on the platform side. Once your account can receive bookings, they will appear here."
          />
        ) : query.isError ? (
          <ErrorState
            title="Couldn't load your jobs"
            error={query.error}
            onRetry={() => query.refetch()}
            isRetrying={query.isRefetching}
          />
        ) : query.data.length === 0 ? (
          <EmptyState
            title={hasFilters ? "No jobs match these filters" : "No jobs yet"}
            description={
              hasFilters
                ? "Try a different status or day — or clear the filters to see everything assigned to you."
                : "Jobs assigned to you will appear here. Keep your availability and service areas up to date so you get matched."
            }
            action={
              hasFilters ? (
                <Button variant="secondary" onClick={onClear}>
                  Clear filters
                </Button>
              ) : (
                // With nothing assigned yet, the useful next step is the thing
                // that actually gets a provider matched to work.
                <Link href="/availability">
                  <Button variant="secondary">Update your availability</Button>
                </Link>
              )
            }
          />
        ) : (
          <Reveal as="ul" className="grid gap-3 sm:grid-cols-2">
            {query.data.map((job) => (
              <RevealItem key={job.assignmentId}>
                <JobCard job={job} />
              </RevealItem>
            ))}
          </Reveal>
        )}
      </div>
    </div>
  );
}

/**
 * One assignment. The whole card is the link target rather than a small
 * "view" affordance in a corner - it is the only action on the row, and a
 * card-sized tap target is what makes this usable one-handed.
 */
function JobCard({ job }: { job: JobListItem }) {
  const needsResponse = job.status === JobStatus.Assigned;
  const isRecurring = job.recurringBookingPlanId !== null;

  return (
    <Link href={`/jobs/${job.bookingId}`} className="group block h-full">
      <motion.div
        whileHover={{ y: -5 }}
        whileTap={{ scale: 0.97 }}
        transition={SPRING}
        className={cx(
          "flex h-full flex-col gap-3 rounded-2xl border bg-surface p-4 shadow-sm transition duration-fast ease-out",
          "group-hover:border-line-strong group-hover:shadow-md",
          needsResponse ? "border-warning/40" : "border-line",
        )}
      >
        <div className="flex items-start justify-between gap-3">
          <div className="flex min-w-0 flex-wrap items-center gap-1.5">
            <JobStatusBadge status={job.status} />
            {/* Sits beside the status rather than below the customer's name:
                whether this is a standing customer changes how a provider
                reads the whole card, so it has to be in the first thing they
                look at, not a footnote. */}
            {isRecurring ? <RecurringJobBadge frequency={job.recurringFrequency} /> : null}
          </div>
          {/* Net payout, not the customer's gross booking total (bug fix,
              docs/OPEN-FIXES-FEATURES.csv "Payout figure") - job detail
              breaks it down further into booking total minus commission. */}
          <span className="nums shrink-0 text-sm font-semibold text-fg">
            {formatInr(job.netAmountToProvider)}
          </span>
        </div>

        <div className="min-w-0">
          <p className="truncate text-base font-semibold text-fg">{job.customerNameSnapshot}</p>
          <p className="mt-0.5 nums text-sm text-fg-muted">
            {formatIsoDate(job.slotDate)} · {formatTime(job.slotStartTimeSnapshot)}–
            {formatTime(job.slotEndTimeSnapshot)}
          </p>
          <p className="nums mt-0.5 text-xs text-fg-subtle">{job.bookingReference}</p>
        </div>

        <p className="line-clamp-2 text-sm leading-relaxed text-fg-subtle">
          {job.addressLine1Snapshot}, {job.addressCitySnapshot} {job.addressPincodeSnapshot}
        </p>

        {needsResponse && job.responseDeadline ? (
          <p className="mt-auto flex items-center gap-1.5 rounded-lg bg-warning-soft px-2.5 py-1.5 text-xs font-medium text-warning">
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
            Respond by {formatDateTime(job.responseDeadline)}
          </p>
        ) : null}
      </motion.div>
    </Link>
  );
}

/** Matches JobCard's real height so the list does not jump when data lands. */
function JobListSkeleton() {
  return (
    <ul className="grid gap-3 sm:grid-cols-2" aria-hidden>
      {Array.from({ length: 4 }, (_, index) => (
        <li
          key={index}
          className="flex flex-col gap-3 rounded-2xl bg-surface p-4 shadow-sm"
        >
          <div className="flex items-start justify-between gap-3">
            <Skeleton className="h-5 w-20 rounded-full" />
            <Skeleton className="h-5 w-16" />
          </div>
          <Skeleton className="h-5 w-40" />
          <Skeleton className="h-4 w-48" />
          <Skeleton className="h-4 w-full" />
        </li>
      ))}
    </ul>
  );
}
