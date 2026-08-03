"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useEffect, useState } from "react";
import { Alert, Button, Card, Field, PageHeading, Select, Textarea } from "@/components/ui";
import { describeError } from "@/lib/api";
import { getSessionClaims, subscribeToAuthChanges } from "@/lib/auth";
import {
  cancelBooking,
  getBookingCompletionProof,
  getBookingDetail,
  refundBooking,
  rescheduleBooking,
  updateBookingStatus,
} from "@/lib/bookings-api";
import {
  CancellationActor,
  RefundMethod,
  RefundStatus,
  RescheduleActor,
} from "@/lib/bookings-types";
import { assignProviderToBooking, getBookingAssignmentHistory, rejectBookingAssignment } from "@/lib/providers-api";
import { BookingProviderAssignmentStatus } from "@/lib/providers-types";
import type { AdminSessionClaims } from "@/lib/types";
import { BookingStatus } from "@/lib/types";

function useAdminClaims(): AdminSessionClaims | null {
  const [claims, setClaims] = useState<AdminSessionClaims | null>(null);
  useEffect(() => {
    const sync = () => setClaims(getSessionClaims());
    sync();
    return subscribeToAuthChanges(sync);
  }, []);
  return claims;
}

const BOOKING_STATUS_LABELS: Record<BookingStatus, string> = {
  [BookingStatus.Initiated]: "Booking Started",
  [BookingStatus.PaymentPending]: "Awaiting Payment",
  [BookingStatus.PaymentFailed]: "Payment Failed",
  [BookingStatus.Confirmed]: "Confirmed",
  [BookingStatus.AwaitingFulfilment]: "Preparing Service",
  [BookingStatus.Assigned]: "Professional Assigned",
  [BookingStatus.InProgress]: "In Progress",
  [BookingStatus.Completed]: "Completed",
  [BookingStatus.CancelledByCustomer]: "Cancelled by Customer",
  [BookingStatus.CancelledByAdmin]: "Cancelled by Admin",
  [BookingStatus.Rescheduled]: "Rescheduled",
  [BookingStatus.RefundPending]: "Refund in Progress",
  [BookingStatus.Refunded]: "Refunded",
};

// Statuses reachable through the generic status-update action. Cancel/
// reschedule/refund each go through their own dedicated action below - the
// API rejects these five here regardless (see AdminBookingStatusUpdateRequest's
// doc comment), so they are left out of this picker entirely.
const GENERIC_STATUS_OPTIONS = [
  BookingStatus.Initiated,
  BookingStatus.PaymentPending,
  BookingStatus.PaymentFailed,
  BookingStatus.Confirmed,
  BookingStatus.AwaitingFulfilment,
  BookingStatus.Assigned,
  BookingStatus.InProgress,
  BookingStatus.Completed,
].map((value) => ({ value: String(value), label: BOOKING_STATUS_LABELS[value] }));

const CANCELLATION_ACTOR_LABELS: Record<CancellationActor, string> = {
  [CancellationActor.Customer]: "Customer",
  [CancellationActor.Admin]: "Admin",
  [CancellationActor.System]: "System",
};

const RESCHEDULE_ACTOR_LABELS: Record<RescheduleActor, string> = {
  [RescheduleActor.Customer]: "Customer",
  [RescheduleActor.Admin]: "Admin",
  [RescheduleActor.System]: "System",
};

const REFUND_STATUS_LABELS: Record<RefundStatus, string> = {
  [RefundStatus.Initiated]: "Initiated",
  [RefundStatus.Processing]: "Processing",
  [RefundStatus.Refunded]: "Refunded",
  [RefundStatus.Failed]: "Failed",
};

const ASSIGNMENT_STATUS_LABELS: Record<BookingProviderAssignmentStatus, string> = {
  [BookingProviderAssignmentStatus.Assigned]: "Awaiting response",
  [BookingProviderAssignmentStatus.Accepted]: "Accepted",
  [BookingProviderAssignmentStatus.Rejected]: "Rejected",
  [BookingProviderAssignmentStatus.Reassigned]: "Superseded (reassigned)",
  [BookingProviderAssignmentStatus.Withdrawn]: "Withdrawn (booking cancelled)",
};

