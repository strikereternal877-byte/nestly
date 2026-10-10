"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useMemo, useState } from "react";
import type { FormEvent } from "react";
import { Alert, Badge, Button, Card, Field, PageHeading, Select, Textarea } from "@/components/ui";
import {
  Breadcrumbs,
  ConfirmDialog,
  DescriptionList,
  FormActions,
  formatCurrency,
  formatDateTime,
} from "@/components/data-table";
import { DetailError, DetailSkeleton } from "@/components/screen-states";
import { BookingStatusBadge, TicketPriorityBadge, TicketStatusBadge } from "@/components/status-badges";
import { describeError } from "@/lib/api";
import { canWriteModule } from "@/lib/permissions";
import {
  assignSupportTicket,
  closeSupportTicket,
  escalateSupportTicket,
  getSupportTicket,
  linkSupportTicketBooking,
  listAssignableAdmins,
  resolveSupportTicket,
  respondToSupportTicket,
  unassignSupportTicket,
} from "@/lib/support-api";
import { categoryLabel, priorityLabel, statusLabel } from "@/lib/support";
import { SupportTicketCommentAuthorType } from "@/lib/support-types";
import type { AdminSupportTicketDetailResponse } from "@/lib/support-types";
import { BookingStatus, SupportTicketStatus } from "@/lib/types";
import { useAdminClaims } from "@/lib/use-admin-claims";

const BOOKING_STATUS_LABELS: Record<BookingStatus, string> = {
  [BookingStatus.Initiated]: "Booking Started",
  [BookingStatus.PaymentPending]: "Awaiting Payment",
  [BookingStatus.PaymentFailed]: "Payment Failed",
  [BookingStatus.Confirmed]: "Confirmed",
  [BookingStatus.AwaitingFulfilment]: "Preparing Service",
  [BookingStatus.Assigned]: "Professional Assigned",
  [BookingStatus.ProviderEnRoute]: "Professional On the Way",
  [BookingStatus.ProviderArrived]: "Professional Arrived",
  [BookingStatus.InProgress]: "In Progress",
  [BookingStatus.Completed]: "Completed",
  [BookingStatus.CancelledByCustomer]: "Cancelled by Customer",
  [BookingStatus.CancelledByAdmin]: "Cancelled by Admin",
  [BookingStatus.Rescheduled]: "Rescheduled",
  [BookingStatus.RefundPending]: "Refund in Progress",
  [BookingStatus.Refunded]: "Refunded",
  [BookingStatus.Expired]: "Expired",
};

const COMMENT_AUTHOR_LABELS: Record<SupportTicketCommentAuthorType, string> = {
  [SupportTicketCommentAuthorType.Customer]: "Customer",
  [SupportTicketCommentAuthorType.Support]: "Support",
  [SupportTicketCommentAuthorType.System]: "System",
};

const COMMENT_AUTHOR_TONES: Record<SupportTicketCommentAuthorType, "brand" | "info" | "neutral"> = {
  [SupportTicketCommentAuthorType.Customer]: "info",
  [SupportTicketCommentAuthorType.Support]: "brand",
  [SupportTicketCommentAuthorType.System]: "neutral",
};

/**
 * Same three lifecycle facts `SupportTicketLifecycle` (backend, SRS 31.2)
 * enforces server-side, mirrored here only to hide obviously-invalid action
 * buttons - the API is the real gate (every mutation still 422s on an
 * invalid transition regardless of what this hides).
 */
function canEscalate(status: SupportTicketStatus): boolean {
  return (
    status === SupportTicketStatus.Open ||
    status === SupportTicketStatus.InProgress ||
    status === SupportTicketStatus.WaitingForCustomer
  );
}

function canResolve(status: SupportTicketStatus): boolean {
  return (
    status === SupportTicketStatus.InProgress ||
    status === SupportTicketStatus.WaitingForCustomer ||
    status === SupportTicketStatus.Escalated
  );
}

function canClose(status: SupportTicketStatus): boolean {
  return status === SupportTicketStatus.Open || status === SupportTicketStatus.Resolved;
}

const BREADCRUMBS = [{ label: "Support", href: "/support" }, { label: "Ticket" }] as const;

/**
 * Admin ticket workflow detail screen (SRS 12.14.2, 16.3, tasks 120a-121):
 * comment thread, assignee, and linked booking summary, plus the full
 * assign/unassign, respond, escalate, resolve/close and link-booking action
 * set `SupportTicketsController` exposes. Mutating actions are only shown to
 * admins holding "support.write" - the API enforces this server-side
 * regardless, this is purely to avoid showing controls that would just 403.
 */
