"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useMemo, useState } from "react";
import type { FormEvent } from "react";
import { Alert, Badge, Button, Card, PageHeading, Textarea } from "@/components/ui";
import type { BadgeTone } from "@/components/ui";
import { Breadcrumbs, DescriptionList, FormActions, formatDateTime } from "@/components/data-table";
import { DetailError, DetailSkeleton } from "@/components/screen-states";
import { describeError } from "@/lib/api";
import { canWriteModule } from "@/lib/permissions";
import { getProviderSupportTicket, replyToProviderSupportTicket, resolveProviderSupportTicket } from "@/lib/provider-support-api";
import {
  ProviderSupportTicketCategory,
  ProviderSupportTicketCommentAuthorType,
  ProviderSupportTicketStatus,
} from "@/lib/provider-support-types";
import type { AdminProviderSupportTicketDetailResponse } from "@/lib/provider-support-types";
import { useAdminClaims } from "@/lib/use-admin-claims";

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

const COMMENT_AUTHOR_LABELS: Record<ProviderSupportTicketCommentAuthorType, string> = {
  [ProviderSupportTicketCommentAuthorType.Provider]: "Provider",
  [ProviderSupportTicketCommentAuthorType.Admin]: "Support",
};

const COMMENT_AUTHOR_TONES: Record<ProviderSupportTicketCommentAuthorType, BadgeTone> = {
  [ProviderSupportTicketCommentAuthorType.Provider]: "info",
  [ProviderSupportTicketCommentAuthorType.Admin]: "brand",
};

const BREADCRUMBS = [{ label: "Provider Support", href: "/provider-support" }, { label: "Ticket" }] as const;

/**
 * Admin workflow detail screen for one provider support ticket (Provider
 * Management UX pass) - comment thread plus reply/resolve, mirroring the
 * customer support ticket detail screen's shape without the
 * assign/escalate/link-booking machinery this smaller module has no
 * equivalent of (see ProviderSupportTicket's doc comment).
 */
