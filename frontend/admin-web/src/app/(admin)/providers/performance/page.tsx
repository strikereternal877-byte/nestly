"use client";

import { keepPreviousData, useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useState } from "react";
import { Alert, Button, PageHeading, Select } from "@/components/ui";
import { DataTable, Pagination } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { ProviderStatusBadge } from "@/components/status-badges";
import { describeError } from "@/lib/api";
import { listProviderPerformance } from "@/lib/providers-api";
import { ProviderPerformanceSortField, ProviderStatus } from "@/lib/providers-types";
import type { ProviderPerformanceSummary } from "@/lib/providers-types";
import { ProvidersTabs } from "../_components/ProvidersTabs";

const PAGE_SIZE = 20;

const STATUS_LABELS: Record<ProviderStatus, string> = {
  [ProviderStatus.PendingVerification]: "Pending verification",
  [ProviderStatus.Active]: "Active",
  [ProviderStatus.Suspended]: "Suspended",
  [ProviderStatus.Deactivated]: "Deactivated",
};

const PERIOD_OPTIONS = [
  { value: "7", label: "Last 7 days" },
  { value: "30", label: "Last 30 days" },
  { value: "90", label: "Last 90 days" },
];

function formatPercent(value: number | null): string {
  return value === null ? "—" : `${value}%`;
}

function formatMinutes(value: number | null): string {
  if (value === null) return "—";
  if (value < 60) return `${Math.round(value)} min`;
  return `${(value / 60).toFixed(1)} hr`;
}

function formatRating(row: ProviderPerformanceSummary): string {
  return row.averageRating === null ? "—" : `★ ${row.averageRating} (${row.ratingCount})`;
}

/** A clickable column header that drives the server-side sort below - see the page's own doc comment for why this does NOT use DataTable's client-side `sortValue`. */
function SortableHeader({
  label,
  field,
  activeField,
  descending,
  onSort,
}: {
  label: string;
  field: ProviderPerformanceSortField;
  activeField: ProviderPerformanceSortField;
  descending: boolean;
  onSort: (field: ProviderPerformanceSortField) => void;
}) {
  const isActive = field === activeField;
  return (
    <button
      type="button"
      onClick={() => onSort(field)}
      className="inline-flex items-center gap-1 rounded font-bold text-fg transition-colors duration-fast ease-out hover:text-brand-600 dark:hover:text-brand-400"
    >
      {label}
      <span aria-hidden className={isActive ? "text-brand-600 dark:text-brand-400" : "text-fg-subtle"}>
        {isActive ? (descending ? "▼" : "▲") : "▲"}
      </span>
    </button>
  );
}

/**
 * Provider performance ranking (docs/OPEN-FIXES-FEATURES.csv "Admin Web,
 * Proposed new page, Provider performance"): "No data to judge or rank
 * providers... not enough to choose between providers or to build fair
 * auto-assignment." This is that data - offers received, acceptance rate,
 * average response time, completion rate and average rating, per provider,
 * over a rolling window, sortable by any column.
 *
 * Sorting/pagination happen server-side
 * (`GET /admin/providers/performance?sortBy=&sortDescending=&page=`) - unlike
 * most admin list screens, this table's ranking is exactly the point, so a
 * column header here posts a new query rather than reordering the DataTable
 * client-side over one visible page (see data-table.tsx's own warning about
 * mixing the two).
 *
 * No "Cancellations" column: `BookingProviderAssignmentStatus` has no state
 * for "provider accepted, then backed out" distinct from a plain decline or
 * a booking-side cancellation - see `ProviderPerformanceSummaryResponse`'s
 * C# doc comment (ProviderManagementContracts.cs) for the full reasoning.
 * Fabricating a number here from data that does not exist would be worse
 * than leaving the column out.
 */
