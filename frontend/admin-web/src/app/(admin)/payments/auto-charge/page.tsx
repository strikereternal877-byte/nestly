"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useState } from "react";
import { Alert, Badge, Button, Card, EmptyState, PageHeading, SkeletonText } from "@/components/ui";
import { Breadcrumbs, ConfirmDialog, formatCurrency, formatDateTime } from "@/components/data-table";
import { SectionError } from "@/components/screen-states";
import { describeError } from "@/lib/api";
import { cancelAutoChargeRetries, forceAutoChargeRetry, listAutoChargeCandidates } from "@/lib/bookings-api";
import type { AdminAutoChargeCandidate } from "@/lib/bookings-types";
import { PaymentsTabs } from "@/components/PaymentsTabs";
import { useAdminClaims } from "@/lib/use-admin-claims";

/** "Due now" / "in 3h" / "not scheduled" - a plain-language read of when the next attempt happens, since raw due timestamps in the near past/future are harder to scan than a relative phrase. */
function dueLabel(nextAttemptDueAtUtc: string | null): string {
  if (nextAttemptDueAtUtc === null) {
    return "No further attempts";
  }

  const dueAt = new Date(nextAttemptDueAtUtc).getTime();
  const diffMinutes = Math.round((dueAt - Date.now()) / 60000);
  if (diffMinutes <= 0) {
    return "Due now";
  }
  if (diffMinutes < 60) {
    return `Due in ${diffMinutes}m`;
  }
  const hours = Math.round(diffMinutes / 60);
  return `Due in ${hours}h`;
}

/**
 * The admin auto-charge queue (Payment Management UX pass gap: previously
 * zero visibility into `RecurringOccurrenceAutoChargeJob`'s attempts or
 * backoff schedule anywhere in the admin panel). Every recurring occurrence
 * still awaiting its off-session charge, oldest-created first.
 *
 * "Retry now" bypasses the backoff-timing gate, the plan's own auto-charge
 * toggle, and a prior cancellation - an explicit, one-off admin action each
 * time (same "admin override, no inherited eligibility gates" precedent as
 * admin reschedule). "Stop retrying" only stops the *automatic* sweep from
 * picking this occurrence up again; it does not block a later "Retry now."
 */
export default function AutoChargeQueuePage() {
  const claims = useAdminClaims();
  const canWrite = claims?.permissions.includes("bookings.write") ?? false;
  const queryClient = useQueryClient();

  const queueQuery = useQuery({
    queryKey: ["admin-auto-charge-queue"],
    queryFn: () => listAutoChargeCandidates(),
  });

  const [actionError, setActionError] = useState<string | null>(null);
  const [actionNotice, setActionNotice] = useState<string | null>(null);
  const [pendingCancel, setPendingCancel] = useState<AdminAutoChargeCandidate | null>(null);

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["admin-auto-charge-queue"] });

  const retryMutation = useMutation({
    mutationFn: (bookingId: string) => forceAutoChargeRetry(bookingId),
    onSuccess: () => {
      setActionError(null);
      setActionNotice("Charge attempt made - see the booking's Payment tab for the outcome.");
      invalidate();
    },
    onError: (err) => setActionError(describeError(err)),
  });

  const cancelMutation = useMutation({
    mutationFn: (bookingId: string) => cancelAutoChargeRetries(bookingId),
    onSuccess: () => {
      setPendingCancel(null);
      setActionError(null);
      setActionNotice("Retries stopped - the customer has been notified to pay manually.");
      invalidate();
    },
    onError: (err) => setActionError(describeError(err)),
  });

  return (
    <div className="flex w-full max-w-6xl flex-col gap-6">
      <PageHeading
        title="Auto-charge retries"
        subtitle="Every recurring booking still waiting on its off-session charge, oldest first."
        breadcrumbs={<Breadcrumbs items={[{ label: "Payments", href: "/payments" }, { label: "Auto-charge retries" }]} />}
      />
      <PaymentsTabs />

      {actionError ? <Alert tone="error">{actionError}</Alert> : null}
      {actionNotice ? <Alert tone="success">{actionNotice}</Alert> : null}

      <Card title="Pending auto-charge occurrences" description="Force an attempt now, or stop the automatic sweep from trying this occurrence again.">
        {queueQuery.isPending ? (
          <SkeletonText lines={4} />
        ) : queueQuery.isError ? (
          <SectionError error={queueQuery.error} onRetry={() => queueQuery.refetch()} />
        ) : queueQuery.data.length === 0 ? (
          <EmptyState title="Nothing pending" description="Every recurring occurrence is either paid, cancelled, or not yet due for its first attempt." />
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
                  <span className="nums font-medium text-fg">{formatCurrency(item.amountDue)}</span>
                </div>
                <p className="mt-1 text-xs text-fg-subtle">{item.customerName}</p>

                <div className="mt-3 flex flex-wrap items-center gap-2 border-t border-line pt-3">
                  <Badge tone={item.cancelledByAdmin ? "danger" : item.nextAttemptDueAtUtc ? "neutral" : "warning"}>
                    {item.cancelledByAdmin ? "Retries cancelled" : dueLabel(item.nextAttemptDueAtUtc)}
                  </Badge>
                  {!item.planAutoChargeEnabled ? <Badge tone="warning">Plan auto-charge off</Badge> : null}
                  <span className="nums text-xs text-fg-subtle">
                    {item.attemptCount} / {item.retryLimit} attempts
                  </span>
                  {item.lastAttemptAtUtc ? (
                    <span className="nums text-xs text-fg-subtle">Last: {formatDateTime(item.lastAttemptAtUtc)}</span>
                  ) : null}
                </div>

                {canWrite ? (
                  <div className="mt-3 flex flex-wrap gap-2 border-t border-line pt-3">
                    <Button
                      size="sm"
                      variant="secondary"
                      loading={retryMutation.isPending && retryMutation.variables === item.bookingId}
                      onClick={() => retryMutation.mutate(item.bookingId)}
                    >
                      Retry now
                    </Button>
                    {!item.cancelledByAdmin ? (
                      <Button size="sm" variant="danger" onClick={() => setPendingCancel(item)}>
                        Stop retrying
                      </Button>
                    ) : null}
                  </div>
                ) : null}
              </li>
            ))}
          </ul>
        )}
      </Card>

      <ConfirmDialog
        open={pendingCancel !== null}
        title="Stop retrying this booking?"
        description="The automatic sweep will never attempt this occurrence again. The customer is notified to pay manually - this can still be reversed with a manual Retry now, if needed."
        confirmLabel="Stop retrying"
        cancelLabel="Keep retrying"
        loading={cancelMutation.isPending}
        error={cancelMutation.isError ? describeError(cancelMutation.error) : null}
        onCancel={() => setPendingCancel(null)}
        onConfirm={() => {
          if (pendingCancel) {
            cancelMutation.mutate(pendingCancel.bookingId);
          }
        }}
      >
        {pendingCancel ? (
          <p className="text-sm text-fg-muted">
            {pendingCancel.bookingReference} — {pendingCancel.customerName}
          </p>
        ) : null}
      </ConfirmDialog>
    </div>
  );
}