export default function SupportTicketDetailPage() {
  const params = useParams<{ id: string }>();
  const ticketId = params.id;
  const claims = useAdminClaims();
  const canWrite = canWriteModule(claims, "support");
  const queryClient = useQueryClient();

  const [assigneeId, setAssigneeId] = useState("");
  const [responseText, setResponseText] = useState("");
  const [resolutionSummary, setResolutionSummary] = useState("");
  const [bookingIdDraft, setBookingIdDraft] = useState("");
  const [pendingAction, setPendingAction] = useState<"escalate" | "close" | null>(null);

  const detailQuery = useQuery({
    queryKey: ["admin-support-ticket-detail", ticketId],
    queryFn: () => getSupportTicket(ticketId),
  });

  const assignableAdminsQuery = useQuery({
    queryKey: ["admin-support-ticket-assignable-admins"],
    queryFn: () => listAssignableAdmins(),
    enabled: canWrite,
  });

  // Each mutation returns the ticket's full updated detail response, so its
  // onSuccess writes that straight into the query cache rather than
  // invalidating and re-fetching.
  function useTicketMutation<TArgs>(
    mutationFn: (args: TArgs) => Promise<AdminSupportTicketDetailResponse>,
    onSettledSuccess?: () => void,
  ) {
    return useMutation({
      mutationFn,
      onSuccess: (data) => {
        queryClient.setQueryData(["admin-support-ticket-detail", ticketId], data);
        onSettledSuccess?.();
      },
    });
  }

  const assignMutation = useTicketMutation((adminUserId: string) => assignSupportTicket(ticketId, { adminUserId }));
  const unassignMutation = useTicketMutation<void>(() => unassignSupportTicket(ticketId));
  // The drafts below are cleared in onSuccess, not at submit time. Clearing
  // eagerly meant a failed response or resolution silently discarded what the
  // admin had just typed, and the only visible sign was an error banner.
  const respondMutation = useTicketMutation(
    (comment: string) => respondToSupportTicket(ticketId, { comment }),
    () => setResponseText(""),
  );
  const escalateMutation = useTicketMutation<void>(
    () => escalateSupportTicket(ticketId),
    () => setPendingAction(null),
  );
  const resolveMutation = useTicketMutation(
    (summary: string) => resolveSupportTicket(ticketId, { resolutionSummary: summary }),
    () => setResolutionSummary(""),
  );
  const closeMutation = useTicketMutation<void>(
    () => closeSupportTicket(ticketId),
    () => setPendingAction(null),
  );
  const linkBookingMutation = useTicketMutation(
    (bookingId: string) => linkSupportTicketBooking(ticketId, { bookingId }),
    () => setBookingIdDraft(""),
  );

  const ticket = detailQuery.data;

  /**
   * The API does not promise an ordering for the thread, and a comment list
   * read out of order is actively misleading. Sorted oldest-first here so the
   * conversation always reads top-to-bottom.
   */
  const comments = useMemo(() => {
    if (!ticket) return [];
    return [...ticket.comments].sort(
      (a, b) => new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime(),
    );
  }, [ticket]);

  const onAssign = (event: FormEvent) => {
    event.preventDefault();
    if (!assigneeId) return;
    assignMutation.mutate(assigneeId);
  };

  const onRespond = (event: FormEvent) => {
    event.preventDefault();
    const comment = responseText.trim();
    if (!comment) return;
    respondMutation.mutate(comment);
  };

  const onResolve = (event: FormEvent) => {
    event.preventDefault();
    const summary = resolutionSummary.trim();
    if (!summary) return;
    resolveMutation.mutate(summary);
  };

  const onLinkBooking = (event: FormEvent) => {
    event.preventDefault();
    const bookingId = bookingIdDraft.trim();
    if (!bookingId) return;
    linkBookingMutation.mutate(bookingId);
  };

  if (detailQuery.isPending) {
    return <DetailSkeleton cards={4} className="flex w-full max-w-7xl flex-col gap-6" />;
  }

  if (detailQuery.isError || !ticket) {
    return (
      <DetailError
        title="Support ticket"
        breadcrumbs={BREADCRUMBS}
        error={detailQuery.error}
        onRetry={() => detailQuery.refetch()}
        className="w-full max-w-7xl"
      />
    );
  }

  const isClosed = ticket.status === SupportTicketStatus.Closed;

  return (
    <div className="flex w-full max-w-7xl flex-col gap-6">
      <PageHeading
        title={ticket.subject}
        subtitle={`${ticket.customerName} · ${categoryLabel(ticket.category)}`}
        breadcrumbs={<Breadcrumbs items={BREADCRUMBS} />}
        actions={
          <>
            <Link href={`/customers/${ticket.customerId}`}>
              <Button variant="secondary" size="sm">
                View customer
              </Button>
            </Link>
            <TicketPriorityBadge priority={ticket.priority} label={priorityLabel(ticket.priority)} />
            <TicketStatusBadge status={ticket.status} label={statusLabel(ticket.status)} />
            {ticket.isDisputed ? <Badge tone="warning">Disputed</Badge> : null}
          </>
        }
      />

      <Card title="Ticket details">
        <DescriptionList
          columns={3}
          items={[
            {
              label: "Status",
              value: <TicketStatusBadge status={ticket.status} label={statusLabel(ticket.status)} />,
            },
            { label: "Disputed", value: ticket.isDisputed ? "Yes" : "No" },
            {
              label: "Assigned to",
              value: ticket.assignedAdminName ?? <span className="text-fg-subtle">Unassigned</span>,
            },
            {
              label: "Escalated at",
              value: <span className="nums">{formatDateTime(ticket.escalatedAtUtc)}</span>,
            },
            { label: "Created", value: <span className="nums">{formatDateTime(ticket.createdAtUtc)}</span> },
            {
              label: "Last updated",
              value: <span className="nums">{formatDateTime(ticket.updatedAtUtc)}</span>,
            },
          ]}
        />

        <p className="mt-5 whitespace-pre-wrap text-sm leading-relaxed text-fg">{ticket.description}</p>

        {ticket.resolutionSummary ? (
          <div className="mt-4 rounded-xl border border-success/25 bg-success-soft px-4 py-3 text-sm text-success">
            <p className="font-semibold">Resolution</p>
            <p className="mt-0.5 leading-relaxed">{ticket.resolutionSummary}</p>
          </div>
        ) : null}
      </Card>

      <Card title="Linked booking" description='SRS 12.14.2 "Link ... booking action", task 120e'>
        {ticket.booking ? (
          <DescriptionList
            columns={3}
            items={[
              { label: "Booking date", value: <span className="nums">{ticket.booking.slotDate}</span> },
              {
                label: "Slot",
                value: (
                  <span className="nums">
                    {ticket.booking.slotStartTimeSnapshot}–{ticket.booking.slotEndTimeSnapshot}
                  </span>
                ),
              },
              {
                label: "Status",
                value: (
                  <BookingStatusBadge
                    status={ticket.booking.status}
                    label={BOOKING_STATUS_LABELS[ticket.booking.status] ?? "Unknown"}
                  />
                ),
              },
              {
                label: "Total payable",
                value: <span className="nums">{formatCurrency(ticket.booking.totalPayableSnapshot)}</span>,
              },
              {
                label: "Booking",
                value: (
                  <Link
                    href={`/bookings/${ticket.booking.id}`}
                    className="rounded text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
                  >
                    Open booking
                  </Link>
                ),
              },
            ]}
          />
        ) : (
          <p className="text-sm text-fg-muted">No booking linked to this ticket.</p>
        )}

        {canWrite ? (
          <form onSubmit={onLinkBooking} className="mt-5 flex flex-col gap-3 border-t border-line pt-5">
            {linkBookingMutation.isError ? (
              <Alert>{describeError(linkBookingMutation.error)}</Alert>
            ) : null}
            <div className="flex flex-col gap-3 sm:flex-row sm:items-end">
              <div className="flex-1">
                <Field
                  label={ticket.booking ? "Re-link to a different booking" : "Booking ID"}
                  placeholder="Booking GUID"
                  value={bookingIdDraft}
                  onChange={(e) => setBookingIdDraft(e.target.value)}
                />
              </div>
              <Button
                type="submit"
                variant="secondary"
                disabled={!bookingIdDraft.trim()}
                loading={linkBookingMutation.isPending}
              >
                Link booking
              </Button>
            </div>
          </form>
        ) : null}
      </Card>

      {canWrite ? (
        <Card title="Assignment" description='SRS 12.14.2 "Assign to team/user", task 120a'>
          {ticket.assignedAdminName ? (
            <div className="flex flex-col gap-3">
              {unassignMutation.isError ? <Alert>{describeError(unassignMutation.error)}</Alert> : null}
              <div className="flex flex-wrap items-center justify-between gap-3 text-sm">
                <span className="text-fg-muted">
                  Assigned to <span className="font-medium text-fg">{ticket.assignedAdminName}</span>
                </span>
                <Button
                  variant="secondary"
                  disabled={isClosed}
                  loading={unassignMutation.isPending}
                  onClick={() => unassignMutation.mutate()}
                >
                  Unassign
                </Button>
              </div>
            </div>
          ) : (
            <form onSubmit={onAssign} className="flex flex-col gap-3">
              {assignMutation.isError ? <Alert>{describeError(assignMutation.error)}</Alert> : null}
              {assignableAdminsQuery.isError ? (
                <Alert
                  tone="warning"
                  action={
                    <Button size="sm" variant="secondary" onClick={() => assignableAdminsQuery.refetch()}>
                      Retry
                    </Button>
                  }
                >
                  The list of assignable admins could not be loaded.{" "}
                  {describeError(assignableAdminsQuery.error)}
                </Alert>
              ) : null}
              <div className="flex max-w-2xl flex-col gap-3 sm:flex-row sm:items-end">
                <div className="flex-1">
                  <Select
                    label="Assign to"
                    placeholder={assignableAdminsQuery.isPending ? "Loading admins…" : "Select an admin…"}
                    value={assigneeId}
                    onChange={(e) => setAssigneeId(e.target.value)}
                    options={(assignableAdminsQuery.data ?? []).map((admin) => ({
                      value: admin.id,
                      label: `${admin.fullName} (${admin.email})`,
                    }))}
                  />
                </div>
                <Button type="submit" disabled={isClosed || !assigneeId} loading={assignMutation.isPending}>
                  Assign
                </Button>
              </div>
            </form>
          )}
        </Card>
      ) : null}

      {canWrite ? (
        <Card
          title="Workflow actions"
          description='SRS 12.14.2 "Mark escalated" / "Mark resolved/closed", tasks 120c-120d'
        >
          <div className="flex flex-wrap gap-2">
            <Button
              variant="secondary"
              disabled={!canEscalate(ticket.status)}
              onClick={() => setPendingAction("escalate")}
            >
              Escalate
            </Button>
            <Button
              variant="secondary"
              disabled={!canClose(ticket.status)}
              onClick={() => setPendingAction("close")}
            >
              Close
            </Button>
          </div>

          {canResolve(ticket.status) ? (
            <form onSubmit={onResolve} className="mt-5 flex max-w-2xl flex-col gap-3 border-t border-line pt-5">
              {resolveMutation.isError ? <Alert>{describeError(resolveMutation.error)}</Alert> : null}
              <Textarea
                label="Resolution summary"
                required
                value={resolutionSummary}
                onChange={(e) => setResolutionSummary(e.target.value)}
                placeholder="Describe how this ticket was resolved"
                hint="Shown to the customer and kept on the ticket."
              />
              <FormActions align="start">
                <Button type="submit" disabled={!resolutionSummary.trim()} loading={resolveMutation.isPending}>
                  Mark resolved
                </Button>
              </FormActions>
            </form>
          ) : null}
        </Card>
      ) : null}

      <Card title="Comment thread" description='SRS 12.14.2 "Add response/note", task 120b'>
        {comments.length === 0 ? (
          <p className="text-sm text-fg-muted">No comments yet.</p>
        ) : (
          <ul className="flex flex-col gap-3">
            {comments.map((comment) => (
              <li key={comment.id} className="rounded-xl border border-line bg-surface-2 p-4">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <Badge tone={COMMENT_AUTHOR_TONES[comment.authorType] ?? "neutral"}>
                    {COMMENT_AUTHOR_LABELS[comment.authorType] ?? "Unknown"}
                  </Badge>
                  <span className="nums text-xs text-fg-subtle">{formatDateTime(comment.createdAt)}</span>
                </div>
                <p className="mt-2 whitespace-pre-wrap text-sm leading-relaxed text-fg">{comment.comment}</p>
              </li>
            ))}
          </ul>
        )}

        {canWrite && !isClosed ? (
          <form onSubmit={onRespond} className="mt-5 flex max-w-2xl flex-col gap-3 border-t border-line pt-5">
            {respondMutation.isError ? <Alert>{describeError(respondMutation.error)}</Alert> : null}
            <Textarea
              label="Add a response"
              value={responseText}
              onChange={(e) => setResponseText(e.target.value)}
              placeholder="Reply to the customer"
            />
            <FormActions align="start">
              <Button type="submit" disabled={!responseText.trim()} loading={respondMutation.isPending}>
                Send response
              </Button>
            </FormActions>
          </form>
        ) : null}
      </Card>

      <ConfirmDialog
        open={pendingAction === "escalate"}
        title="Escalate this ticket?"
        description="It moves to the escalated queue and notifies the escalation owners. There is no un-escalate action."
        confirmLabel="Escalate"
        cancelLabel="Not now"
        tone="primary"
        loading={escalateMutation.isPending}
        error={escalateMutation.isError ? describeError(escalateMutation.error) : null}
        onCancel={() => setPendingAction(null)}
        onConfirm={() => escalateMutation.mutate()}
      />

      <ConfirmDialog
        open={pendingAction === "close"}
        title="Close this ticket?"
        description="A closed ticket accepts no further responses and cannot be reopened from this screen."
        confirmLabel="Close ticket"
        cancelLabel="Keep open"
        loading={closeMutation.isPending}
        error={closeMutation.isError ? describeError(closeMutation.error) : null}
        onCancel={() => setPendingAction(null)}
        onConfirm={() => closeMutation.mutate()}
      />
    </div>
  );
}