/**
 * Admin booking detail screen (SRS 12.11.2-3, task 116): snapshots, full
 * status timeline, payment/cancellation/reschedule/refund history, and the
 * authorized cancel/reschedule/refund/status actions (tasks 115d, 117a-c).
 * Mutating actions are only shown to admins holding "bookings.write" - the
 * API enforces this server-side regardless, this purely avoids showing
 * controls that would just 403.
 */
export default function BookingDetailPage() {
  const params = useParams<{ bookingId: string }>();
  const bookingId = params.bookingId;
  const claims = useAdminClaims();
  const canWrite = claims?.permissions.includes("bookings.write") ?? false;
  const queryClient = useQueryClient();

  const detailQuery = useQuery({
    queryKey: ["admin-booking-detail", bookingId],
    queryFn: () => getBookingDetail(bookingId),
  });

  const assignmentHistoryQuery = useQuery({
    queryKey: ["admin-booking-assignment-history", bookingId],
    queryFn: () => getBookingAssignmentHistory(bookingId),
  });

  const [actionError, setActionError] = useState<string | null>(null);
  const [actionNotice, setActionNotice] = useState<string | null>(null);

  const [newStatus, setNewStatus] = useState("");
  const [statusReason, setStatusReason] = useState("");

  const [cancelReason, setCancelReason] = useState("");
  const [cancelNotes, setCancelNotes] = useState("");

  const [rescheduleLocalityId, setRescheduleLocalityId] = useState("");
  const [rescheduleSlotWindowId, setRescheduleSlotWindowId] = useState("");
  const [rescheduleSlotDate, setRescheduleSlotDate] = useState("");
  const [rescheduleReason, setRescheduleReason] = useState("");

  const [refundIsFull, setRefundIsFull] = useState(true);
  const [refundAmount, setRefundAmount] = useState("");
  const [refundReason, setRefundReason] = useState("");
  const [refundMethod, setRefundMethod] = useState(String(RefundMethod.Gateway));

  const [assignProviderId, setAssignProviderId] = useState("");
  const [rejectReason, setRejectReason] = useState("");

  const invalidateDetail = () => {
    queryClient.invalidateQueries({ queryKey: ["admin-booking-detail", bookingId] });
    queryClient.invalidateQueries({ queryKey: ["admin-booking-assignment-history", bookingId] });
  };

  const statusMutation = useMutation({
    mutationFn: () => updateBookingStatus(bookingId, { newStatus: Number(newStatus) as BookingStatus, reason: statusReason || undefined }),
    onSuccess: () => {
      setActionError(null);
      setActionNotice("Status updated.");
      setStatusReason("");
      invalidateDetail();
    },
    onError: (err) => setActionError(describeError(err)),
  });

  const cancelMutation = useMutation({
    mutationFn: () => cancelBooking(bookingId, { reason: cancelReason, internalNotes: cancelNotes || undefined }),
    onSuccess: () => {
      setActionError(null);
      setActionNotice("Booking cancelled.");
      setCancelReason("");
      setCancelNotes("");
      invalidateDetail();
    },
    onError: (err) => setActionError(describeError(err)),
  });

  const rescheduleMutation = useMutation({
    mutationFn: () =>
      rescheduleBooking(bookingId, {
        localityId: rescheduleLocalityId,
        slotWindowId: rescheduleSlotWindowId,
        slotDate: rescheduleSlotDate,
        reason: rescheduleReason || undefined,
      }),
    onSuccess: () => {
      setActionError(null);
      setActionNotice("Booking rescheduled.");
      setRescheduleLocalityId("");
      setRescheduleSlotWindowId("");
      setRescheduleSlotDate("");
      setRescheduleReason("");
      invalidateDetail();
    },
    onError: (err) => setActionError(describeError(err)),
  });

  const refundMutation = useMutation({
    mutationFn: () =>
      refundBooking(bookingId, {
        isFullRefund: refundIsFull,
        amount: refundIsFull ? undefined : Number(refundAmount),
        reason: refundReason,
        method: Number(refundMethod) as RefundMethod,
      }),
    onSuccess: () => {
      setActionError(null);
      setActionNotice("Refund initiated.");
      setRefundAmount("");
      setRefundReason("");
      invalidateDetail();
    },
    onError: (err) => setActionError(describeError(err)),
  });

  const assignProviderMutation = useMutation({
    mutationFn: () => assignProviderToBooking(bookingId, { providerId: assignProviderId }),
    onSuccess: () => {
      setActionError(null);
      setActionNotice("Provider assigned.");
      setAssignProviderId("");
      invalidateDetail();
    },
    onError: (err) => setActionError(describeError(err)),
  });

  const rejectAssignmentMutation = useMutation({
    mutationFn: () => rejectBookingAssignment(bookingId, { reason: rejectReason || undefined }),
    onSuccess: () => {
      setActionError(null);
      setActionNotice("Assignment rejected. Booking needs reassignment.");
      setRejectReason("");
      invalidateDetail();
    },
    onError: (err) => setActionError(describeError(err)),
  });

  if (detailQuery.isPending) {
    return <p className="text-sm text-neutral-500">Loading booking…</p>;
  }

  if (detailQuery.isError) {
    return <Alert>{describeError(detailQuery.error)}</Alert>;
  }

  const booking = detailQuery.data;

  return (
    <div className="mx-auto flex w-full max-w-4xl flex-col gap-6">
      <div className="flex items-center justify-between">
        <PageHeading title={booking.customer.name} subtitle={`Booking ${booking.id}`} />
        <Link href="/bookings" className="text-sm underline-offset-2 hover:underline">
          Back to bookings
        </Link>
      </div>

      {actionError ? <Alert>{actionError}</Alert> : null}
      {actionNotice ? <Alert tone="success">{actionNotice}</Alert> : null}

      <Card title="Booking summary" description={`Status: ${booking.statusLabel}`}>
        <dl className="grid grid-cols-2 gap-x-4 gap-y-2 text-sm sm:grid-cols-3">
          <div>
            <dt className="text-neutral-500">Customer mobile</dt>
            <dd>{booking.customer.mobile}</dd>
          </div>
          <div>
            <dt className="text-neutral-500">Slot</dt>
            <dd>
              {booking.slot.date} · {booking.slot.startTime}–{booking.slot.endTime}
            </dd>
          </div>
          <div>
            <dt className="text-neutral-500">City</dt>
            <dd>{booking.address.city}</dd>
          </div>
          <div>
            <dt className="text-neutral-500">Total payable</dt>
            <dd>₹{booking.price.finalPayable.toFixed(2)}</dd>
          </div>
          <div>
            <dt className="text-neutral-500">Coupon</dt>
            <dd>{booking.price.couponCode ?? "—"}</dd>
          </div>
          <div>
            <dt className="text-neutral-500">Created</dt>
            <dd>{new Date(booking.createdAtUtc).toLocaleString()}</dd>
          </div>
        </dl>
      </Card>

      <Card title="Address" description="Snapshot at booking time">
        <p className="text-sm">
          {[booking.address.label, booking.address.line1, booking.address.line2, booking.address.landmark, booking.address.city, booking.address.state, booking.address.pincode]
            .filter(Boolean)
            .join(", ")}
        </p>
        <p className="mt-1 text-sm text-neutral-500">
          Contact: {booking.address.contactName} · {booking.address.contactMobile}
        </p>
      </Card>

      <Card title="Services booked">
        <ul className="flex flex-col gap-2 text-sm">
          {booking.items.map((item) => (
            <li key={item.id} className="rounded-lg border border-black/10 p-3 dark:border-white/15">
              <div className="flex items-center justify-between">
                <span className="font-medium">{item.name}</span>
                <span>₹{item.lineTotal.toFixed(2)}</span>
              </div>
              {item.addOns.length > 0 ? (
                <ul className="mt-2 flex flex-col gap-1 pl-4 text-xs text-neutral-600 dark:text-neutral-400">
                  {item.addOns.map((addOn) => (
                    <li key={addOn.id} className="flex items-center justify-between">
                      <span>{addOn.name}</span>
                      <span>₹{addOn.lineTotal.toFixed(2)}</span>
                    </li>
                  ))}
                </ul>
              ) : null}
            </li>
          ))}
        </ul>
      </Card>

      <Card title="Payment">
        {booking.payment === null ? (
          <p className="text-sm text-neutral-500">No payment transaction yet.</p>
        ) : (
          <dl className="grid grid-cols-2 gap-x-4 gap-y-2 text-sm sm:grid-cols-3">
            <div>
              <dt className="text-neutral-500">Amount</dt>
              <dd>
                {booking.payment.currency} {booking.payment.amount.toFixed(2)}
              </dd>
            </div>
            <div>
              <dt className="text-neutral-500">Gateway ref</dt>
              <dd>{booking.payment.gatewayPaymentRef ?? "—"}</dd>
            </div>
            <div>
              <dt className="text-neutral-500">Updated</dt>
              <dd>{new Date(booking.payment.updatedAtUtc).toLocaleString()}</dd>
            </div>
          </dl>
        )}
      </Card>

      {booking.status === BookingStatus.Completed ? <CompletionProofCard bookingId={booking.id} /> : null}

      <Card title="Status timeline" description="Full history (SRS 12.11.2-3)">
        <ol className="flex flex-col gap-2 text-sm">
          {booking.timeline.map((entry, index) => (
            <li key={index} className="rounded-lg border border-black/10 p-3 dark:border-white/15">
              <div className="flex items-center justify-between">
                <span className="font-medium">{entry.toStatusLabel}</span>
                <span className="text-xs text-neutral-500">{new Date(entry.changedAtUtc).toLocaleString()}</span>
              </div>
              {entry.reason ? <p className="mt-1 text-xs text-neutral-600 dark:text-neutral-400">{entry.reason}</p> : null}
            </li>
          ))}
        </ol>
      </Card>

      <Card
        title="Provider assignment"
        description="Manual admin-driven assignment (PROVIDER.md OPEN DECISIONS #1, tasks 147, 159)"
      >
        {assignmentHistoryQuery.isPending ? (
          <p className="text-sm text-neutral-500">Loading assignment history…</p>
        ) : assignmentHistoryQuery.isError ? (
          <Alert>{describeError(assignmentHistoryQuery.error)}</Alert>
        ) : assignmentHistoryQuery.data.length === 0 ? (
          <p className="text-sm text-neutral-500">No provider has been assigned to this booking yet.</p>
        ) : (
          <ul className="flex flex-col gap-2 text-sm">
            {assignmentHistoryQuery.data.map((assignment) => (
              <li key={assignment.id} className="rounded-lg border border-black/10 p-3 dark:border-white/15">
                <div className="flex items-center justify-between">
                  <span className="font-medium">{assignment.providerDisplayName}</span>
                  <span>{ASSIGNMENT_STATUS_LABELS[assignment.status]}</span>
                </div>
                <p className="mt-1 text-xs text-neutral-500">
                  Assigned {new Date(assignment.assignedAt).toLocaleString()}
                  {assignment.respondedAt ? ` · Responded ${new Date(assignment.respondedAt).toLocaleString()}` : ""}
                </p>
                {assignment.notes ? <p className="mt-1 text-xs text-neutral-600 dark:text-neutral-400">{assignment.notes}</p> : null}
              </li>
            ))}
          </ul>
        )}

        {canWrite ? (
          <div className="mt-5 flex flex-col gap-3 border-t border-black/10 pt-5 dark:border-white/15 sm:flex-row sm:items-end">
            <div className="flex-1">
              <Field
                label="Provider ID to assign"
                value={assignProviderId}
                onChange={(e) => setAssignProviderId(e.target.value)}
                placeholder="Provider GUID"
              />
            </div>
            <Button disabled={!assignProviderId.trim() || assignProviderMutation.isPending} onClick={() => assignProviderMutation.mutate()}>
              {assignProviderMutation.isPending ? "Assigning…" : "Assign provider"}
            </Button>
          </div>
        ) : null}

        {canWrite ? (
          <div className="mt-3 flex flex-col gap-3 sm:flex-row sm:items-end">
            <div className="flex-1">
              <Field
                label="Rejection reason (optional)"
                value={rejectReason}
                onChange={(e) => setRejectReason(e.target.value)}
                placeholder="Reason the current assignment is being rejected"
              />
            </div>
            <Button variant="danger" disabled={rejectAssignmentMutation.isPending} onClick={() => rejectAssignmentMutation.mutate()}>
              {rejectAssignmentMutation.isPending ? "Rejecting…" : "Reject current assignment"}
            </Button>
          </div>
        ) : null}
      </Card>

      {booking.cancellation ? (
        <Card title="Cancellation">
          <dl className="grid grid-cols-2 gap-x-4 gap-y-2 text-sm sm:grid-cols-3">
            <div>
              <dt className="text-neutral-500">Actor</dt>
              <dd>{CANCELLATION_ACTOR_LABELS[booking.cancellation.actor]}</dd>
            </div>
            <div>
              <dt className="text-neutral-500">Fee</dt>
              <dd>₹{booking.cancellation.cancellationFeeAmount.toFixed(2)}</dd>
            </div>
            <div>
              <dt className="text-neutral-500">Refund amount</dt>
              <dd>₹{booking.cancellation.refundAmount.toFixed(2)}</dd>
            </div>
          </dl>
          <p className="mt-3 text-sm">{booking.cancellation.reason}</p>
          {booking.cancellation.internalNotes ? (
            <p className="mt-1 text-xs text-neutral-500">Internal notes: {booking.cancellation.internalNotes}</p>
          ) : null}
        </Card>
      ) : null}

      {booking.reschedules.length > 0 ? (
        <Card title="Reschedule history">
          <ul className="flex flex-col gap-2 text-sm">
            {booking.reschedules.map((reschedule) => (
              <li key={reschedule.id} className="rounded-lg border border-black/10 p-3 dark:border-white/15">
                <div className="flex items-center justify-between">
                  <span>
                    {reschedule.fromSlotDate} {reschedule.fromSlotStartTime} → {reschedule.toSlotDate} {reschedule.toSlotStartTime}
                  </span>
                  <span className="text-xs text-neutral-500">{RESCHEDULE_ACTOR_LABELS[reschedule.actor]}</span>
                </div>
                {reschedule.feeAmount > 0 ? <p className="mt-1 text-xs text-neutral-500">Fee: ₹{reschedule.feeAmount.toFixed(2)}</p> : null}
              </li>
            ))}
          </ul>
        </Card>
      ) : null}

      {booking.refunds.length > 0 ? (
        <Card title="Refund history">
          <ul className="flex flex-col gap-2 text-sm">
            {booking.refunds.map((refund) => (
              <li key={refund.id} className="flex items-center justify-between rounded-lg border border-black/10 p-3 dark:border-white/15">
                <span>{refund.reason}</span>
                <span>{REFUND_STATUS_LABELS[refund.status]}</span>
                <span className="font-medium">₹{refund.amount.toFixed(2)}</span>
              </li>
            ))}
          </ul>
        </Card>
      ) : null}

      {canWrite ? (
        <>
          <Card title="Update status" description="General operational status transitions (task 115d)">
            <div className="flex flex-col gap-3 sm:flex-row sm:items-end">
              <div className="flex-1">
                <Select
                  label="New status"
                  options={[{ value: "", label: "Select a status…" }, ...GENERIC_STATUS_OPTIONS]}
                  value={newStatus}
                  onChange={(e) => setNewStatus(e.target.value)}
                />
              </div>
              <div className="flex-1">
                <Field label="Reason (optional)" value={statusReason} onChange={(e) => setStatusReason(e.target.value)} />
              </div>
              <Button disabled={!newStatus || statusMutation.isPending} onClick={() => statusMutation.mutate()}>
                {statusMutation.isPending ? "Updating…" : "Update status"}
              </Button>
            </div>
          </Card>

          <Card title="Cancel booking" description="Admin-initiated cancellation (SRS 12.11.3, task 117a)">
            <div className="flex flex-col gap-3">
              <Field label="Reason" value={cancelReason} onChange={(e) => setCancelReason(e.target.value)} />
              <Textarea label="Internal notes (optional)" value={cancelNotes} onChange={(e) => setCancelNotes(e.target.value)} />
              <div>
                <Button variant="danger" disabled={!cancelReason.trim() || cancelMutation.isPending} onClick={() => cancelMutation.mutate()}>
                  {cancelMutation.isPending ? "Cancelling…" : "Cancel booking"}
                </Button>
              </div>
            </div>
          </Card>

          <Card title="Reschedule booking" description="Admin-initiated reschedule (SRS 12.11.3, task 117b)">
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <Field label="Locality ID" value={rescheduleLocalityId} onChange={(e) => setRescheduleLocalityId(e.target.value)} />
              <Field label="Slot window ID" value={rescheduleSlotWindowId} onChange={(e) => setRescheduleSlotWindowId(e.target.value)} />
              <Field label="New slot date" type="date" value={rescheduleSlotDate} onChange={(e) => setRescheduleSlotDate(e.target.value)} />
              <Field label="Reason (optional)" value={rescheduleReason} onChange={(e) => setRescheduleReason(e.target.value)} />
              <div>
                <Button
                  disabled={!rescheduleLocalityId || !rescheduleSlotWindowId || !rescheduleSlotDate || rescheduleMutation.isPending}
                  onClick={() => rescheduleMutation.mutate()}
                >
                  {rescheduleMutation.isPending ? "Rescheduling…" : "Reschedule"}
                </Button>
              </div>
            </div>
          </Card>

          <Card title="Refund" description="Full or partial refund with audit (SRS 12.11.3, 12.13.2-3, task 117c)">
            <div className="flex flex-col gap-3">
              <div className="flex gap-4 text-sm">
                <label className="flex items-center gap-2">
                  <input type="radio" checked={refundIsFull} onChange={() => setRefundIsFull(true)} />
                  Full refund
                </label>
                <label className="flex items-center gap-2">
                  <input type="radio" checked={!refundIsFull} onChange={() => setRefundIsFull(false)} />
                  Partial refund
                </label>
              </div>
              {!refundIsFull ? (
                <Field
                  label="Amount"
                  type="number"
                  min="0"
                  step="0.01"
                  value={refundAmount}
                  onChange={(e) => setRefundAmount(e.target.value)}
                />
              ) : null}
              <Select
                label="Method"
                options={[
                  { value: String(RefundMethod.Gateway), label: "Gateway" },
                  { value: String(RefundMethod.Wallet), label: "Wallet credit" },
                ]}
                value={refundMethod}
                onChange={(e) => setRefundMethod(e.target.value)}
              />
              <Field label="Reason" value={refundReason} onChange={(e) => setRefundReason(e.target.value)} />
              <div>
                <Button
                  disabled={!refundReason.trim() || (!refundIsFull && !refundAmount) || refundMutation.isPending}
                  onClick={() => refundMutation.mutate()}
                >
                  {refundMutation.isPending ? "Processing…" : "Initiate refund"}
                </Button>
              </div>
            </div>
          </Card>
        </>
      ) : null}
    </div>
  );
}

