"use client";

import { keepPreviousData, useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useEffect, useState } from "react";
import { Button, Field, PageHeading, Select } from "@/components/ui";
import {
  DataTable,
  FilterBar,
  Pagination,
  countActiveFilters,
  exportRowsToCsv,
  formatCurrency,
  formatDate,
} from "@/components/data-table";
import type { CsvColumn, DataTableColumn } from "@/components/data-table";
import { BookingStatusBadge } from "@/components/status-badges";
import { searchBookings } from "@/lib/bookings-api";
import type { AdminBookingListItem } from "@/lib/bookings-types";
import { listCategories } from "@/lib/catalog-api";
import { BookingStatus } from "@/lib/types";
import { BookingsTabs } from "@/components/BookingsTabs";
import { todayIsoDate } from "@/lib/date";

const PAGE_SIZE = 20;

const STATUS_OPTIONS: { value: string; label: string }[] = [
  { value: "", label: "Any status" },
  { value: String(BookingStatus.Initiated), label: "Booking Started" },
  { value: String(BookingStatus.PaymentPending), label: "Awaiting Payment" },
  { value: String(BookingStatus.PaymentFailed), label: "Payment Failed" },
  { value: String(BookingStatus.Confirmed), label: "Confirmed" },
  { value: String(BookingStatus.AwaitingFulfilment), label: "Preparing Service" },
  { value: String(BookingStatus.Assigned), label: "Professional Assigned" },
  { value: String(BookingStatus.ProviderEnRoute), label: "Professional On the Way" },
  { value: String(BookingStatus.ProviderArrived), label: "Professional Arrived" },
  { value: String(BookingStatus.InProgress), label: "In Progress" },
  { value: String(BookingStatus.Completed), label: "Completed" },
  { value: String(BookingStatus.CancelledByCustomer), label: "Cancelled by Customer" },
  { value: String(BookingStatus.CancelledByAdmin), label: "Cancelled by Admin" },
  { value: String(BookingStatus.Rescheduled), label: "Rescheduled" },
  { value: String(BookingStatus.RefundPending), label: "Refund in Progress" },
  { value: String(BookingStatus.Refunded), label: "Refunded" },
  { value: String(BookingStatus.Expired), label: "Expired" },
];

interface FilterFormState {
  reference: string;
  customerName: string;
  customerMobile: string;
  status: string;
  city: string;
  categoryId: string;
  couponCode: string;
  slotDateFrom: string;
  slotDateTo: string;
}

const EMPTY_FILTERS: FilterFormState = {
  reference: "",
  customerName: "",
  customerMobile: "",
  status: "",
  city: "",
  categoryId: "",
  couponCode: "",
  slotDateFrom: "",
  slotDateTo: "",
};

/**
 * Admin booking list/search screen (SRS 12.11.1, task 116). Filters cover
 * booking reference, customer name/mobile, status, city, category, coupon
 * code and slot date range - the SRS 12.11.1 list also mentions a "service"
 * filter (supported server-side via AdminBookingSearchParams.serviceId, and
 * listServices() could back a dropdown the same way listCategories() backs
 * Category, but no picker exists on this screen yet) and "payment status"/
 * "professional"/"source" filters that have no backing domain concept at all
 * (see BookingSearchFilter's doc comment).
 *
 * Built on the task 221 pattern. Columns are deliberately NOT sortable: this
 * list is paged server-side and the endpoint takes no sort parameter, so a
 * header sort would silently reorder only the 20 rows on screen.
 */
/**
 * Bulk action scope (task: premium UX audit, "No bulk-action affordance on
 * any large tabular list page"): row-select plus "Export selected" on this
 * one list, not a generic bulk-selection framework across every admin list.
 * Bookings is the highest-traffic list an ops admin works from and export is
 * unambiguously safe — read-only, nothing to undo — unlike a bulk status
 * change here, which would touch live bookings and belongs behind the same
 * per-booking review (cancellation reason, refund amount) the detail page
 * already requires one at a time. Full bulk-action support (status changes,
 * other list pages) remains a larger follow-up; `DataTable`'s new
 * `selection` prop is written generically so extending it is additive.
 */
