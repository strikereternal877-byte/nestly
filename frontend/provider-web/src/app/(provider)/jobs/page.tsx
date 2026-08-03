"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useState } from "react";
import type { FormEvent } from "react";
import { Alert, Button, Card, Field, PageHeading, Select } from "@/components/ui";
import { describeError, isNotImplemented } from "@/lib/api";
import { listJobs } from "@/lib/jobs-api";
import { jobStatusLabel } from "@/lib/jobs-types";

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
 * filterable by status/date. The backend behind this surface is currently a
 * stub returning HTTP 501 (sibling task #147 not yet merged) - that renders
 * as its own "not yet available" state below rather than a hard error,
 * since it is an expected, temporary condition rather than a failure.
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
    <div className="mx-auto w-full max-w-5xl">
      <PageHeading title="Jobs" subtitle="Bookings assigned to you - accept, reject and track progress." />

      <Card title="Filters">
        <form onSubmit={onSubmit} className="flex flex-wrap items-end gap-3">
          <div className="w-48">
            <Select label="Status" options={STATUS_OPTIONS} value={status} onChange={(e) => setStatus(e.target.value)} />
          </div>
          <div className="w-48">
            <Field label="Date" type="date" value={date} onChange={(e) => setDate(e.target.value)} />
          </div>
          <Button type="submit">Search</Button>
          <Button type="button" variant="secondary" onClick={onClear}>
            Clear
          </Button>
        </form>
      </Card>

      <div className="mt-6">
        {query.isPending ? (
          <p className="text-sm text-neutral-500">Loading jobs…</p>
        ) : query.isError && isNotImplemented(query.error) ? (
          <Card title="Jobs aren't available yet">
            <p className="text-sm text-neutral-600 dark:text-neutral-400">
              Job assignment is still being built on the platform side. Check back once your account can receive
              bookings.
            </p>
          </Card>
        ) : query.isError ? (
          <Alert>{describeError(query.error)}</Alert>
        ) : query.data.length === 0 ? (
          <Card title="No jobs match these filters">
            <p className="text-sm text-neutral-600 dark:text-neutral-400">Try broadening your search criteria.</p>
          </Card>
        ) : (
          <div className="overflow-x-auto rounded-xl border border-black/10 dark:border-white/15">
            <table className="w-full min-w-[640px] text-left text-sm">
              <thead className="border-b border-black/10 bg-black/5 dark:border-white/15 dark:bg-white/5">
                <tr>
                  <th className="px-4 py-3 font-medium">Booking</th>
                  <th className="px-4 py-3 font-medium">Status</th>
                  <th className="px-4 py-3 font-medium">Assigned</th>
                  <th className="px-4 py-3 font-medium">Response deadline</th>
                </tr>
              </thead>
              <tbody>
                {query.data.map((job) => (
                  <tr
                    key={job.assignmentId}
                    className="border-b border-black/5 last:border-0 hover:bg-black/[.03] dark:border-white/10 dark:hover:bg-white/[.05]"
                  >
                    <td className="px-4 py-3">
                      <Link href={`/jobs/${job.bookingId}`} className="font-medium underline-offset-2 hover:underline">
                        {job.bookingId}
                      </Link>
                    </td>
                    <td className="px-4 py-3">{jobStatusLabel(job.status)}</td>
                    <td className="px-4 py-3">{new Date(job.assignedAt).toLocaleString()}</td>
                    <td className="px-4 py-3">
                      {job.responseDeadline ? new Date(job.responseDeadline).toLocaleString() : "—"}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}
