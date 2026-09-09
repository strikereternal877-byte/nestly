"use client";

import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { motion } from "motion/react";
import Link from "next/link";
import { useState } from "react";
import type { ChangeEvent } from "react";
import { Reveal, revealItem } from "@/components/motion";
import { Alert, Avatar, Button, Card, DonutChart, Field, KpiCard, PageHeading, Skeleton, cx } from "@/components/ui";
import type { ChartTone } from "@/components/ui";
import {
  Breadcrumbs,
  DataTable,
  FilterBar,
  countActiveFilters,
  formatCurrency,
} from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { BookingStatusBadge, bookingStatusTone } from "@/components/status-badges";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import { searchBookings } from "@/lib/bookings-api";
import type { AdminBookingListItem } from "@/lib/bookings-types";
import { listCategories } from "@/lib/catalog-api";
import { getVisibleNavModules } from "@/lib/permissions";
import { listCities } from "@/lib/serviceability-api";
import { useAdminClaims } from "@/lib/use-admin-claims";
import type { DashboardKpiFilters, DashboardKpiResponse } from "@/lib/types";
import { toLocalIsoDate } from "@/lib/date";

const isoDate = toLocalIsoDate;

const RECENT_BOOKINGS_PAGE_SIZE = 8;

interface DatePreset {
  key: string;
  label: string;
  range: () => { dateFrom: string; dateTo: string };
}

/**
 * Date-range presets (SRS 12.3.1's "Bookings today/this week/this month" plus
 * the interaction convention of offering presets before a custom range).
 * "Last 7/30 days" rather than calendar week/month: a rolling window needs no
 * day-of-week/month-boundary logic and is the more common preset shape.
 */
const PRESETS: DatePreset[] = [
  {
    key: "today",
    label: "Today",
    range: () => {
      const today = isoDate(new Date());
      return { dateFrom: today, dateTo: today };
    },
  },
  {
    key: "last7",
    label: "Last 7 days",
    range: () => {
      const to = new Date();
      const from = new Date(to);
      from.setDate(from.getDate() - 6);
      return { dateFrom: isoDate(from), dateTo: isoDate(to) };
    },
  },
  {
    key: "last30",
    label: "Last 30 days",
    range: () => {
      const to = new Date();
      const from = new Date(to);
      from.setDate(from.getDate() - 29);
      return { dateFrom: isoDate(from), dateTo: isoDate(to) };
    },
  },
];

function buildQueryString(filters: DashboardKpiFilters): string {
  const params = new URLSearchParams();
  if (filters.dateFrom) params.set("dateFrom", filters.dateFrom);
  if (filters.dateTo) params.set("dateTo", filters.dateTo);
  if (filters.city) params.set("city", filters.city);
  if (filters.category) params.set("category", filters.category);
  const queryString = params.toString();
  return queryString ? `?${queryString}` : "";
}

/**
 * Dashboard KPI widgets (SRS 12.3, task 100): bookings, revenue,
 * cancellations, refunds, and open support tickets, scoped by the date
 * range/city/category filters above them - backed by the admin API's
 * dashboard endpoint (task 99, `IDashboardQueryService`).
 *
 * Filters sit in one row above the widgets and scope all of them at once
 * (never a per-widget filter), and a refetch keeps the previous numbers on
 * screen at reduced opacity rather than flashing a loading skeleton -
 * `keepPreviousData` is exactly this behaviour for react-query v5.
 *
 * This screen is also the reference implementation for task 221's shared
 * pattern (`components/data-table.tsx`): `FilterBar` above, a three-state KPI
 * row, and a `DataTable` whose columns, sort, density, skeleton, empty state
 * and error-with-retry are all the table primitive's rather than hand-rolled.
 * Every other module screen is expected to look like this one.
 */