/** Photo + checklist evidence the provider submitted at job completion - dispute-review evidence (tasks 195-198, SRS 12.11.2). */
function CompletionProofCard({ bookingId }: { bookingId: string }) {
  const query = useQuery({
    queryKey: ["admin-booking-completion-proof", bookingId],
    queryFn: () => getBookingCompletionProof(bookingId),
  });

  if (query.isPending) {
    return (
      <Card title="Completion proof">
        <p className="text-sm text-neutral-500">Loading…</p>
      </Card>
    );
  }

  if (query.isError) {
    return (
      <Card title="Completion proof">
        <Alert>{describeError(query.error)}</Alert>
      </Card>
    );
  }

  const proof = query.data;
  if (!proof) {
    return null;
  }

  return (
    <Card title="Completion proof" description="Submitted by the provider at job completion (SRS 12.11.2)">
      <p className="text-sm text-neutral-600 dark:text-neutral-400">
        Submitted {new Date(proof.submittedAtUtc).toLocaleString()} · {proof.photoRefs.length} photo(s)
      </p>
      {proof.photoRefs.length > 0 ? (
        <ul className="mt-2 flex flex-col gap-1 text-sm">
          {proof.photoRefs.map((ref, i) => (
            <li key={i}>
              <a href={ref} target="_blank" rel="noreferrer" className="hover:underline">
                {ref}
              </a>
            </li>
          ))}
        </ul>
      ) : null}
      {proof.checklistAnswers.length > 0 ? (
        <ul className="mt-3 flex flex-col gap-1 text-sm">
          {proof.checklistAnswers.map((answer, i) => (
            <li key={i}>
              {answer.completed ? "✓" : "○"} {answer.item}
              {answer.notes ? ` — ${answer.notes}` : ""}
            </li>
          ))}
        </ul>
      ) : null}
    </Card>
  );
}
