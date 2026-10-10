"use client";

import { keepPreviousData, useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useEffect, useState } from "react";
import { Badge, Button, Field, PageHeading, Select } from "@/components/ui";
import { DataTable, FilterBar, Pagination, countActiveFilters, formatDate } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { useResetOnChange } from "@/hooks/useResetOnChange";
import { TicketPriorityBadge, TicketStatusBadge } from "@/components/status-badges";
import { searchBookings } from "@/lib/bookings-api";
import { listAssignableAdmins, searchSupportTickets } from "@/lib/support-api";
import {
  CATEGORY_FILTER_OPTIONS,
  DEFAULT_SUPPORT_TICKET_FILTERS,
  PRIORITY_FILTER_OPTIONS,
  STATUS_FILTER_OPTIONS,
  UNASSIGNED_FILTER_OPTIONS,
  buildSupportTicketSearchQuery,
  categoryLabel,
  priorityLabel,
  statusLabel,
} from "@/lib/support";
import type { SupportTicketFilters } from "@/lib/support";
import type { AdminSupportTicketSummaryResponse } from "@/lib/support-types";

const PAGE_SIZE = 20;

/**
 * Admin ticket list screen (SRS 12.14.1, tasks 120f/121): filterable search
 * across every customer's tickets (status, category, priority, customer,
 * booking, assignee, unassigned-only, date range), linking each row through
 * to the workflow detail screen at `/support/[id]` where assign/respond/
 * escalate/resolve/link-booking actually happen.
 *
 * Columns are deliberately not sortable: the list is paged server-side and
 * the endpoint takes no sort parameter, so a header sort would reorder only
 * the 20 rows on screen.
 */
