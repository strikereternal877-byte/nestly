"use client";

import { keepPreviousData, useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useEffect, useState } from "react";
import { Badge, Field, PageHeading, Select } from "@/components/ui";
import type { BadgeTone } from "@/components/ui";
import {
  DataTable,
  FilterBar,
  Pagination,
  countActiveFilters,
  formatCurrency,
  formatDateTime,
} from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { PaymentsTabs } from "@/components/PaymentsTabs";
import { searchBookings } from "@/lib/bookings-api";
import { searchPaymentTransactions } from "@/lib/payments-api";
import { PaymentTransactionStatus } from "@/lib/payments-types";
import type { AdminPaymentTransactionListItem } from "@/lib/payments-types";

const PAGE_SIZE = 20;

const STATUS_LABELS: Record<PaymentTransactionStatus, string> = {
  [PaymentTransactionStatus.Pending]: "Pending",
  [PaymentTransactionStatus.Success]: "Success",
  [PaymentTransactionStatus.Failed]: "Failed",
  [PaymentTransactionStatus.Cancelled]: "Cancelled",
};

const STATUS_TONES: Record<PaymentTransactionStatus, BadgeTone> = {
  [PaymentTransactionStatus.Pending]: "warning",
  [PaymentTransactionStatus.Success]: "success",
  [PaymentTransactionStatus.Failed]: "danger",
  [PaymentTransactionStatus.Cancelled]: "neutral",
};

const STATUS_OPTIONS = [
  { value: "", label: "Any status" },
  ...Object.entries(STATUS_LABELS).map(([value, label]) => ({ value, label })),
];

interface FilterFormState {
  bookingId: string;
  status: string;
  fromDate: string;
  toDate: string;
}

const EMPTY_FILTERS: FilterFormState = { bookingId: "", status: "", fromDate: "", toDate: "" };

/**
 * Admin payment transaction view (SRS 12.13.1, task 311): a filterable,
 * paginated reconciliation list over `PaymentTransaction` rows -
 * `PaymentsController.Search` (admin-api). Previously payments were only
 * visible incidentally through a booking's own detail page; this is the
 * standalone surface, gated behind "payments.read".
 *
 * Built on the same server-paged, non-sortable pattern as the bookings and
 * audit-log list screens (see their doc comments for why no column here is
 * sortable).
 */