export default function ProviderSupportTicketDetailPage() {
  const params = useParams<{ id: string }>();
  const ticketId = params.id;
  const claims = useAdminClaims();
  // The backend gates writes behind "support.write", not "provider-support.write" -
  // this nav entry reuses the customer support ticket module's permission
  // codes (see ProviderSupportTicketsController's doc comment), it just has
  // its own NavModuleKey for sidebar/nav visibility purposes.
  const canWrite = canWriteModule(claims, "support");
  const queryClient = useQueryClient();

  const [replyText, setReplyText] = useState("");
  const [resolutionSummary, setResolutionSummary] = useState("");

  const detailQuery = useQuery({
    queryKey: ["admin-provider-support-ticket-detail", ticketId],
    queryFn: () => getProviderSupportTicket(ticketId),
  });

  function useTicketMutation<TArgs>(
    mutationFn: (args: TArgs) => Promise<AdminProviderSupportTicketDetailResponse>,
    onSettledSuccess?: () => void,
  ) {
    return useMutation({
      mutationFn,
      onSuccess: (data) => {
        queryClient.setQueryData(["admin-provider-support-ticket-detail", ticketId], data);
        queryClient.invalidateQueries({ queryKey: ["admin-provider-support-tickets"] });
        onSettledSuccess?.();
      },
    });
  }

  const replyMutation = useTicketMutation(
    (comment: string) => replyToProviderSupportTicket(ticketId, { comment }),
    () => setReplyText(""),
  );
  const resolveMutation = useTicketMutation(
    (summary: string) => resolveProviderSupportTicket(ticketId, { resolutionSummary: summary }),
    () => setResolutionSummary(""),
  );

  const ticket = detailQuery.data;

  const comments = useMemo(() => {
    if (!ticket) return [];
    return [...ticket.comments].sort((a, b) => new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime());
  }, [ticket]);

  const onReply = (event: FormEvent) => {
    event.preventDefault();
    const comment = replyText.trim();
    if (!comment) return;
    replyMutation.mutate(comment);
  };

  const onResolve = (event: FormEvent) => {
    event.preventDefault();
    const summary = resolutionSummary.trim();
    if (!summary) return;
    resolveMutation.mutate(summary);
  };

  if (detailQuery.isPending) {
    return <DetailSkeleton cards={3} className="flex w-full max-w-4xl flex-col gap-6" />;
  }

  if (detailQuery.isError || !ticket) {
    return (
      <DetailError
        title="Provider support ticket"
        breadcrumbs={BREADCRUMBS}
        error={detailQuery.error}
        onRetry={() => detailQuery.refetch()}
        className="w-full max-w-4xl"
      />
    );
  }

  const isResolved = ticket.status === ProviderSupportTicketStatus.Resolved;

  return (
    <div className="flex w-full max-w-4xl flex-col gap-6">
      <PageHeading
        title={ticket.subject}
        subtitle={`${ticket.providerName} · ${CATEGORY_LABELS[ticket.category]}`}
        breadcrumbs={<Breadcrumbs items={BREADCRUMBS} />}
        actions={
          <>
            <Link href={`/providers/${ticket.providerId}`}>
              <Button variant="secondary" size="sm">
                View provider
              </Button>
            </Link>
            <Badge tone={STATUS_TONES[ticket.status]}>{STATUS_LABELS[ticket.status]}</Badge>
          </>
        }
      />

      <Card title="Ticket details">
        <DescriptionList
          columns={3}
          items={[
            { label: "Status", value: <Badge tone={STATUS_TONES[ticket.status]}>{STATUS_LABELS[ticket.status]}</Badge> },
            { label: "Category", value: CATEGORY_LABELS[ticket.category] },
            { label: "Created", value: <span className="nums">{formatDateTime(ticket.createdAtUtc)}</span> },
            { label: "Last updated", value: <span className="nums">{formatDateTime(ticket.updatedAtUtc)}</span> },
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

      <Card title="Conversation">
        {comments.length === 0 ? (
          <p className="text-sm text-fg-muted">No replies yet.</p>
        ) : (
          <ul className="flex flex-col gap-3">
            {comments.map((comment) => (
              <li key={comment.id} className="rounded-xl border border-line bg-surface-2 p-4">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <Badge tone={COMMENT_AUTHOR_TONES[comment.authorType]}>{COMMENT_AUTHOR_LABELS[comment.authorType]}</Badge>
                  <span className="nums text-xs text-fg-subtle">{formatDateTime(comment.createdAt)}</span>
                </div>
                <p className="mt-2 whitespace-pre-wrap text-sm leading-relaxed text-fg">{comment.comment}</p>
              </li>
            ))}
          </ul>
        )}

        {canWrite ? (
          <form onSubmit={onReply} className="mt-5 flex max-w-2xl flex-col gap-3 border-t border-line pt-5">
            {replyMutation.isError ? <Alert>{describeError(replyMutation.error)}</Alert> : null}
            <Textarea
              label="Reply"
              value={replyText}
              onChange={(e) => setReplyText(e.target.value)}
              placeholder="Reply to the provider"
            />
            <FormActions align="start">
              <Button type="submit" disabled={!replyText.trim()} loading={replyMutation.isPending}>
                Send reply
              </Button>
            </FormActions>
          </form>
        ) : null}
      </Card>

      {canWrite && !isResolved ? (
        <Card title="Resolve">
          <form onSubmit={onResolve} className="flex max-w-2xl flex-col gap-3">
            {resolveMutation.isError ? <Alert>{describeError(resolveMutation.error)}</Alert> : null}
            <Textarea
              label="Resolution summary"
              value={resolutionSummary}
              onChange={(e) => setResolutionSummary(e.target.value)}
              placeholder="What was done to resolve this"
            />
            <FormActions align="start">
              <Button
                type="submit"
                variant="secondary"
                disabled={!resolutionSummary.trim()}
                loading={resolveMutation.isPending}
              >
                Mark resolved
              </Button>
            </FormActions>
          </form>
        </Card>
      ) : null}
    </div>
  );
}
