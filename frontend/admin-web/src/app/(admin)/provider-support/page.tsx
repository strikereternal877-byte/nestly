"use client";

import { keepPreviousData, useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useState } from "react";
import { Badge, Button, Field, PageHeading, Select } from "@/components/ui";
import { DataTable, FilterBar, Pagination, countActiveFilters, formatDate } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import type { BadgeTone } from "@/components/ui";
import { useResetOnChange } from "@/hooks/useResetOnChange";
import { searchProviderSupportTickets } from "@/lib/provider-support-api";
import { ProviderSupportTicketCategory, ProviderSupportTicketStatus } from "@/lib/provider-support-types";
import type { AdminProviderSupportTicketSummaryResponse } from "@/lib/provider-support-types";

const PAGE_SIZE = 20;

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

const STATUS_OPTIONS = [
  { value: "", label: "Any status" },
  { value: String(ProviderSupportTicketStatus.Open), label: "Open" },
  { value: String(ProviderSupportTicketStatus.InProgress), label: "In progress" },
  { value: String(ProviderSupportTicketStatus.Resolved), label: "Resolved" },
];

const CATEGORY_OPTIONS = [
  { value: "", label: "Any category" },
  ...Object.entries(CATEGORY_LABELS).map(([value, label]) => ({ value, label })),
];

interface Filters {
  status: string;
  category: string;
  providerId: string;
}

const DEFAULT_FILTERS: Filters = { status: "", category: "", providerId: "" };

/**
 * Admin ticket list over provider support tickets (Provider Management UX
 * pass) - mirrors the customer support ticket list screen's shape, scaled to
 * this module's smaller filter set (no priority/assignee/dispute - see
 * ProviderSupportTicket's doc comment for why it is a separate, leaner
 * module).
 */
export default function ProviderSupportTicketsPage() {
  const [draft, setDraft] = useState<Filters>(DEFAULT_FILTERS);
  const [page, setPage] = useState(1);

  useResetOnChange([draft.status, draft.category, draft.providerId], () => setPage(1));

  const query = useQuery({
    queryKey: ["admin-provider-support-tickets", draft, page],
    queryFn: () =>
      searchProviderSupportTickets({
        status: draft.status === "" ? undefined : (Number(draft.status) as ProviderSupportTicketStatus),
        category: draft.category === "" ? undefined : (Number(draft.category) as ProviderSupportTicketCategory),
        providerId: draft.providerId.trim() === "" ? undefined : draft.providerId.trim(),
        page,
        pageSize: PAGE_SIZE,
      }),
    placeholderData: keepPreviousData,
  });

  const onReset = () => {
    setDraft(DEFAULT_FILTERS);
    setPage(1);
  };

  const columns: DataTableColumn<AdminProviderSupportTicketSummaryResponse>[] = [
    {
      key: "subject",
      header: "Subject",
      className: "max-w-sm",
      cell: (ticket) => (
        <Link
          href={`/provider-support/${ticket.id}`}
          className="font-medium text-fg underline-offset-4 hover:text-brand-600 hover:underline dark:hover:text-brand-400"
        >
          {ticket.subject}
        </Link>
      ),
    },
    {
      key: "provider",
      header: "Provider",
      cell: (ticket) => (
        <Link
          href={`/providers/${ticket.providerId}`}
          className="font-medium text-fg underline-offset-4 hover:text-brand-600 hover:underline dark:hover:text-brand-400"
        >
          {ticket.providerName}
        </Link>
      ),
    },
    { key: "category", header: "Category", cell: (ticket) => CATEGORY_LABELS[ticket.category] },
    {
      key: "status",
      header: "Status",
      cell: (ticket) => <Badge tone={STATUS_TONES[ticket.status]}>{STATUS_LABELS[ticket.status]}</Badge>,
    },
    {
      key: "created",
      header: "Created",
      cell: (ticket) => <span className="nums">{formatDate(ticket.createdAtUtc)}</span>,
    },
  ];

  return (
    <div className="w-full max-w-7xl">
      <PageHeading
        title="Provider Support"
        subtitle="Tickets providers raise from provider-web's Help & Support page, across every provider."
      />

      <FilterBar onClear={onReset} activeCount={countActiveFilters(draft)} busy={query.isFetching} columns={3}>
        <Select
          label="Status"
          options={STATUS_OPTIONS}
          value={draft.status}
          onChange={(e) => setDraft((f) => ({ ...f, status: e.target.value }))}
        />
        <Select
          label="Category"
          options={CATEGORY_OPTIONS}
          value={draft.category}
          onChange={(e) => setDraft((f) => ({ ...f, category: e.target.value }))}
        />
        <Field
          label="Provider ID"
          name="providerId"
          autoComplete="on"
          placeholder="Provider GUID"
          value={draft.providerId}
          onChange={(e) => setDraft((f) => ({ ...f, providerId: e.target.value }))}
        />
      </FilterBar>

      <div className="mt-6">
        <DataTable
          title="Tickets"
          columns={columns}
          rows={query.data?.items}
          rowKey={(ticket) => ticket.id}
          isLoading={query.isPending}
          isFetching={query.isFetching}
          error={query.error}
          onRetry={() => query.refetch()}
          caption="Provider support tickets matching the current filters"
          emptyTitle="No tickets match these filters"
          emptyDescription="Try clearing the filters to see every provider ticket."
          emptyAction={
            countActiveFilters(draft) > 0 ? (
              <Button variant="secondary" onClick={onReset}>
                Clear filters
              </Button>
            ) : undefined
          }
          skeletonRows={8}
          minWidth="900px"
          footer={
            query.data ? (
              <Pagination
                page={page}
                pageSize={PAGE_SIZE}
                totalCount={query.data.totalCount}
                onPageChange={setPage}
                busy={query.isFetching}
                itemLabel="ticket"
              />
            ) : null
          }
        />
      </div>
    </div>
  );
}
