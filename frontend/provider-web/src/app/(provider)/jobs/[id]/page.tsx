"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useParams, useRouter } from "next/navigation";
import { useState } from "react";
import { Alert, Button, Card, Field, PageHeading } from "@/components/ui";
import { describeError, isNotImplemented } from "@/lib/api";
import {
  acceptJob,
  completeJob,
  getCompletionVerification,
  getJobDetail,
  rejectJob,
  startJob,
  submitCompletionProof,
  submitCompletionVerification,
} from "@/lib/jobs-api";
import { JobStatus, jobStatusLabel } from "@/lib/jobs-types";
import type { BookingCompletionProofResponse, CompletionChecklistAnswer } from "@/lib/jobs-types";

/**
 * Job detail (docs/PROVIDER.md's `booking_provider_assignment` bridge table):
 * accept/reject/start/complete actions plus completion proof submission.
 * Same 501-stub caveat as the list page - the backend for this surface is
 * pending sibling task #147, so this renders an explicit "not yet available"
 * state instead of a hard error.
 */
export default function JobDetailPage() {
  const params = useParams<{ id: string }>();
  const jobId = params.id;
  const router = useRouter();
  const queryClient = useQueryClient();
  const [proofRef, setProofRef] = useState("");

  const query = useQuery({ queryKey: ["provider-job", jobId], queryFn: () => getJobDetail(jobId) });
  const verificationQuery = useQuery({
    queryKey: ["provider-job-completion-verification", jobId],
    queryFn: () => getCompletionVerification(jobId),
  });

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: ["provider-job", jobId] });
    queryClient.invalidateQueries({ queryKey: ["provider-job-completion-verification", jobId] });
  };

  const acceptMutation = useMutation({ mutationFn: () => acceptJob(jobId), onSuccess: invalidate });
  const rejectMutation = useMutation({ mutationFn: () => rejectJob(jobId), onSuccess: invalidate });
  const startMutation = useMutation({ mutationFn: () => startJob(jobId), onSuccess: invalidate });
  const completeMutation = useMutation({ mutationFn: () => completeJob(jobId), onSuccess: invalidate });
  const proofMutation = useMutation({
    mutationFn: () => submitCompletionProof(jobId, { proofRef }),
    onSuccess: () => {
      setProofRef("");
      invalidate();
    },
  });

  const anyActionPending =
    acceptMutation.isPending || rejectMutation.isPending || startMutation.isPending || completeMutation.isPending;

  if (query.isPending) {
    return <p className="p-2 text-sm text-neutral-500">Loading job…</p>;
  }

  if (query.isError && isNotImplemented(query.error)) {
    return (
      <div className="mx-auto w-full max-w-2xl">
        <PageHeading title="Job detail" />
        <Card title="This job isn't available yet">
          <p className="text-sm text-neutral-600 dark:text-neutral-400">
            Job assignment is still being built on the platform side. Check back once your account can receive
            bookings.
          </p>
          <div className="mt-4">
            <Button type="button" variant="secondary" onClick={() => router.push("/jobs")}>
              Back to jobs
            </Button>
          </div>
        </Card>
      </div>
    );
  }

  if (query.isError) {
    return (
      <div className="mx-auto w-full max-w-2xl">
        <PageHeading title="Job detail" />
        <Alert>{describeError(query.error)}</Alert>
      </div>
    );
  }

  const job = query.data;
  const actionError =
    acceptMutation.error ?? rejectMutation.error ?? startMutation.error ?? completeMutation.error ?? proofMutation.error;

  return (
    <div className="mx-auto w-full max-w-2xl">
      <PageHeading title={`Job ${job.bookingId}`} subtitle={`Status: ${jobStatusLabel(job.status)}`} />

      {actionError ? (
        <div className="mb-4">
          <Alert>{describeError(actionError)}</Alert>
        </div>
      ) : null}

      <Card title="Details">
        <dl className="grid grid-cols-1 gap-3 text-sm sm:grid-cols-2">
          <div>
            <dt className="font-medium">Booking ID</dt>
            <dd>{job.bookingId}</dd>
          </div>
          <div>
            <dt className="font-medium">Status</dt>
            <dd>{jobStatusLabel(job.status)}</dd>
          </div>
          <div>
            <dt className="font-medium">Assigned</dt>
            <dd>{new Date(job.assignedAt).toLocaleString()}</dd>
          </div>
          <div>
            <dt className="font-medium">Response deadline</dt>
            <dd>{job.responseDeadline ? new Date(job.responseDeadline).toLocaleString() : "—"}</dd>
          </div>
          <div>
            <dt className="font-medium">Customer</dt>
            <dd>
              {job.customerNameSnapshot} ({job.customerMobileSnapshot})
            </dd>
          </div>
          <div>
            <dt className="font-medium">Slot</dt>
            <dd>
              {job.slotDate} {job.slotStartTimeSnapshot}–{job.slotEndTimeSnapshot}
            </dd>
          </div>
          <div className="sm:col-span-2">
            <dt className="font-medium">Address</dt>
            <dd>
              {job.addressLine1Snapshot}
              {job.addressLine2Snapshot ? `, ${job.addressLine2Snapshot}` : ""}, {job.addressCitySnapshot}{" "}
              {job.addressPincodeSnapshot}
            </dd>
          </div>
          {job.items.length > 0 ? (
            <div className="sm:col-span-2">
              <dt className="font-medium">Items</dt>
              <dd>{job.items.map((item) => `${item.nameSnapshot} × ${item.quantity}`).join(", ")}</dd>
            </div>
          ) : null}
        </dl>
      </Card>

      <div className="mt-6">
        <Card title="Actions">
          <div className="flex flex-wrap gap-2">
            {job.status === JobStatus.Assigned ? (
              <>
                <Button type="button" disabled={anyActionPending} onClick={() => acceptMutation.mutate()}>
                  {acceptMutation.isPending ? "Accepting…" : "Accept"}
                </Button>
                <Button
                  type="button"
                  variant="danger"
                  disabled={anyActionPending}
                  onClick={() => rejectMutation.mutate()}
                >
                  {rejectMutation.isPending ? "Rejecting…" : "Reject"}
                </Button>
              </>
            ) : null}
            {job.status === JobStatus.Accepted ? (
              <Button type="button" disabled={anyActionPending} onClick={() => startMutation.mutate()}>
                {startMutation.isPending ? "Starting…" : "Start job"}
              </Button>
            ) : null}
            {job.status === JobStatus.InProgress ? (
              <Button
                type="button"
                disabled={anyActionPending || !verificationQuery.data}
                title={verificationQuery.data ? undefined : "Submit completion verification (photos + checklist) below first"}
                onClick={() => completeMutation.mutate()}
              >
                {completeMutation.isPending ? "Completing…" : "Mark complete"}
              </Button>
            ) : null}
            {job.status !== JobStatus.Assigned && job.status !== JobStatus.Accepted && job.status !== JobStatus.InProgress ? (
              <p className="text-sm text-neutral-600 dark:text-neutral-400">No actions available for this status.</p>
            ) : null}
          </div>

          {job.status === JobStatus.InProgress || job.status === JobStatus.Completed ? (
            <form
              onSubmit={(e) => {
                e.preventDefault();
                proofMutation.mutate();
              }}
              className="mt-5 flex flex-wrap items-end gap-3 border-t border-black/10 pt-4 dark:border-white/15"
            >
              {/* Same "no upload backend yet" caveat as the KYC document form -
                  this is a text reference, not a real file upload. */}
              <div className="w-72">
                <Field
                  label="Completion proof (file reference or URL)"
                  value={proofRef}
                  onChange={(e) => setProofRef(e.target.value)}
                  placeholder="https://…"
                />
              </div>
              <Button type="submit" disabled={proofMutation.isPending || proofRef.trim() === ""}>
                {proofMutation.isPending ? "Submitting…" : "Submit completion proof"}
              </Button>
            </form>
          ) : null}
        </Card>
      </div>

      {job.status === JobStatus.InProgress || job.status === JobStatus.Completed ? (
        <div className="mt-6">
          <CompletionVerificationCard
            jobId={jobId}
            existing={verificationQuery.data}
            isLoading={verificationQuery.isPending}
            onSubmitted={invalidate}
          />
        </div>
      ) : null}
    </div>
  );
}