export default function PaymentsPage() {
  const [filters, setFilters] = useState<FilterFormState>(EMPTY_FILTERS);
  const [appliedFilters, setAppliedFilters] = useState<FilterFormState>(EMPTY_FILTERS);
  const [page, setPage] = useState(1);

  // "Booking ID" stays a plain GUID text field (PaymentTransactionRepository
  // matches it as an exact FK equality, so there's no partial-GUID server
  // search to typeahead against) - but the same booking can be found by
  // typing part of its human-readable reference instead, reusing the
  // reference Contains search bookings/page.tsx already has. The datalist
  // option's value is the real booking GUID the field must submit; its label
  // is the reference the admin actually recognises. Debounced and gated at
  // 2+ chars, same as every other live-search datalist in this app - so an
  // empty/just-clicked field shows nothing, only typing narrows it down.
  const [debouncedBookingSearch, setDebouncedBookingSearch] = useState("");
  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedBookingSearch(filters.bookingId.trim()), 300);
    return () => window.clearTimeout(handle);
  }, [filters.bookingId]);

  const bookingSuggestionsQuery = useQuery({
    queryKey: ["admin-payments-booking-suggestions", debouncedBookingSearch],
    queryFn: () => searchBookings({ reference: debouncedBookingSearch, page: 1, pageSize: 8 }),
    enabled: debouncedBookingSearch.length >= 2,
    placeholderData: keepPreviousData,
  });

  const query = useQuery({
    queryKey: ["admin-payments", appliedFilters, page],
    queryFn: () =>
      searchPaymentTransactions({
        bookingId: appliedFilters.bookingId || undefined,
        status: appliedFilters.status === "" ? undefined : (Number(appliedFilters.status) as PaymentTransactionStatus),
        fromUtc: appliedFilters.fromDate ? new Date(`${appliedFilters.fromDate}T00:00:00.000Z`).toISOString() : undefined,
        toUtc: appliedFilters.toDate ? new Date(`${appliedFilters.toDate}T23:59:59.999Z`).toISOString() : undefined,
        page,
        pageSize: PAGE_SIZE,
      }),
    placeholderData: keepPreviousData,
  });

  const onSubmit = () => {
    setPage(1);
    setAppliedFilters(filters);
  };

  const onClear = () => {
    setFilters(EMPTY_FILTERS);
    setAppliedFilters(EMPTY_FILTERS);
    setPage(1);
  };

  const columns: DataTableColumn<AdminPaymentTransactionListItem>[] = [
    {
      key: "transaction",
      header: "Transaction",
      cell: (transaction) => (
        <>
          <Link
            href={`/payments/${transaction.id}`}
            className="nums font-medium text-fg underline-offset-4 hover:text-brand-600 hover:underline dark:hover:text-brand-400"
          >
            {transaction.id.slice(0, 8)}
          </Link>
          <div className="nums mt-0.5 text-xs text-fg-subtle">
            <Link href={`/bookings/${transaction.bookingId}`} className="hover:underline">
              Booking {transaction.bookingId.slice(0, 8)}
            </Link>
          </div>
        </>
      ),
    },
    {
      key: "status",
      header: "Status",
      cell: (transaction) => (
        <Badge tone={STATUS_TONES[transaction.status]}>{STATUS_LABELS[transaction.status]}</Badge>
      ),
    },
    {
      key: "amount",
      header: "Amount",
      numeric: true,
      cell: (transaction) => (
        <span className="nums">
          {transaction.currency} {formatCurrency(transaction.amount)}
        </span>
      ),
    },
    {
      key: "gateway",
      header: "Gateway reference",
      cell: (transaction) =>
        transaction.latestGatewayPaymentRef ?? transaction.latestGatewayOrderId ?? (
          <span className="text-fg-subtle">—</span>
        ),
    },
    {
      key: "created",
      header: "Created",
      cell: (transaction) => <span className="nums">{formatDateTime(transaction.createdAtUtc)}</span>,
    },
    {
      key: "updated",
      header: "Updated",
      cell: (transaction) => <span className="nums">{formatDateTime(transaction.updatedAtUtc)}</span>,
    },
  ];

  return (
    <div className="w-full max-w-7xl">
      <PageHeading
        title="Payments"
        subtitle="Every payment transaction, filterable by booking and gateway status - the reconciliation surface for ops (SRS 12.13.1)."
      />

      <PaymentsTabs />

      <FilterBar
        onSubmit={onSubmit}
        onClear={onClear}
        activeCount={countActiveFilters(appliedFilters)}
        busy={query.isFetching}
      >
        <Field
          label="Booking ID"
          name="bookingId"
          autoComplete="on"
          list="payments-booking-id-suggestions"
          value={filters.bookingId}
          onChange={(e) => setFilters((f) => ({ ...f, bookingId: e.target.value }))}
          placeholder="Exact booking ID, or type a reference to search"
        />
        <datalist id="payments-booking-id-suggestions">
          {(bookingSuggestionsQuery.data?.items ?? []).map((booking) => (
            <option key={booking.id} value={booking.id} label={booking.reference} />
          ))}
        </datalist>
        <Select
          label="Status"
          options={STATUS_OPTIONS}
          value={filters.status}
          onChange={(e) => setFilters((f) => ({ ...f, status: e.target.value }))}
        />
        <Field
          label="Created from"
          type="date"
          value={filters.fromDate}
          onChange={(e) => setFilters((f) => ({ ...f, fromDate: e.target.value }))}
        />
        <Field
          label="Created to"
          type="date"
          value={filters.toDate}
          onChange={(e) => setFilters((f) => ({ ...f, toDate: e.target.value }))}
        />
      </FilterBar>

      <div className="mt-6">
        <DataTable
          title="Transactions"
          columns={columns}
          rows={query.data?.items}
          rowKey={(transaction) => transaction.id}
          isLoading={query.isPending}
          isFetching={query.isFetching}
          error={query.error}
          onRetry={() => query.refetch()}
          skeletonRows={8}
          minWidth="920px"
          caption="Payment transactions matching the current filters"
          emptyTitle="No transactions match these filters"
          emptyDescription="Try widening the date range, or clear the filters to see every transaction."
          footer={
            query.data ? (
              <Pagination
                page={page}
                pageSize={PAGE_SIZE}
                totalCount={query.data.totalCount}
                onPageChange={setPage}
                busy={query.isFetching}
                itemLabel="transaction"
              />
            ) : null
          }
        />
      </div>
    </div>
  );
}