export default function ProviderPerformancePage() {
  const [page, setPage] = useState(1);
  const [periodDays, setPeriodDays] = useState(30);
  const [sortBy, setSortBy] = useState(ProviderPerformanceSortField.OffersReceived);
  const [sortDescending, setSortDescending] = useState(true);

  const query = useQuery({
    queryKey: ["admin-provider-performance", page, periodDays, sortBy, sortDescending] as const,
    queryFn: () =>
      listProviderPerformance({ page, pageSize: PAGE_SIZE, periodDays, sortBy, sortDescending }),
    placeholderData: keepPreviousData,
  });

  const onSort = (field: ProviderPerformanceSortField) => {
    setPage(1);
    setSortDescending((current) => (field === sortBy ? !current : true));
    setSortBy(field);
  };

  const header = (field: ProviderPerformanceSortField, label: string) => (
    <SortableHeader label={label} field={field} activeField={sortBy} descending={sortDescending} onSort={onSort} />
  );

  const columns: DataTableColumn<ProviderPerformanceSummary>[] = [
    {
      key: "name",
      header: header(ProviderPerformanceSortField.DisplayName, "Provider"),
      cell: (row) => (
        <Link
          href={`/providers/${row.providerId}`}
          className="font-medium text-fg underline-offset-4 hover:text-brand-600 hover:underline dark:hover:text-brand-400"
        >
          {row.displayName}
        </Link>
      ),
    },
    {
      key: "status",
      header: "Status",
      cell: (row) => <ProviderStatusBadge status={row.status} label={STATUS_LABELS[row.status]} />,
    },
    {
      key: "offers",
      header: header(ProviderPerformanceSortField.OffersReceived, "Offers received"),
      numeric: true,
      cell: (row) => row.offersReceived,
    },
    {
      key: "acceptanceRate",
      header: header(ProviderPerformanceSortField.AcceptanceRate, "Acceptance rate"),
      numeric: true,
      cell: (row) => formatPercent(row.acceptanceRatePercent),
    },
    {
      key: "responseTime",
      header: header(ProviderPerformanceSortField.AverageResponseTime, "Avg. response time"),
      numeric: true,
      cell: (row) => formatMinutes(row.averageResponseTimeMinutes),
    },
    {
      key: "completionRate",
      header: header(ProviderPerformanceSortField.CompletionRate, "Completion rate"),
      numeric: true,
      cell: (row) => formatPercent(row.completionRatePercent),
    },
    {
      key: "rating",
      header: header(ProviderPerformanceSortField.AverageRating, "Rating"),
      numeric: true,
      cell: formatRating,
    },
  ];

  return (
    <div className="w-full max-w-6xl">
      <PageHeading
        title="Provider Performance"
        subtitle="Offers received, acceptance rate, response time, completion rate and rating - the numbers to judge or rank providers by (PROVIDER.md)."
      />
      <ProvidersTabs />

      <div className="mb-6 flex flex-wrap items-end justify-between gap-4">
        <Select
          label="Window"
          value={String(periodDays)}
          onChange={(e) => {
            setPeriodDays(Number(e.target.value));
            setPage(1);
          }}
          options={PERIOD_OPTIONS}
          className="w-44"
        />
        {query.data ? (
          <span className="text-sm text-fg-muted">
            {query.data.totalCount} {query.data.totalCount === 1 ? "provider" : "providers"}
          </span>
        ) : null}
      </div>

      {query.isError ? (
        <Alert tone="error" action={<Button size="sm" onClick={() => query.refetch()}>Retry</Button>}>
          {describeError(query.error)}
        </Alert>
      ) : null}

      <DataTable
        title="Ranking"
        description="Ratings are all-time; every other column is scoped to the window above."
        columns={columns}
        rows={query.data?.items}
        rowKey={(row) => row.providerId}
        isLoading={query.isPending}
        isFetching={query.isFetching}
        skeletonRows={8}
        minWidth="1000px"
        caption="Provider performance ranking for the selected window"
        emptyTitle="No providers to rank yet"
        emptyDescription="Providers appear here once they have received at least one job offer."
        footer={
          query.data ? (
            <Pagination
              page={page}
              pageSize={PAGE_SIZE}
              totalCount={query.data.totalCount}
              onPageChange={setPage}
              busy={query.isFetching}
              itemLabel="provider"
            />
          ) : null
        }
      />
    </div>
  );
}