export default function DashboardPage() {
  const todayIso = isoDate(new Date());
  const claims = useAdminClaims();

  // The recent-bookings panel is a second read of an existing endpoint, so it
  // is only offered to an admin whose session actually grants the bookings
  // module — otherwise the dashboard would show a permanent 403 card.
  const canReadBookings = getVisibleNavModules(claims).some((module) => module.key === "bookings");

  const [filters, setFilters] = useState<DashboardKpiFilters>({ dateFrom: todayIso, dateTo: todayIso });
  const [activePreset, setActivePreset] = useState<string | null>("today");
  const [draftFrom, setDraftFrom] = useState(todayIso);
  const [draftTo, setDraftTo] = useState(todayIso);
  const [draftCity, setDraftCity] = useState("");
  const [draftCategory, setDraftCategory] = useState("");

  // Real suggestion lists for the City/Category filters - both stay plain
  // text inputs (never converted to a dropdown), but the server matches them
  // as an exact case-insensitive equality (DashboardQueryService.GetKpisAsync:
  // city against AddressCitySnapshot, category against Category.Slug), so a
  // <datalist> of the real values guards against a typo silently matching
  // nothing while still letting the admin type freely.
  const citiesQuery = useQuery({
    queryKey: ["serviceability-cities"],
    queryFn: () => listCities(),
    staleTime: 5 * 60 * 1000,
  });

  const categoriesQuery = useQuery({
    queryKey: ["catalog-categories"],
    queryFn: listCategories,
    staleTime: 5 * 60 * 1000,
  });

  const query = useQuery({
    queryKey: ["dashboard-kpis", filters],
    queryFn: () =>
      apiFetch<DashboardKpiResponse>(`${API_V1}/dashboard/kpis${buildQueryString(filters)}`, {
        authenticated: true,
      }),
    placeholderData: keepPreviousData,
  });

  // The tiles count bookings by CreatedAtUtc across the selected days
  // (DashboardQueryService.GetKpisAsync), treating dateFrom as UTC midnight
  // and dateTo as UTC end-of-day. The list below has to ask the same
  // question: it used to filter on slotDate, so any booking whose slot fell
  // outside the window it was created in counted in the tile and vanished
  // from the list - "Bookings 6" above "Showing ... of 1". The booking search
  // compares CreatedAtUtc with the same inclusive bounds
  // (BookingRepository.SearchAsync), so these line up exactly.
  const createdFromUtc = filters.dateFrom ? `${filters.dateFrom}T00:00:00.000Z` : undefined;
  const createdToUtc = filters.dateTo ? `${filters.dateTo}T23:59:59.999Z` : undefined;

  // The KPI endpoint matches category by slug, the booking search by id, so
  // the typed slug is resolved through the same list that backs the filter's
  // suggestions. Previously the category filter was simply not passed here,
  // leaving the list unfiltered while the tiles honoured it.
  const categoryFilter = filters.category?.trim().toLowerCase();
  const categoryId = categoryFilter
    ? categoriesQuery.data?.find((category) => category.slug.toLowerCase() === categoryFilter)?.id
    : undefined;

  const recentBookings = useQuery({
    queryKey: ["dashboard-recent-bookings", createdFromUtc, createdToUtc, filters.city, categoryId],
    queryFn: () =>
      searchBookings({
        createdFromUtc,
        createdToUtc,
        city: filters.city,
        categoryId,
        page: 1,
        pageSize: RECENT_BOOKINGS_PAGE_SIZE,
      }),
    // Waiting for the slug to resolve keeps the list from flashing an
    // unfiltered page while the category list is still loading.
    enabled: canReadBookings && (!categoryFilter || categoryId !== undefined),
    placeholderData: keepPreviousData,
  });

  // Status mix for the donut below - derived from the same rows the table
  // renders (never a separate fetch, never invented counts): whatever page
  // of "recent bookings" is on screen is what the chart summarizes.
  const statusBreakdown = (() => {
    const toChartTone = (status: AdminBookingListItem["status"]): ChartTone => {
      const tone = bookingStatusTone(status);
      // ChartTone has no "neutral" swatch (dashboard charts are never gray) -
      // the rare Initiated/Refunded/Expired rows fall back to "info".
      return tone === "neutral" ? "info" : tone;
    };
    const counts = new Map<string, { count: number; tone: ChartTone }>();
    for (const booking of recentBookings.data?.items ?? []) {
      const existing = counts.get(booking.statusLabel);
      if (existing) {
        existing.count += 1;
      } else {
        counts.set(booking.statusLabel, { count: 1, tone: toChartTone(booking.status) });
      }
    }
    return Array.from(counts, ([label, { count, tone }]) => ({ label, value: count, tone }));
  })();

  const applyPreset = (preset: DatePreset) => {
    const range = preset.range();
    setDraftFrom(range.dateFrom);
    setDraftTo(range.dateTo);
    setActivePreset(preset.key);
    setFilters((current) => ({ ...current, ...range }));
  };

  const onDraftDateChange = (setter: (value: string) => void) => (event: ChangeEvent<HTMLInputElement>) => {
    setter(event.target.value);
    setActivePreset(null);
  };

  const applyCustomFilters = () => {
    setActivePreset(null);
    setFilters({
      dateFrom: draftFrom || undefined,
      dateTo: draftTo || undefined,
      city: draftCity.trim() || undefined,
      category: draftCategory.trim() || undefined,
    });
  };

  const clearFilters = () => {
    setDraftFrom(todayIso);
    setDraftTo(todayIso);
    setDraftCity("");
    setDraftCategory("");
    setActivePreset("today");
    setFilters({ dateFrom: todayIso, dateTo: todayIso });
  };

  const columns: DataTableColumn<AdminBookingListItem>[] = [
    {
      key: "customer",
      header: "Customer",
      sortValue: (booking) => booking.customerName,
      cell: (booking) => (
        <div className="flex items-center gap-3">
          <Avatar name={booking.customerName} size="sm" />
          <div className="min-w-0">
            <Link
              href={`/bookings/${booking.id}`}
              className="font-medium text-fg underline-offset-4 hover:text-brand-600 hover:underline dark:hover:text-brand-400"
            >
              {booking.customerName}
            </Link>
            <div className="nums text-xs text-fg-subtle">{booking.customerMobile}</div>
          </div>
        </div>
      ),
    },
    {
      key: "service",
      header: "Service",
      sortValue: (booking) => booking.serviceName,
      cell: (booking) => booking.serviceName,
    },
    {
      key: "city",
      header: "City",
      sortValue: (booking) => booking.city,
      cell: (booking) => booking.city,
    },
    {
      key: "slotDate",
      header: "Slot date",
      sortValue: (booking) => booking.slotDate,
      cell: (booking) => <span className="nums">{booking.slotDate}</span>,
    },
    {
      key: "status",
      header: "Status",
      sortValue: (booking) => booking.statusLabel,
      cell: (booking) => <BookingStatusBadge status={booking.status} label={booking.statusLabel} />,
    },
    {
      key: "total",
      header: "Total",
      numeric: true,
      sortValue: (booking) => booking.totalPayable,
      cell: (booking) => formatCurrency(booking.totalPayable),
    },
  ];

  return (
    <div className="w-full max-w-7xl">
      <PageHeading
        title="Dashboard"
        subtitle="Bookings, revenue, cancellations, refunds, and support activity."
        breadcrumbs={<Breadcrumbs items={[{ label: "Home", href: "/dashboard" }, { label: "Dashboard" }]} />}
      />

      <FilterBar
        onSubmit={applyCustomFilters}
        onClear={clearFilters}
        activeCount={countActiveFilters(filters)}
        submitLabel="Apply filters"
        busy={query.isFetching}
        columns={4}
        actions={
          <div className="flex flex-wrap gap-2" role="group" aria-label="Date range presets">
            {PRESETS.map((preset) => (
              <Button
                key={preset.key}
                type="button"
                size="sm"
                variant={activePreset === preset.key ? "subtle" : "ghost"}
                onClick={() => applyPreset(preset)}
              >
                {preset.label}
              </Button>
            ))}
          </div>
        }
      >
        <Field label="From" type="date" value={draftFrom} onChange={onDraftDateChange(setDraftFrom)} />
        <Field label="To" type="date" value={draftTo} onChange={onDraftDateChange(setDraftTo)} />
        <Field
          label="City"
          name="city"
          autoComplete="address-level2"
          list="dashboard-city-suggestions"
          placeholder="e.g. Bengaluru"
          value={draftCity}
          onChange={(event) => setDraftCity(event.target.value)}
        />
        {/* Options only appear once the admin has typed something - an empty
            field must not pop the entire city list on click. */}
        <datalist id="dashboard-city-suggestions">
          {draftCity.trim()
            ? (citiesQuery.data ?? [])
                .filter((city) => city.name.toLowerCase().includes(draftCity.trim().toLowerCase()))
                .map((city) => <option key={city.id} value={city.name} />)
            : null}
        </datalist>
        <Field
          label="Category"
          name="category"
          autoComplete="on"
          list="dashboard-category-suggestions"
          placeholder="e.g. Home Cleaning"
          value={draftCategory}
          onChange={(event) => setDraftCategory(event.target.value)}
        />
        {/* value is the real Category.Slug the backend matches on; label
            shows the human-readable name so the admin doesn't have to know
            or type the slug directly. */}
        <datalist id="dashboard-category-suggestions">
          {(categoriesQuery.data ?? []).map((category) => (
            <option key={category.id} value={category.slug} label={category.name} />
          ))}
        </datalist>
      </FilterBar>

      <section className="mt-6" aria-label="Key metrics">
        {query.isPending ? (
          <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-5">
            {Array.from({ length: 5 }, (_, index) => (
              <div key={index} className="rounded-2xl bg-surface p-5 shadow-sm">
                <Skeleton className="h-4 w-20" />
                <Skeleton className="mt-3 h-8 w-24" />
              </div>
            ))}
          </div>
        ) : query.isError ? (
          <Alert
            tone="error"
            title="Could not load the dashboard"
            action={
              <Button size="sm" variant="secondary" onClick={() => query.refetch()}>
                Retry
              </Button>
            }
          >
            {describeError(query.error)}
          </Alert>
        ) : (
          <>
            <p className="mb-3 text-sm text-fg-muted">
              Showing <span className="nums">{query.data.dateFrom}</span> to{" "}
              <span className="nums">{query.data.dateTo}</span>
              {query.isFetching ? " · refreshing…" : ""}
            </p>
            <Reveal
              className={cx(
                "grid grid-cols-2 gap-4 transition-opacity duration-fast ease-out sm:grid-cols-3 lg:grid-cols-5",
                query.isFetching && "opacity-60",
              )}
            >
              <motion.div variants={revealItem}>
                <KpiCard
                  icon={<CalendarIcon />}
                  tone="brand"
                  label="Bookings"
                  value={query.data.bookingsCount.toLocaleString("en-IN")}
                />
              </motion.div>
              <motion.div variants={revealItem}>
                <KpiCard
                  icon={<RupeeIcon />}
                  tone="success"
                  label="Revenue"
                  value={formatCurrency(query.data.revenueTotal)}
                />
              </motion.div>
              <motion.div variants={revealItem}>
                <KpiCard
                  icon={<CrossCircleIcon />}
                  tone="danger"
                  label="Cancellations"
                  value={query.data.cancellationsCount.toLocaleString("en-IN")}
                />
              </motion.div>
              <motion.div variants={revealItem}>
                <KpiCard
                  icon={<ReturnIcon />}
                  tone="warning"
                  label="Refund amount"
                  value={formatCurrency(query.data.refundAmountTotal)}
                />
              </motion.div>
              <motion.div variants={revealItem}>
                <KpiCard
                  icon={<HeadsetIcon />}
                  tone="info"
                  label="Open support tickets"
                  value={query.data.openSupportTicketsCount.toLocaleString("en-IN")}
                />
              </motion.div>
            </Reveal>
          </>
        )}
      </section>

      {canReadBookings && statusBreakdown.length > 0 ? (
        <div className="mt-6">
          <Card title="Bookings by status" description="Status mix of the bookings listed below.">
            <DonutChart data={statusBreakdown} />
          </Card>
        </div>
      ) : null}

      {canReadBookings ? (
        <div className="mt-6">
          <DataTable
            title="Bookings in this window"
            description="The most recent bookings matching the filters above."
            columns={columns}
            rows={recentBookings.data?.items}
            rowKey={(booking) => booking.id}
            isLoading={recentBookings.isPending}
            isFetching={recentBookings.isFetching}
            error={recentBookings.error}
            onRetry={() => recentBookings.refetch()}
            skeletonRows={5}
            minWidth="820px"
            caption="Recent bookings in the selected window"
            emptyTitle="No bookings in this window"
            emptyDescription="Widen the date range or clear the city filter to see more."
            emptyAction={
              <Button variant="secondary" onClick={clearFilters}>
                Reset filters
              </Button>
            }
            footer={
              <div className="flex items-center justify-between gap-3">
                <span className="text-sm text-fg-muted">
                  Showing <span className="nums">{recentBookings.data?.items.length ?? 0}</span> of{" "}
                  <span className="nums font-medium text-fg">
                    {(recentBookings.data?.totalCount ?? 0).toLocaleString("en-IN")}
                  </span>
                </span>
                <Link
                  href="/bookings"
                  className="text-sm font-medium text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
                >
                  View all bookings
                </Link>
              </div>
            }
          />
        </div>
      ) : null}
    </div>
  );
}

