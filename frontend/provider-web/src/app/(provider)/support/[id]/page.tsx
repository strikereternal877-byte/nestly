"use client";

import { useParams } from "next/navigation";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ErrorState } from "@/components/states";
import { Badge, Button, Card, PageHeading, Skeleton, Textarea, cx } from "@/components/ui";
import type { BadgeTone } from "@/components/ui";
import { formatDateTime } from "@/lib/format";
import { addSupportTicketComment, getSupportTicketDetail } from "@/lib/support-api";
import {
  ProviderSupportTicketCategory,
  ProviderSupportTicketCommentAuthorType,
  ProviderSupportTicketStatus,
} from "@/lib/support-types";

const CATEGORY_LABELS: Record<ProviderSupportTicketCategory, string> = {
  [ProviderSupportTicketCategory.Payout]: "Payout",
  [ProviderSupportTicketCategory.Kyc]: "KYC / documents",
  [ProviderSupportTicketCategory.JobIssue]: "Job issue",
  [ProviderSupportTicketCategory.Account]: "Account",
  [ProviderSupportTicketCategory.Technical]: "App / technical",
  [ProviderSupportTicketCategory.Other]: "Other",
};

const STATUS_LABELS: Record<ProviderSupportTicketStatus, string> = {
  [ProviderSupportTicketStatus.Open]: "Open",
  [ProviderSupportTicketStatus.InProgress]: "In progress",
  [ProviderSupportTicketStatus.Resolved]: "Resolved",
};

const STATUS_TONES: Record<ProviderSupportTicketStatus, BadgeTone> = {
  [ProviderSupportTicketStatus.Open]: "warning",
  [ProviderSupportTicketStatus.InProgress]: "info",
  [ProviderSupportTicketStatus.Resolved]: "success",
};

/** One support ticket's full thread, with a reply box (Provider Management UX pass). */
export default function SupportTicketDetailPage() {
  const params = useParams<{ id: string }>();
  const ticketId = params.id;
  const queryClient = useQueryClient();
  const [reply, setReply] = useState("");

  const query = useQuery({
    queryKey: ["provider-support-ticket", ticketId],
    queryFn: () => getSupportTicketDetail(ticketId),
  });

  const replyMutation = useMutation({
    mutationFn: (comment: string) => addSupportTicketComment(ticketId, { comment }),
    onSuccess: (ticket) => {
      queryClient.setQueryData(["provider-support-ticket", ticketId], ticket);
      queryClient.invalidateQueries({ queryKey: ["provider-support-tickets"] });
      setReply("");
    },
  });

  if (query.isPending) {
    return (
      <div className="flex w-full max-w-3xl animate-rise flex-col gap-6">
        <Skeleton className="h-24 rounded-2xl" aria-hidden />
        <Card>
          <div className="flex flex-col gap-4" aria-hidden>
            {[0, 1, 2].map((row) => (
              <Skeleton key={row} className="h-16 rounded-xl" />
            ))}
          </div>
        </Card>
      </div>
    );
  }

  if (query.isError) {
    return (
      <div className="flex w-full max-w-3xl animate-rise flex-col gap-6">
        <PageHeading title="Ticket" />
        <Card>
          <ErrorState
            title="Couldn't load this ticket"
            error={query.error}
            onRetry={() => query.refetch()}
            isRetrying={query.isRefetching}
          />
        </Card>
      </div>
    );
  }

  const ticket = query.data;
  const canReply = ticket.status !== ProviderSupportTicketStatus.Resolved;

  return (
    <div className="flex w-full max-w-3xl animate-rise flex-col gap-6">
      <PageHeading
        title={ticket.subject}
        subtitle={`${CATEGORY_LABELS[ticket.category]} · Raised ${formatDateTime(ticket.createdAtUtc)}`}
        actions={<Badge tone={STATUS_TONES[ticket.status]}>{STATUS_LABELS[ticket.status]}</Badge>}
      />

      <Card title="Description">
        <p className="whitespace-pre-wrap text-sm leading-relaxed text-fg">{ticket.description}</p>
      </Card>

      {ticket.resolutionSummary ? (
        <Card title="Resolution">
          <p className="whitespace-pre-wrap text-sm leading-relaxed text-fg">{ticket.resolutionSummary}</p>
        </Card>
      ) : null}

      <Card title="Conversation" flush>
        {ticket.comments.length === 0 ? (
          <p className="px-5 py-8 text-center text-sm text-fg-muted">No replies yet.</p>
        ) : (
          <ul className="flex flex-col gap-4 p-5">
            {ticket.comments.map((comment) => {
              const fromAdmin = comment.authorType === ProviderSupportTicketCommentAuthorType.Admin;
              return (
                <li
                  key={comment.id}
                  className={cx("flex flex-col gap-1", fromAdmin ? "items-start" : "items-end")}
                >
                  <div
                    className={cx(
                      "max-w-[85%] rounded-2xl px-4 py-2.5 text-sm leading-relaxed",
                      fromAdmin ? "bg-surface-2 text-fg" : "bg-brand-600 text-fg-on-brand",
                    )}
                  >
                    <p className="whitespace-pre-wrap">{comment.comment}</p>
                  </div>
                  <span className="px-1 text-xs text-fg-subtle">
                    {fromAdmin ? "Glavyx Support" : "You"} · {formatDateTime(comment.createdAt)}
                  </span>
                </li>
              );
            })}
          </ul>
        )}

        {canReply ? (
          <div className="border-t border-line p-5">
            <Textarea
              label="Reply"
              rows={3}
              maxLength={2000}
              value={reply}
              onChange={(event) => setReply(event.target.value)}
            />
            {replyMutation.isError ? (
              <div className="mt-2">
                <ErrorState title="Couldn't send your reply" error={replyMutation.error} />
              </div>
            ) : null}
            <Button
              className="mt-3"
              loading={replyMutation.isPending}
              disabled={reply.trim() === ""}
              onClick={() => replyMutation.mutate(reply.trim())}
            >
              Send reply
            </Button>
          </div>
        ) : (
          <p className="border-t border-line px-5 py-4 text-sm text-fg-muted">
            This ticket is resolved. Raise a new ticket if the issue comes back.
          </p>
        )}
      </Card>
    </div>
  );
}
