"use client";

import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import { ErrorState } from "@/components/states";
import { Badge, Button, Card, EmptyState, PageHeading, Skeleton } from "@/components/ui";
import type { BadgeTone } from "@/components/ui";
import { formatDate } from "@/lib/format";
import { listSupportTickets } from "@/lib/support-api";
import { ProviderSupportTicketCategory, ProviderSupportTicketStatus } from "@/lib/support-types";
import type { ProviderSupportTicketSummary } from "@/lib/support-types";

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

const FAQS: readonly { question: string; answer: string }[] = [
  {
    question: "When do I get paid?",
    answer:
      "Payouts are batched by your admin team on a regular cycle. Check Earnings → Payouts for the status of each batch - you'll also get a notification the moment one is marked paid.",
  },
  {
    question: "My KYC document was rejected - what now?",
    answer:
      "Open Profile → KYC to see the rejection reason, then upload a corrected document. Resubmitting doesn't affect your other approved documents.",
  },
  {
    question: "Why am I not seeing new job offers?",
    answer:
      "Offers only reach you when your account is Active, your service areas and skills cover the job's category and pincode, and you're under your daily job limit. Check Profile and Availability first - if everything looks right, raise a ticket below.",
  },
  {
    question: "How do I update my service areas or skills?",
    answer: "Both are editable any time from your Profile page and take effect immediately for new offers.",
  },
];

/**
 * Help & Support (Provider Management UX pass) - a provider previously had
 * no way to reach Glavyx from inside the app at all. Combines a short set of
 * answers to the questions that come up most during daily operations with
 * the real escalation path: a support ticket a human on the admin side
 * actually answers (see ProviderSupportTicketsController /
 * AdminProviderSupportTicketsController).
 */
export default function SupportPage() {
  const query = useQuery({ queryKey: ["provider-support-tickets"], queryFn: listSupportTickets });

  return (
    <div className="flex w-full max-w-4xl animate-rise flex-col gap-6">
      <PageHeading
        title="Help & Support"
        subtitle="Answers to common questions, and a direct line to the Glavyx support team for anything else."
        actions={
          <Link href="/support/new">
            <Button>Raise a ticket</Button>
          </Link>
        }
      />

      <Card title="Frequently asked">
        <dl className="flex flex-col divide-y divide-line">
          {FAQS.map((item) => (
            <div key={item.question} className="py-4 first:pt-0 last:pb-0">
              <dt className="text-[0.9375rem] font-semibold text-fg">{item.question}</dt>
              <dd className="mt-1.5 text-sm leading-relaxed text-fg-muted">{item.answer}</dd>
            </div>
          ))}
        </dl>
      </Card>

      <Card title="Your tickets" flush>
        {query.isPending ? (
          <div className="flex flex-col gap-4 p-5" aria-hidden>
            {[0, 1].map((row) => (
              <Skeleton key={row} className="h-16 rounded-xl" />
            ))}
          </div>
        ) : query.isError ? (
          <div className="p-5">
            <ErrorState
              title="Couldn't load your tickets"
              error={query.error}
              onRetry={() => query.refetch()}
              isRetrying={query.isRefetching}
            />
          </div>
        ) : query.data.length === 0 ? (
          <EmptyState
            className="border-none"
            title="No tickets yet"
            description="If the FAQ above didn't answer it, raise a ticket and we'll get back to you."
            action={
              <Link href="/support/new">
                <Button variant="secondary">Raise a ticket</Button>
              </Link>
            }
          />
        ) : (
          <ul className="divide-y divide-line">
            {query.data.map((ticket) => (
              <TicketRow key={ticket.id} ticket={ticket} />
            ))}
          </ul>
        )}
      </Card>
    </div>
  );
}

function TicketRow({ ticket }: { ticket: ProviderSupportTicketSummary }) {
  return (
    <li>
      <Link
        href={`/support/${ticket.id}`}
        className="flex items-start justify-between gap-3 px-5 py-4 transition-colors duration-fast ease-out hover:bg-surface-2"
      >
        <div className="min-w-0">
          <p className="truncate text-[0.9375rem] font-semibold text-fg">{ticket.subject}</p>
          <p className="mt-0.5 text-sm text-fg-muted">
            {CATEGORY_LABELS[ticket.category]} · Raised {formatDate(ticket.createdAtUtc)}
          </p>
        </div>
        <Badge tone={STATUS_TONES[ticket.status]} className="shrink-0">
          {STATUS_LABELS[ticket.status]}
        </Badge>
      </Link>
    </li>
  );
}