/**
 * Photos + checklist evidence required before `completeJob` succeeds
 * (tasks 195-197) - distinct from the single legacy proof-ref field above.
 * Resubmitting replaces the previous evidence (same behavior as the backend).
 */
function CompletionVerificationCard({
  jobId,
  existing,
  isLoading,
  onSubmitted,
}: {
  jobId: string;
  existing: BookingCompletionProofResponse | undefined;
  isLoading: boolean;
  onSubmitted: () => void;
}) {
  const [photoRefsText, setPhotoRefsText] = useState("");
  const [checklist, setChecklist] = useState<CompletionChecklistAnswer[]>([{ item: "", completed: false, notes: null }]);

  const mutation = useMutation({
    mutationFn: () =>
      submitCompletionVerification(jobId, {
        photoRefs: photoRefsText
          .split("\n")
          .map((r) => r.trim())
          .filter((r) => r.length > 0),
        checklistAnswers: checklist.filter((a) => a.item.trim() !== ""),
      }),
    onSuccess: onSubmitted,
  });

  function updateChecklistItem(index: number, patch: Partial<CompletionChecklistAnswer>) {
    setChecklist((prev) => prev.map((a, i) => (i === index ? { ...a, ...patch } : a)));
  }

  if (isLoading) {
    return (
      <Card title="Completion verification">
        <p className="text-sm text-neutral-500">Loading…</p>
      </Card>
    );
  }

  return (
    <Card title="Completion verification">
      {existing ? (
        <div className="mb-4 rounded-lg border border-black/10 p-3 text-sm dark:border-white/15">
          <p className="font-medium">Submitted {new Date(existing.submittedAtUtc).toLocaleString()}</p>
          <p className="mt-1 text-neutral-600 dark:text-neutral-400">{existing.photoRefs.length} photo(s)</p>
          {existing.checklistAnswers.length > 0 ? (
            <ul className="mt-2 flex flex-col gap-1">
              {existing.checklistAnswers.map((a, i) => (
                <li key={i}>
                  {a.completed ? "✓" : "○"} {a.item}
                  {a.notes ? ` — ${a.notes}` : ""}
                </li>
              ))}
            </ul>
          ) : null}
        </div>
      ) : (
        <p className="mb-4 text-sm text-neutral-600 dark:text-neutral-400">
          Required before this job can be marked complete.
        </p>
      )}

      {mutation.isError ? (
        <div className="mb-3">
          <Alert>{describeError(mutation.error)}</Alert>
        </div>
      ) : null}

      <form
        onSubmit={(e) => {
          e.preventDefault();
          mutation.mutate();
        }}
        className="flex flex-col gap-4"
      >
        <div>
          <label htmlFor="photo-refs" className="text-sm font-medium">
            Photo references (one per line)
          </label>
          <textarea
            id="photo-refs"
            rows={3}
            value={photoRefsText}
            onChange={(e) => setPhotoRefsText(e.target.value)}
            placeholder="https://…"
            className="mt-1.5 w-full rounded-lg border border-black/15 bg-transparent px-3 py-2 text-sm outline-none focus:border-black focus:ring-1 focus:ring-black dark:border-white/20 dark:focus:border-white dark:focus:ring-white"
          />
        </div>

        <div className="flex flex-col gap-2">
          <span className="text-sm font-medium">Checklist</span>
          {checklist.map((answer, index) => (
            <div key={index} className="flex items-center gap-2">
              <input
                type="checkbox"
                checked={answer.completed}
                onChange={(e) => updateChecklistItem(index, { completed: e.target.checked })}
              />
              <input
                type="text"
                value={answer.item}
                onChange={(e) => updateChecklistItem(index, { item: e.target.value })}
                placeholder="Checklist item"
                className="flex-1 rounded-lg border border-black/15 bg-transparent px-3 py-2 text-sm outline-none focus:border-black focus:ring-1 focus:ring-black dark:border-white/20 dark:focus:border-white dark:focus:ring-white"
              />
            </div>
          ))}
          <Button
            type="button"
            variant="secondary"
            className="w-fit"
            onClick={() => setChecklist((prev) => [...prev, { item: "", completed: false, notes: null }])}
          >
            Add checklist item
          </Button>
        </div>

        <Button type="submit" disabled={mutation.isPending || photoRefsText.trim() === ""} className="w-fit">
          {mutation.isPending ? "Submitting…" : existing ? "Resubmit" : "Submit"}
        </Button>
      </form>
    </Card>
  );
}