export default function SupportTicketsPage() {
  // Live filtering (no Search button): Customer ID, Booking ID and Assigned
  // admin ID are debounced 300ms, same convention as customers/page.tsx;
  // Status, Category, Priority, Assignment and the date range are all
  // dropdowns/date-pickers and apply immediately.
  const [draft, setDraft] = useState<SupportTicketFilters>(DEFAULT_SUPPORT_TICKET_FILTERS);
  const [page, setPage] = useState(1);

  // Real assignable-admin list to suggest against the "Assigned admin ID"
  // field, which stays a plain GUID text input (never a dropdown) - the
  // search endpoint takes an AssignedAdminUserId GUID
  // (SupportTicketRepository.SearchAsync) and this is the same list the
  // ticket detail screen's own "Assign" control already offers, exposed here
  // as a <datalist> so the admin can pick a real name and still see/paste the
  // GUID the field actually submits.
  const assignableAdminsQuery = useQuery({
    queryKey: ["support-assignable-admins"],
    queryFn: listAssignableAdmins,
    staleTime: 5 * 60 * 1000,
  });

  // "Booking ID" is also a plain GUID text field matched as an exact FK
  // equality (SupportTicketRepository.SearchAsync), so it's suggested the
  // same way as Payments' Booking ID field: search by the booking's
  // human-readable reference (Contains, already exposed via searchBookings)
  // and fill the field with the real GUID the search actually needs. The
  // same debounced value doubles as the live filter's bookingId, rather than
  // debouncing it twice.
  const [debouncedBookingSearch, setDebouncedBookingSearch] = useState("");
  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedBookingSearch(draft.bookingId.trim()), 300);
    return () => window.clearTimeout(handle);
  }, [draft.bookingId]);

  const bookingSuggestionsQuery = useQuery({
    queryKey: ["admin-support-booking-suggestions", debouncedBookingSearch],
    queryFn: () => searchBookings({ reference: debouncedBookingSearch, page: 1, pageSize: 8 }),
    enabled: debouncedBookingSearch.length >= 2,
    placeholderData: keepPreviousData,
  });

  const [debouncedCustomerId, setDebouncedCustomerId] = useState("");
  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedCustomerId(draft.customerId.trim()), 300);
    return () => window.clearTimeout(handle);
  }, [draft.customerId]);

  const [debouncedAssignedAdminUserId, setDebouncedAssignedAdminUserId] = useState("");
  useEffect(() => {
    const handle = window.setTimeout(
      () => setDebouncedAssignedAdminUserId(draft.assignedAdminUserId.trim()),
      300,
    );
    return () => window.clearTimeout(handle);
  }, [draft.assignedAdminUserId]);

  const liveFilters: SupportTicketFilters = {
    status: draft.status,
    category: draft.category,
    priority: draft.priority,
    unassigned: draft.unassigned,
    fromDate: draft.fromDate,
    toDate: draft.toDate,
    customerId: debouncedCustomerId,
    bookingId: debouncedBookingSearch,
    assignedAdminUserId: debouncedAssignedAdminUserId,
  };

  // Any filter change resets to page 1 (same pattern as customers/page.tsx).
  useResetOnChange(
    [
      draft.status,
      draft.category,
      draft.priority,
      draft.unassigned,
      draft.fromDate,
      draft.toDate,
      debouncedCustomerId,
      debouncedBookingSearch,
      debouncedAssignedAdminUserId,
    ],
    () => setPage(1),
  );

  const query = useQuery({
    queryKey: ["admin-support-tickets", liveFilters, page],
    queryFn: () => searchSupportTickets(buildSupportTicketSearchQuery(liveFilters, { page, pageSize: PAGE_SIZE })),
    placeholderData: keepPreviousData,
  });

  const onReset = () => {
    setDraft(DEFAULT_SUPPORT_TICKET_FILTERS);
    setDebouncedCustomerId("");
    setDebouncedBookingSearch("");
    setDebouncedAssignedAdminUserId("");
    setPage(1);
  };

  const columns: DataTableColumn<AdminSupportTicketSummaryResponse>[] = [
    {
      key: "subject",
      header: "Subject",
      className: "max-w-sm",
      cell: (ticket) => (
        <div className="flex flex-wrap items-center gap-2">
          <Link
            href={`/support/${ticket.id}`}
            className="font-medium text-fg underline-offset-4 hover:text-brand-600 hover:underline dark:hover:text-brand-400"
          >
            {ticket.subject}
          </Link>
          {ticket.isDisputed ? <Badge tone="warning">Disputed</Badge> : null}
        </div>
      ),
    },
    {
      key: "customer",
      header: "Customer",
      cell: (ticket) => (
        <Link
          href={`/customers/${ticket.customerId}`}
          className="font-medium text-fg underline-offset-4 hover:text-brand-600 hover:underline dark:hover:text-brand-400"
        >
          {ticket.customerName}
        </Link>
      ),
    },
    { key: "category", header: "Category", cell: (ticket) => categoryLabel(ticket.category) },
    {
      key: "priority",
      header: "Priority",
      cell: (ticket) => (
        <TicketPriorityBadge priority={ticket.priority} label={priorityLabel(ticket.priority)} />
      ),
    },
    {
      key: "status",
      header: "Status",
      cell: (ticket) => <TicketStatusBadge status={ticket.status} label={statusLabel(ticket.status)} />,
    },
    {
      key: "assignee",
      header: "Assigned to",
      cell: (ticket) =>
        ticket.assignedAdminName ?? <span className="text-fg-subtle">Unassigned</span>,
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
        title="Support Tickets"
        subtitle="Search, assign, respond to and resolve customer support tickets across every customer (SRS 12.14)."
      />

      <FilterBar
        onClear={onReset}
        activeCount={countActiveFilters(draft)}
        busy={query.isFetching}
      >
        <Select
          label="Status"
          options={STATUS_FILTER_OPTIONS}
          value={draft.status}
          onChange={(e) => setDraft((f) => ({ ...f, status: e.target.value as SupportTicketFilters["status"] }))}
        />
        <Select
          label="Category"
          options={CATEGORY_FILTER_OPTIONS}
          value={draft.category}
          onChange={(e) => setDraft((f) => ({ ...f, category: e.target.value as SupportTicketFilters["category"] }))}
        />
        <Select
          label="Priority"
          options={PRIORITY_FILTER_OPTIONS}
          value={draft.priority}
          onChange={(e) => setDraft((f) => ({ ...f, priority: e.target.value as SupportTicketFilters["priority"] }))}
        />
        <Select
          label="Assignment"
          options={UNASSIGNED_FILTER_OPTIONS}
          value={draft.unassigned}
          onChange={(e) =>
            setDraft((f) => ({ ...f, unassigned: e.target.value as SupportTicketFilters["unassigned"] }))
          }
        />
        <Field
          label="Customer ID"
          name="customerId"
          autoComplete="on"
          placeholder="Customer GUID"
          value={draft.customerId}
          onChange={(e) => setDraft((f) => ({ ...f, customerId: e.target.value }))}
        />
        <Field
          label="Booking ID"
          name="bookingId"
          autoComplete="on"
          list="support-booking-id-suggestions"
          placeholder="Booking GUID, or type a reference to search"
          value={draft.bookingId}
          onChange={(e) => setDraft((f) => ({ ...f, bookingId: e.target.value }))}
        />
        <datalist id="support-booking-id-suggestions">
          {(bookingSuggestionsQuery.data?.items ?? []).map((booking) => (
            <option key={booking.id} value={booking.id} label={booking.reference} />
          ))}
        </datalist>
        <Field
          label="Assigned admin ID"
          name="assignedAdminUserId"
          autoComplete="on"
          list="support-assignable-admins"
          placeholder="Admin user GUID"
          value={draft.assignedAdminUserId}
          onChange={(e) => setDraft((f) => ({ ...f, assignedAdminUserId: e.target.value }))}
        />
        {/* Options only appear once the admin has typed something - an empty
            field must not pop the entire admin list on click. Matches
            against the typed text as either a name or a pasted GUID prefix. */}
        <datalist id="support-assignable-admins">
          {draft.assignedAdminUserId.trim()
            ? (assignableAdminsQuery.data ?? [])
                .filter((admin) => {
                  const term = draft.assignedAdminUserId.trim().toLowerCase();
                  return admin.fullName.toLowerCase().includes(term) || admin.id.toLowerCase().includes(term);
                })
                .map((admin) => <option key={admin.id} value={admin.id} label={admin.fullName} />)
            : null}
        </datalist>
        <Field
          label="Created from"
          type="date"
          max={draft.toDate || undefined}
          value={draft.fromDate}
          onChange={(e) => setDraft((f) => ({ ...f, fromDate: e.target.value }))}
        />
        <Field
          label="Created to"
          type="date"
          min={draft.fromDate || undefined}
          value={draft.toDate}
          onChange={(e) => setDraft((f) => ({ ...f, toDate: e.target.value }))}
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
          caption="Support tickets matching the current filters"
          emptyTitle="No tickets match these filters"
          emptyDescription="Try broadening the date range, or clear the filters to see every ticket."
          emptyAction={
            countActiveFilters(draft) > 0 ? (
              <Button variant="secondary" onClick={onReset}>
                Clear filters
              </Button>
            ) : undefined
          }
          skeletonRows={8}
          minWidth="1080px"
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
