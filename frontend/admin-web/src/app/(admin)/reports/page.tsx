"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useState } from "react";
import { Alert, Badge, Field, PageHeading, StatTile } from "@/components/ui";
import type { BadgeTone } from "@/components/ui";
import { DataTable, FilterBar, countActiveFilters, formatCurrency, formatDateTime } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { CustomerStatusBadge } from "@/components/status-badges";
import { describeError } from "@/lib/api";
import { endOfLocalDayUtc, startOfLocalDayUtc } from "@/lib/day-range";
import { isoDateOffsetFromToday, todayIsoDate } from "@/lib/date";
import { canWriteModule } from "@/lib/permissions";
import { listCities } from "@/lib/serviceability-api";
import { categoryLabel } from "@/lib/support";
import { useAdminClaims } from "@/lib/use-admin-claims";
import {
  downloadBlob,
  exportBookingRevenueCsv,
  exportCouponUsageCsv,
  exportCustomerSegmentationCsv,
  exportRefundCsv,
  exportSupportTicketCsv,
  getBookingRevenueReport,
  getCouponUsageReport,
  getCustomerSegmentationReport,
  getRefundReport,
  getSupportTicketReport,
} from "@/lib/reports-api";
import { RefundMethod, RefundStatus, RefundType } from "@/lib/reports-types";
import type {
  CouponUsageReportRow,
  CustomerCitySegmentRow,
  CustomerStatusSegmentRow,
  RefundReportRow,
  SupportTicketCategoryVolumeRow,
} from "@/lib/reports-types";
import { ExportQueueCard } from "./_components/ExportQueueCard";
import { ReportCard, StatGrid, StatGridSkeleton, TableSkeleton } from "./_components/ReportCard";

/**
 * Reports and exports (SRS 12.18, tasks 128a-129): the standard admin reports
 * over one shared date range, each with an instant CSV export, plus the
 * permission-gated async export queue (128d) for ranges too large to render.
 *
 * Instant export is visible to any admin holding "reports.read" (mirrors
 * `ReviewsController`'s CSV export, which needs only Read); requesting an
 * async job is gated on "reports.write" since it persists a job and occupies
 * a background worker slot.
 *
 * Every date on this screen is a *local calendar day*. The range is converted
 * to instants with `lib/day-range`, not `${date}T00:00:00.000Z` — the latter
 * declares the admin's day boundary to be a UTC one and, in IST, shifts every
 * report's window by 5h30m.
 */

interface ReportRange {
  fromDate: string;
  toDate: string;
  city: string;
}

function defaultRange(): ReportRange {
  return { fromDate: isoDateOffsetFromToday(-30), toDate: todayIsoDate(), city: "" };
}

const REFUND_TYPE_LABELS: Record<RefundType, string> = {
  [RefundType.Full]: "Full",
  [RefundType.Partial]: "Partial",
};

const REFUND_METHOD_LABELS: Record<RefundMethod, string> = {
  [RefundMethod.Gateway]: "Gateway",
  [RefundMethod.Wallet]: "Wallet",
};

const REFUND_STATUS_LABELS: Record<RefundStatus, string> = {
  [RefundStatus.Initiated]: "Initiated",
  [RefundStatus.Processing]: "Processing",
  [RefundStatus.Refunded]: "Refunded",
  [RefundStatus.Failed]: "Failed",
};

const REFUND_STATUS_TONES: Record<RefundStatus, BadgeTone> = {
  [RefundStatus.Initiated]: "info",
  [RefundStatus.Processing]: "warning",
  [RefundStatus.Refunded]: "success",
  [RefundStatus.Failed]: "danger",
};

