"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useState } from "react";
import { Alert, Button, Card, EmptyState, Field, PageHeading, SkeletonText } from "@/components/ui";
import { Breadcrumbs, ConfirmDialog, formatDateTime } from "@/components/data-table";
import { SectionError } from "@/components/screen-states";
import { describeError } from "@/lib/api";
import { approveCompletionProof, listPendingCompletionProofs, rejectCompletionProof } from "@/lib/bookings-api";
import { useAdminClaims } from "@/lib/use-admin-claims";
import { BookingsTabs } from "@/components/BookingsTabs";

/** Completion-proof photos are restricted server-side to JPEG/PNG/WebP - same check the booking detail page's own CompletionProofCard would need if it rendered non-image refs. */
function isImageFileRef(fileRef: string): boolean {
  try {
    return /\.(jpe?g|png|webp)$/i.test(new URL(fileRef).pathname);
  } catch {
    return false;
  }
}

/**
 * The completion-proof review queue (Order/Booking Management UX pass,
 * mirrors providers/verification's KYC queue): every provider-submitted
 * completion proof still awaiting an admin verdict, across every booking, in
 * one worklist - instead of the previous "search for a specific booking,
 * then check its Overview tab" flow (which, until a status-guard regression
 * this pass also fixed, didn't even render there while a proof was actually
 * pending).
 *
 * Approving here transitions the booking straight to Completed as the direct
 * consequence (BookingManagementService.ApproveCompletionProofAsync) - there
 * is no separate "now mark it Completed" step for the admin to remember.
 * Reuses the exact approve/reject mutations the booking detail page's own
 * CompletionProofCard calls, not a second implementation of the review
 * action.
 */