const BOOKING_CSV_COLUMNS: readonly CsvColumn<AdminBookingListItem>[] = [
  { header: "Booking #", value: (booking) => booking.reference },
  { header: "Customer", value: (booking) => booking.customerName },
  { header: "Mobile", value: (booking) => booking.customerMobile },
  { header: "Service", value: (booking) => booking.serviceName },
  { header: "City", value: (booking) => booking.city },
  { header: "Slot date", value: (booking) => booking.slotDate },
  { header: "Status", value: (booking) => booking.statusLabel },
  { header: "Total", value: (booking) => booking.totalPayable },
  { header: "Created", value: (booking) => booking.createdAtUtc },
];

export default function BookingsPage() {
  const [filters, setFilters] = useState<FilterFormState>(EMPTY_FILTERS);
  const [appliedFilters, setAppliedFilters] = useState<FilterFormState>(EMPTY_FILTERS);
  const [page, setPage] = useState(1);
  // Keyed by booking id, valued with the row itself (not just the id) so a
  // selection survives paging — `DataTable` only ever holds the current
  // page's rows, but the export button needs the actual row data for every
  // booking selected across however many pages the admin visited. Cleared on
  // a new search (`onSubmit`/`onClear` below), since filters changing
  // underneath a stale selection would be confusing.
  const [selectedBookings, setSelectedBookings] = useState<Map<string, AdminBookingListItem>>(new Map());

  // Real category list for the Category filter's dropdown, not free text -
  // the search endpoint takes a CategoryId GUID (BookingRepository.SearchAsync),
  // so this can't be a typed field the way City/Coupon code are.
  const categoriesQuery = useQuery({
    queryKey: ["catalog-categories"],
    queryFn: listCategories,
    staleTime: 5 * 60 * 1000,
  });
  const categoryOptions = [
    { value: "", label: "Any category" },
    ...(categoriesQuery.data ?? []).map((category) => ({ value: category.id, label: category.name })),
  ];

  // Live typeahead for Booking # - suggests real references as the admin
  // types, reusing the same server-side Contains match the Search button
  // submits (BookingRepository.SearchAsync's Reference filter). Debounced
  // and gated at 2+ chars so it doesn't fire a request per keystroke or on
  // an empty field.
  const [debouncedReference, setDebouncedReference] = useState("");
  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedReference(filters.reference.trim()), 300);
    return () => window.clearTimeout(handle);
  }, [filters.reference]);

  const referenceSuggestionsQuery = useQuery({
    queryKey: ["admin-bookings-reference-suggestions", debouncedReference],
    queryFn: () => searchBookings({ reference: debouncedReference, page: 1, pageSize: 8 }),
    enabled: debouncedReference.length >= 2,
    placeholderData: keepPreviousData,
  });

  // Live typeahead for Customer name/mobile - same reasoning as Booking #
  // above, reusing searchBookings (there is no standalone customer search
  // client this page can import; the customers admin screen's own search is
  // page-local, not an exported function) rather than inventing an endpoint.
  const [debouncedCustomerName, setDebouncedCustomerName] = useState("");
  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedCustomerName(filters.customerName.trim()), 300);
    return () => window.clearTimeout(handle);
  }, [filters.customerName]);

  const customerNameSuggestionsQuery = useQuery({
    queryKey: ["admin-bookings-customer-name-suggestions", debouncedCustomerName],
    queryFn: () => searchBookings({ customerName: debouncedCustomerName, page: 1, pageSize: 8 }),
    enabled: debouncedCustomerName.length >= 2,
    placeholderData: keepPreviousData,
  });

  const [debouncedCustomerMobile, setDebouncedCustomerMobile] = useState("");
  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedCustomerMobile(filters.customerMobile.trim()), 300);
    return () => window.clearTimeout(handle);
  }, [filters.customerMobile]);

  const customerMobileSuggestionsQuery = useQuery({
    queryKey: ["admin-bookings-customer-mobile-suggestions", debouncedCustomerMobile],
    queryFn: () => searchBookings({ customerMobile: debouncedCustomerMobile, page: 1, pageSize: 8 }),
    enabled: debouncedCustomerMobile.length >= 2,
    placeholderData: keepPreviousData,
  });

  const query = useQuery({
    queryKey: ["admin-bookings", appliedFilters, page],
    queryFn: () =>
      searchBookings({
        reference: appliedFilters.reference || undefined,
        customerName: appliedFilters.customerName || undefined,
        customerMobile: appliedFilters.customerMobile || undefined,
        status: appliedFilters.status === "" ? undefined : (Number(appliedFilters.status) as BookingStatus),
        city: appliedFilters.city || undefined,
        categoryId: appliedFilters.categoryId || undefined,
        couponCode: appliedFilters.couponCode || undefined,
        slotDateFrom: appliedFilters.slotDateFrom || undefined,
        slotDateTo: appliedFilters.slotDateTo || undefined,
        page,
        pageSize: PAGE_SIZE,
      }),
    placeholderData: keepPreviousData,
  });

  const onSubmit = () => {
    setPage(1);
    setSelectedBookings(new Map());
    setAppliedFilters(filters);
  };

  const onClear = () => {
    setFilters(EMPTY_FILTERS);
    setAppliedFilters(EMPTY_FILTERS);
    setPage(1);
    setSelectedBookings(new Map());
  };

  const toggleSelection = (keys: Set<string>) => {
    const rows = query.data?.items ?? [];
    setSelectedBookings((current) => {
      const next = new Map(current);
      // A key present in `keys` but missing from `next` is a fresh
      // selection on the currently loaded page — look up its row there. A
      // key that was already in `next` and stays in `keys` is untouched. A
      // key removed from `keys` is deselected.
      keys.forEach((key) => {
        if (next.has(key)) return;
        const row = rows.find((booking) => booking.id === key);
        if (row) next.set(key, row);
      });
      Array.from(next.keys()).forEach((key) => {
        if (!keys.has(key)) next.delete(key);
      });
      return next;
    });
  };

  const onExportSelected = () => {
    exportRowsToCsv(
      Array.from(selectedBookings.values()),
      BOOKING_CSV_COLUMNS,
      `bookings-export-${todayIsoDate()}.csv`,
    );
  };

  const columns: DataTableColumn<AdminBookingListItem>[] = [
    {
      key: "reference",
      header: "Booking #",
      cell: (booking) => (
        <Link
          href={`/bookings/${booking.id}`}
          className="nums font-medium text-fg underline-offset-4 hover:text-brand-600 hover:underline dark:hover:text-brand-400"
        >
          {booking.reference}
        </Link>
      ),
    },
    {
      key: "customer",
      header: "Customer",
      cell: (booking) => (
        <>
          <span className="text-fg">{booking.customerName}</span>
          <div className="nums text-xs text-fg-subtle">{booking.customerMobile}</div>
        </>
      ),
    },
    { key: "service", header: "Service", cell: (booking) => booking.serviceName },
    { key: "city", header: "City", cell: (booking) => booking.city },
    {
      key: "slotDate",
      header: "Slot date",
      cell: (booking) => <span className="nums">{booking.slotDate}</span>,
    },
    {
      key: "status",
      header: "Status",
      cell: (booking) => <BookingStatusBadge status={booking.status} label={booking.statusLabel} />,
    },
    {
      key: "total",
      header: "Total",
      numeric: true,
      cell: (booking) => formatCurrency(booking.totalPayable),
    },
    {
      key: "created",
      header: "Created",
      cell: (booking) => <span className="nums">{formatDate(booking.createdAtUtc)}</span>,
    },
  ];

  return (
    <div className="w-full max-w-7xl">
      <PageHeading
        title="Bookings"
        subtitle="Search bookings and manage cancellations, reschedules and refunds (SRS 12.11)."
      />

      <BookingsTabs />

      <FilterBar
        onSubmit={onSubmit}
        onClear={onClear}
        activeCount={countActiveFilters(appliedFilters)}
        busy={query.isFetching}
      >
        <Field
          label="Booking #"
          name="reference"
          autoComplete="on"
          list="booking-reference-suggestions"
          value={filters.reference}
          onChange={(e) => setFilters((f) => ({ ...f, reference: e.target.value }))}
          placeholder="e.g. NST-260825-K7F3M"
        />
        <datalist id="booking-reference-suggestions">
          {(referenceSuggestionsQuery.data?.items ?? []).map((booking) => (
            <option key={booking.id} value={booking.reference} />
          ))}
        </datalist>
        <Field
          label="Customer name"
          name="customerName"
          autoComplete="name"
          list="booking-customer-name-suggestions"
          value={filters.customerName}
          onChange={(e) => setFilters((f) => ({ ...f, customerName: e.target.value }))}
        />
        <datalist id="booking-customer-name-suggestions">
          {(customerNameSuggestionsQuery.data?.items ?? []).map((booking) => (
            <option key={booking.id} value={booking.customerName} />
          ))}
        </datalist>
        <Field
          label="Customer mobile"
          name="customerMobile"
          autoComplete="tel"
          list="booking-customer-mobile-suggestions"
          value={filters.customerMobile}
          onChange={(e) => setFilters((f) => ({ ...f, customerMobile: e.target.value }))}
        />
        <datalist id="booking-customer-mobile-suggestions">
          {(customerMobileSuggestionsQuery.data?.items ?? []).map((booking) => (
            <option key={booking.id} value={booking.customerMobile} />
          ))}
        </datalist>
        <Select
          label="Status"
          options={STATUS_OPTIONS}
          value={filters.status}
          onChange={(e) => setFilters((f) => ({ ...f, status: e.target.value }))}
        />
        <Field
          label="City"
          name="city"
          autoComplete="address-level2"
          value={filters.city}
          onChange={(e) => setFilters((f) => ({ ...f, city: e.target.value }))}
        />
        <Select
          label="Category"
          options={categoryOptions}
          value={filters.categoryId}
          onChange={(e) => setFilters((f) => ({ ...f, categoryId: e.target.value }))}
        />
        <Field
          label="Coupon code"
          name="couponCode"
          autoComplete="on"
          value={filters.couponCode}
          onChange={(e) => setFilters((f) => ({ ...f, couponCode: e.target.value }))}
        />
        <Field
          label="Slot date from"
          type="date"
          value={filters.slotDateFrom}
          onChange={(e) => setFilters((f) => ({ ...f, slotDateFrom: e.target.value }))}
        />
        <Field
          label="Slot date to"
          type="date"
          value={filters.slotDateTo}
          onChange={(e) => setFilters((f) => ({ ...f, slotDateTo: e.target.value }))}
        />
      </FilterBar>

      <div className="mt-6">
        <DataTable
          title="Results"
          actions={
            selectedBookings.size > 0 ? (
              <>
                <span className="text-xs text-fg-subtle">{selectedBookings.size} selected</span>
                <Button size="sm" variant="secondary" onClick={onExportSelected}>
                  Export selected
                </Button>
                <Button size="sm" variant="ghost" onClick={() => setSelectedBookings(new Map())}>
                  Clear selection
                </Button>
              </>
            ) : undefined
          }
          columns={columns}
          rows={query.data?.items}
          rowKey={(booking) => booking.id}
          isLoading={query.isPending}
          isFetching={query.isFetching}
          error={query.error}
          onRetry={() => query.refetch()}
          skeletonRows={8}
          minWidth="920px"
          caption="Bookings matching the current filters"
          emptyTitle="No bookings match these filters"
          emptyDescription="Try broadening the date range, or clear the filters to see every booking."
          emptyAction={
            <Button variant="secondary" onClick={onClear}>
              Clear filters
            </Button>
          }
          selection={{
            selectedKeys: new Set(selectedBookings.keys()),
            onSelectionChange: toggleSelection,
          }}
          footer={
            query.data ? (
              <Pagination
                page={page}
                pageSize={PAGE_SIZE}
                totalCount={query.data.totalCount}
                onPageChange={setPage}
                busy={query.isFetching}
                itemLabel="booking"
              />
            ) : null
          }
        />
      </div>
    </div>
  );
}