const ICON_PROPS = {
  viewBox: "0 0 24 24",
  fill: "none",
  stroke: "currentColor",
  strokeWidth: "1.75",
  strokeLinecap: "round" as const,
  strokeLinejoin: "round" as const,
  className: "h-5 w-5",
  "aria-hidden": true,
};

function CalendarIcon() {
  return (
    <svg {...ICON_PROPS}>
      <rect x="3.5" y="4.5" width="17" height="16" rx="2" />
      <path d="M3.5 9.5h17M8 2.5v4M16 2.5v4" />
    </svg>
  );
}

function RupeeIcon() {
  return (
    <svg {...ICON_PROPS}>
      <path d="M6 4.5h12M6 9.5h12M6 4.5c4 0 6.5 1.5 6.5 4.5S10 13.5 6 13.5h1l7 6.5" />
    </svg>
  );
}

function CrossCircleIcon() {
  return (
    <svg {...ICON_PROPS}>
      <circle cx="12" cy="12" r="8.5" />
      <path d="m9.2 9.2 5.6 5.6M14.8 9.2l-5.6 5.6" />
    </svg>
  );
}

function ReturnIcon() {
  return (
    <svg {...ICON_PROPS}>
      <path d="M9 6 4.5 10.5 9 15" />
      <path d="M4.5 10.5H14a5.5 5.5 0 0 1 5.5 5.5v2" />
    </svg>
  );
}

function HeadsetIcon() {
  return (
    <svg {...ICON_PROPS}>
      <path d="M4 13v-1a8 8 0 0 1 16 0v1" />
      <rect x="3" y="13" width="4.5" height="6" rx="1.5" />
      <rect x="16.5" y="13" width="4.5" height="6" rx="1.5" />
      <path d="M18.5 19v.5a3 3 0 0 1-3 3h-2.4" />
    </svg>
  );
}