export default function CompletionProofQueuePage() {
  const claims = useAdminClaims();
  const canWrite = claims?.permissions.includes("bookings.write") ?? false;
  const queryClient = useQueryClient();

  const queueQuery = useQuery({
    queryKey: ["admin-booking-completion-proof-queue"],
    queryFn: () => listPendingCompletionProofs(),
  });

  const [actionError, setActionError] = useState<string | null>(null);
  const [actionNotice, setActionNotice] = useState<string | null>(null);
  const [rejectReasonByBooking, setRejectReasonByBooking] = useState<Record<string, string>>({});
  const [pendingRejection, setPendingRejection] = useState<{ bookingId: string; label: string } | null>(null);

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["admin-booking-completion-proof-queue"] });

  const approveMutation = useMutation({
    mutationFn: (bookingId: string) => approveCompletionProof(bookingId),
    onSuccess: () => {
      setActionError(null);
      setActionNotice("Completion proof approved - the booking is now Completed.");
      invalidate();
    },
    onError: (err) => setActionError(describeError(err)),
  });

  const rejectMutation = useMutation({
    mutationFn: ({ bookingId, reason }: { bookingId: string; reason: string }) => rejectCompletionProof(bookingId, { reason }),
    onSuccess: () => {
      setPendingRejection(null);
      setActionError(null);
      setActionNotice("Completion proof rejected - the provider can resubmit.");
      invalidate();
    },
    onError: (err) => setActionError(describeError(err)),
  });

  return (
    <div className="flex w-full max-w-5xl flex-col gap-6">
      <PageHeading
        title="Completion proofs"
        subtitle="Every provider-submitted completion proof still awaiting a verdict, oldest first. Approving completes the booking directly."
        breadcrumbs={<Breadcrumbs items={[{ label: "Bookings", href: "/bookings" }, { label: "Completion proofs" }]} />}
      />
      <BookingsTabs />

      {actionError ? <Alert tone="error">{actionError}</Alert> : null}
      {actionNotice ? <Alert tone="success">{actionNotice}</Alert> : null}

      <Card title="Pending completion proofs" description="Approve to complete the booking, or reject with a reason so the provider can finish the job and resubmit.">
        {queueQuery.isPending ? (
          <SkeletonText lines={4} />
        ) : queueQuery.isError ? (
          <SectionError error={queueQuery.error} onRetry={() => queueQuery.refetch()} />
        ) : queueQuery.data.length === 0 ? (
          <EmptyState title="Nothing pending" description="Every submitted completion proof has been reviewed." />
        ) : (
          <ul className="flex flex-col gap-3 text-sm">
            {queueQuery.data.map((item) => (
              <li key={item.bookingId} className="rounded-xl border border-line p-3">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <Link
                    href={`/bookings/${item.bookingId}`}
                    className="font-medium text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
                  >
                    {item.bookingReference}
                  </Link>
                  <span className="nums text-xs text-fg-subtle">Submitted {formatDateTime(item.submittedAtUtc)}</span>
                </div>
                <p className="mt-1 text-xs text-fg-subtle">
                  {item.customerName} · completed by {item.providerDisplayName}
                </p>

                {item.photoRefs.length > 0 ? (
                  <div className="mt-3 flex flex-wrap items-center gap-3 border-t border-line pt-3">
                    {item.photoRefs.map((ref, i) =>
                      isImageFileRef(ref) ? (
                        // eslint-disable-next-line @next/next/no-img-element
                        <img
                          key={i}
                          src={ref}
                          alt={`Completion photo ${i + 1} for ${item.bookingReference}`}
                          className="h-16 w-16 shrink-0 rounded-lg border border-line object-cover"
                        />
                      ) : (
                        <a
                          key={i}
                          href={ref}
                          target="_blank"
                          rel="noopener noreferrer"
                          className="text-sm font-medium text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
                        >
                          View photo {i + 1}
                        </a>
                      ),
                    )}
                  </div>
                ) : null}

                {item.checklistAnswers.length > 0 ? (
                  <ul className="mt-3 flex flex-col gap-1 border-t border-line pt-3 text-sm text-fg">
                    {item.checklistAnswers.map((answer, i) => (
                      <li key={i}>
                        <span className={answer.completed ? "text-success" : "text-fg-subtle"} aria-hidden>
                          {answer.completed ? "✓" : "○"}
                        </span>{" "}
                        {answer.item}
                        {answer.notes ? <span className="text-fg-muted"> — {answer.notes}</span> : null}
                      </li>
                    ))}
                  </ul>
                ) : null}

                {canWrite ? (
                  <div className="mt-3 flex flex-col gap-3 border-t border-line pt-3 sm:flex-row sm:items-end">
                    <Button
                      variant="secondary"
                      loading={approveMutation.isPending && approveMutation.variables === item.bookingId}
                      onClick={() => approveMutation.mutate(item.bookingId)}
                    >
                      Approve
                    </Button>
                    <div className="flex-1">
                      <Field
                        label="Rejection reason"
                        value={rejectReasonByBooking[item.bookingId] ?? ""}
                        onChange={(e) => setRejectReasonByBooking((m) => ({ ...m, [item.bookingId]: e.target.value }))}
                      />
                    </div>
                    <Button
                      variant="danger"
                      disabled={!(rejectReasonByBooking[item.bookingId] ?? "").trim()}
                      onClick={() => setPendingRejection({ bookingId: item.bookingId, label: item.bookingReference })}
                    >
                      Reject
                    </Button>
                  </div>
                ) : null}
              </li>
            ))}
          </ul>
        )}
      </Card>

      <ConfirmDialog
        open={pendingRejection !== null}
        title="Reject this completion proof?"
        description="The booking stays In Progress so the provider can finish the job and resubmit."
        confirmLabel="Reject proof"
        cancelLabel="Keep pending"
        loading={rejectMutation.isPending}
        error={rejectMutation.isError ? describeError(rejectMutation.error) : null}
        onCancel={() => setPendingRejection(null)}
        onConfirm={() => {
          if (!pendingRejection) return;
          rejectMutation.mutate({
            bookingId: pendingRejection.bookingId,
            reason: (rejectReasonByBooking[pendingRejection.bookingId] ?? "").trim(),
          });
        }}
      >
        {pendingRejection ? (
          <p className="text-sm text-fg-muted">
            {pendingRejection.label} —{" "}
            <span className="font-medium text-fg">{rejectReasonByBooking[pendingRejection.bookingId] ?? ""}</span>
          </p>
        ) : null}
      </ConfirmDialog>
    </div>
  );
}