export default function ReportsPage() {
  const claims = useAdminClaims();
  const canRequestAsyncExport = canWriteModule(claims, "reports");

  // One initialiser, evaluated once. Calling the defaults separately for the
  // draft and the applied range meant a page opened across midnight could
  // start with the form and the query disagreeing about "today".
  const [draft, setDraft] = useState<ReportRange>(defaultRange);
  const [applied, setApplied] = useState<ReportRange>(draft);
  const [rangeError, setRangeError] = useState<string | null>(null);

  const [exportError, setExportError] = useState<string | null>(null);
  const [runningExport, setRunningExport] = useState<string | null>(null);

  // Real city list to suggest against the City field, which stays a plain
  // text input (never a dropdown) - ReportingQueryService's Booking &
  // Revenue report matches city with a case-insensitive exact match (not a
  // substring search), so a real city name posts cleanly whether typed or
  // picked from the datalist.
  const citiesQuery = useQuery({
    queryKey: ["cities"],
    queryFn: () => listCities(),
    staleTime: 5 * 60 * 1000,
  });

  // `applied` is only ever set from a validated range, so these cannot be
  // null in practice; `?? ""` keeps that unreachable branch type-safe and the
  // API client drops empty query values rather than sending "null".
  const fromUtc = startOfLocalDayUtc(applied.fromDate) ?? "";
  const toUtc = endOfLocalDayUtc(applied.toDate) ?? "";

  const dateRangeFilters = { fromUtc, toUtc };
  const bookingRevenueFilters = { fromUtc, toUtc, city: applied.city || undefined };
  const segmentationFilters = { registeredFromUtc: fromUtc, registeredToUtc: toUtc };

  const bookingRevenueQuery = useQuery({
    queryKey: ["reports", "booking-revenue", bookingRevenueFilters] as const,
    queryFn: () => getBookingRevenueReport(bookingRevenueFilters),
  });
  const refundQuery = useQuery({
    queryKey: ["reports", "refunds", dateRangeFilters] as const,
    queryFn: () => getRefundReport(dateRangeFilters),
  });
  const couponUsageQuery = useQuery({
    queryKey: ["reports", "coupon-usage", dateRangeFilters] as const,
    queryFn: () => getCouponUsageReport(dateRangeFilters),
  });
  const segmentationQuery = useQuery({
    queryKey: ["reports", "customer-segmentation", dateRangeFilters] as const,
    queryFn: () => getCustomerSegmentationReport(segmentationFilters),
  });
  const supportTicketQuery = useQuery({
    queryKey: ["reports", "support-tickets", dateRangeFilters] as const,
    queryFn: () => getSupportTicketReport(dateRangeFilters),
  });

  /**
   * CSV export. The previous version had no in-flight state at all, so a
   * double-click on a slow connection fired two requests and saved two copies
   * of the same file; one export at a time now, with the button showing it.
   */
  async function runExport(key: string, exportFn: () => Promise<Blob>, fileNamePrefix: string) {
    if (runningExport) return;
    setRunningExport(key);
    setExportError(null);
    try {
      const blob = await exportFn();
      downloadBlob(blob, `${fileNamePrefix}-${todayIsoDate()}.csv`);
    } catch (err) {
      setExportError(describeError(err));
    } finally {
      setRunningExport(null);
    }
  }

  function applyRange() {
    if (!draft.fromDate || !draft.toDate) {
      setRangeError("Pick both a start and an end date.");
      return;
    }
    if (draft.fromDate > draft.toDate) {
      setRangeError("The start date must not be after the end date.");
      return;
    }
    setRangeError(null);
    setApplied(draft);
  }

  const refundColumns: DataTableColumn<RefundReportRow>[] = [
    {
      key: "booking",
      header: "Booking",
      cell: (row) => (
        <Link
          href={`/bookings/${row.bookingId}`}
          className="nums font-medium text-fg underline-offset-4 hover:text-brand-600 hover:underline dark:hover:text-brand-400"
        >
          {row.bookingId}
        </Link>
      ),
    },
    {
      key: "type",
      header: "Type",
      sortValue: (row) => row.type,
      cell: (row) => REFUND_TYPE_LABELS[row.type],
    },
    {
      key: "method",
      header: "Method",
      sortValue: (row) => row.method,
      cell: (row) => REFUND_METHOD_LABELS[row.method],
    },
    {
      key: "amount",
      header: "Amount",
      numeric: true,
      sortValue: (row) => row.amount,
      cell: (row) => <span className="nums">{formatCurrency(row.amount)}</span>,
    },
    {
      key: "status",
      header: "Status",
      sortValue: (row) => row.status,
      cell: (row) => <Badge tone={REFUND_STATUS_TONES[row.status]}>{REFUND_STATUS_LABELS[row.status]}</Badge>,
    },
    {
      key: "reason",
      header: "Reason",
      cell: (row) => <span className="max-w-xs truncate text-fg-muted" title={row.reason}>{row.reason}</span>,
    },
    {
      key: "createdAt",
      header: "Created",
      sortValue: (row) => row.createdAtUtc,
      cell: (row) => <span className="nums">{formatDateTime(row.createdAtUtc)}</span>,
    },
  ];

  const couponColumns: DataTableColumn<CouponUsageReportRow>[] = [
    {
      key: "code",
      header: "Code",
      sortValue: (row) => row.couponCode,
      cell: (row) => <span className="nums font-medium text-fg">{row.couponCode}</span>,
    },
    {
      key: "redemptions",
      header: "Redemptions",
      numeric: true,
      sortValue: (row) => row.redemptionCount,
      cell: (row) => row.redemptionCount.toLocaleString("en-IN"),
    },
    {
      key: "discount",
      header: "Discount given",
      numeric: true,
      sortValue: (row) => row.totalDiscountAmount,
      cell: (row) => formatCurrency(row.totalDiscountAmount),
    },
  ];

  const statusColumns: DataTableColumn<CustomerStatusSegmentRow>[] = [
    {
      key: "status",
      header: "Status",
      sortValue: (row) => row.status,
      cell: (row) => <CustomerStatusBadge status={row.status} />,
    },
    {
      key: "count",
      header: "Customers",
      numeric: true,
      sortValue: (row) => row.count,
      cell: (row) => row.count.toLocaleString("en-IN"),
    },
  ];

  const cityColumns: DataTableColumn<CustomerCitySegmentRow>[] = [
    {
      key: "city",
      header: "City",
      sortValue: (row) => row.city,
      cell: (row) => row.city || <span className="text-fg-subtle">Not set</span>,
    },
    {
      key: "count",
      header: "Customers",
      numeric: true,
      sortValue: (row) => row.count,
      cell: (row) => row.count.toLocaleString("en-IN"),
    },
  ];

  const categoryColumns: DataTableColumn<SupportTicketCategoryVolumeRow>[] = [
    {
      key: "category",
      header: "Category",
      sortValue: (row) => categoryLabel(row.category),
      cell: (row) => categoryLabel(row.category),
    },
    {
      key: "count",
      header: "Tickets",
      numeric: true,
      sortValue: (row) => row.count,
      cell: (row) => row.count.toLocaleString("en-IN"),
    },
  ];

  return (
    <div className="w-full max-w-6xl">
      <PageHeading
        title="Reports & Exports"
        subtitle="Standard admin reports over one shared date range with CSV export, plus an async export queue for large ranges (SRS 12.18)."
      />

      <div className="flex animate-rise flex-col gap-6">
        <FilterBar
          columns={3}
          submitLabel="Apply"
          activeCount={countActiveFilters({ city: applied.city })}
          busy={
            bookingRevenueQuery.isFetching ||
            refundQuery.isFetching ||
            couponUsageQuery.isFetching ||
            segmentationQuery.isFetching ||
            supportTicketQuery.isFetching
          }
          onSubmit={applyRange}
          onClear={
            draft.city
              ? () => {
                  const cleared = { ...draft, city: "" };
                  setDraft(cleared);
                  setApplied(cleared);
                }
              : undefined
          }
        >
          <Field
            label="From"
            type="date"
            max={draft.toDate || undefined}
            value={draft.fromDate}
            onChange={(event) => setDraft({ ...draft, fromDate: event.target.value })}
          />
          <Field
            label="To"
            type="date"
            min={draft.fromDate || undefined}
            value={draft.toDate}
            onChange={(event) => setDraft({ ...draft, toDate: event.target.value })}
          />
          <Field
            label="City"
            list="reports-city-suggestions"
            hint="Booking & Revenue only. Leave blank for every city."
            placeholder="Optional"
            value={draft.city}
            onChange={(event) => setDraft({ ...draft, city: event.target.value })}
          />
          {/* Options only appear once the admin has typed something - an
              empty field must not pop the entire city list on click. */}
          <datalist id="reports-city-suggestions">
            {draft.city.trim()
              ? (citiesQuery.data ?? [])
                  .filter((city) => city.name.toLowerCase().includes(draft.city.trim().toLowerCase()))
                  .map((city) => <option key={city.id} value={city.name} />)
              : null}
          </datalist>
        </FilterBar>

        {rangeError ? <Alert>{rangeError}</Alert> : null}
        {exportError ? (
          <Alert title="The export could not be downloaded">{exportError}</Alert>
        ) : null}

        <ReportCard
          title="Booking & Revenue"
          description="Bookings created in the selected window and the revenue they represent (SRS 12.18.1, task 128a)."
          isLoading={bookingRevenueQuery.isPending}
          error={bookingRevenueQuery.error}
          onRetry={() => void bookingRevenueQuery.refetch()}
          skeleton={<StatGridSkeleton count={2} columns={2} />}
          exporting={runningExport === "booking-revenue"}
          onExport={() =>
            void runExport(
              "booking-revenue",
              () => exportBookingRevenueCsv(bookingRevenueFilters),
              "booking-revenue",
            )
          }
        >
          <StatGrid columns={2}>
            <StatTile
              tone="brand"
              label="Bookings"
              value={(bookingRevenueQuery.data?.totalBookingsCount ?? 0).toLocaleString("en-IN")}
            />
            <StatTile
              tone="success"
              label="Revenue"
              value={formatCurrency(bookingRevenueQuery.data?.totalRevenue ?? 0)}
              title={formatCurrency(bookingRevenueQuery.data?.totalRevenue ?? 0)}
            />
          </StatGrid>
        </ReportCard>

        <ReportCard
          title="Refunds"
          description="Refunds raised in the selected window and the total value returned (SRS 12.18.1, task 128b)."
          isLoading={refundQuery.isPending}
          error={refundQuery.error}
          onRetry={() => void refundQuery.refetch()}
          skeleton={
            <div className="flex flex-col gap-5">
              <StatGridSkeleton count={2} columns={2} />
              <TableSkeleton />
            </div>
          }
          exporting={runningExport === "refunds"}
          onExport={() => void runExport("refunds", () => exportRefundCsv(dateRangeFilters), "refunds")}
        >
          <div className="flex flex-col gap-5">
            <StatGrid columns={2}>
              <StatTile
                tone="danger"
                label="Refunds"
                value={(refundQuery.data?.totalCount ?? 0).toLocaleString("en-IN")}
              />
              <StatTile
                tone="warning"
                label="Total refunded"
                value={formatCurrency(refundQuery.data?.totalRefundedAmount ?? 0)}
                title={formatCurrency(refundQuery.data?.totalRefundedAmount ?? 0)}
              />
            </StatGrid>
            <DataTable
              columns={refundColumns}
              rows={refundQuery.data?.rows}
              rowKey={(row) => row.refundId}
              isFetching={refundQuery.isFetching}
              caption="Every refund raised in the selected window"
              defaultSort={{ key: "createdAt", direction: "desc" }}
              emptyTitle="No refund was raised in this window"
              emptyDescription="Widen the date range to see earlier refunds."
              maxHeight="24rem"
              minWidth="720px"
              hideDensityToggle
            />
          </div>
        </ReportCard>

        <ReportCard
          title="Coupon usage"
          description="Redemptions and discount given, in total and per coupon (SRS 12.18.1, task 128b)."
          isLoading={couponUsageQuery.isPending}
          error={couponUsageQuery.error}
          onRetry={() => void couponUsageQuery.refetch()}
          skeleton={
            <div className="flex flex-col gap-5">
              <StatGridSkeleton count={2} columns={2} />
              <TableSkeleton />
            </div>
          }
          exporting={runningExport === "coupon-usage"}
          onExport={() =>
            void runExport("coupon-usage", () => exportCouponUsageCsv(dateRangeFilters), "coupon-usage")
          }
        >
          <div className="flex flex-col gap-5">
            <StatGrid columns={2}>
              <StatTile
                tone="brand"
                label="Redemptions"
                value={(couponUsageQuery.data?.totalRedemptions ?? 0).toLocaleString("en-IN")}
              />
              <StatTile
                tone="accent"
                label="Total discount"
                value={formatCurrency(couponUsageQuery.data?.totalDiscountAmount ?? 0)}
                title={formatCurrency(couponUsageQuery.data?.totalDiscountAmount ?? 0)}
              />
            </StatGrid>
            <DataTable
              columns={couponColumns}
              rows={couponUsageQuery.data?.rows}
              rowKey={(row) => row.couponId}
              isFetching={couponUsageQuery.isFetching}
              caption="Redemptions per coupon in the selected window"
              defaultSort={{ key: "redemptions", direction: "desc" }}
              emptyTitle="No coupon was redeemed in this window"
              emptyDescription="Widen the date range to see earlier redemptions."
              maxHeight="24rem"
              minWidth="560px"
              hideDensityToggle
            />
          </div>
        </ReportCard>

        <ReportCard
          title="Customer segmentation"
          description="Customers who registered in the selected window, split by status and by city (SRS 12.18.1, task 128c)."
          isLoading={segmentationQuery.isPending}
          error={segmentationQuery.error}
          onRetry={() => void segmentationQuery.refetch()}
          skeleton={
            <div className="flex flex-col gap-5">
              <StatGridSkeleton count={1} columns={2} />
              <div className="grid grid-cols-1 gap-5 lg:grid-cols-2">
                <TableSkeleton />
                <TableSkeleton />
              </div>
            </div>
          }
          exporting={runningExport === "customer-segmentation"}
          onExport={() =>
            void runExport(
              "customer-segmentation",
              () => exportCustomerSegmentationCsv(segmentationFilters),
              "customer-segmentation",
            )
          }
        >
          <div className="flex flex-col gap-5">
            <StatGrid columns={2}>
              <StatTile
                tone="info"
                label="Customers registered"
                value={(segmentationQuery.data?.totalCustomers ?? 0).toLocaleString("en-IN")}
              />
            </StatGrid>
            <div className="grid grid-cols-1 gap-5 lg:grid-cols-2">
              <DataTable
                title="By status"
                columns={statusColumns}
                rows={segmentationQuery.data?.byStatus}
                rowKey={(row) => String(row.status)}
                isFetching={segmentationQuery.isFetching}
                caption="Registered customers by account status"
                defaultSort={{ key: "count", direction: "desc" }}
                emptyTitle="No customers in this window"
                hideDensityToggle
              />
              <DataTable
                title="By city"
                columns={cityColumns}
                rows={segmentationQuery.data?.byCity}
                rowKey={(row) => row.city || "__unset"}
                isFetching={segmentationQuery.isFetching}
                caption="Registered customers by city"
                defaultSort={{ key: "count", direction: "desc" }}
                emptyTitle="No customers in this window"
                maxHeight="22rem"
                hideDensityToggle
              />
            </div>
          </div>
        </ReportCard>

        <ReportCard
          title="Support tickets"
          description="Ticket volume, resolution rate and average time to resolve, with the category breakdown (SRS 12.18.1, task 128c)."
          isLoading={supportTicketQuery.isPending}
          error={supportTicketQuery.error}
          onRetry={() => void supportTicketQuery.refetch()}
          skeleton={
            <div className="flex flex-col gap-5">
              <StatGridSkeleton count={3} />
              <TableSkeleton rows={5} />
            </div>
          }
          exporting={runningExport === "support-tickets"}
          onExport={() =>
            void runExport(
              "support-tickets",
              () => exportSupportTicketCsv(dateRangeFilters),
              "support-tickets",
            )
          }
        >
          <div className="flex flex-col gap-5">
            <StatGrid>
              <StatTile
                tone="brand"
                label="Total tickets"
                value={(supportTicketQuery.data?.totalTickets ?? 0).toLocaleString("en-IN")}
              />
              <StatTile
                tone="success"
                label="Resolved or closed"
                value={(supportTicketQuery.data?.resolvedCount ?? 0).toLocaleString("en-IN")}
              />
              <StatTile
                tone="info"
                label="Avg. resolution time"
                value={
                  supportTicketQuery.data?.averageResolutionHours == null
                    ? "—"
                    : `${supportTicketQuery.data.averageResolutionHours.toFixed(1)}h`
                }
                hint="Across tickets resolved in this window."
              />
            </StatGrid>
            <DataTable
              columns={categoryColumns}
              rows={supportTicketQuery.data?.byCategory}
              rowKey={(row) => String(row.category)}
              isFetching={supportTicketQuery.isFetching}
              caption="Ticket volume by category"
              defaultSort={{ key: "count", direction: "desc" }}
              emptyTitle="No tickets in this window"
              maxHeight="22rem"
              minWidth="420px"
              hideDensityToggle
            />
          </div>
        </ReportCard>

        {canRequestAsyncExport ? (
          <ExportQueueCard fromUtc={fromUtc} toUtc={toUtc} city={applied.city} />
        ) : null}
      </div>
    </div>
  );
}
