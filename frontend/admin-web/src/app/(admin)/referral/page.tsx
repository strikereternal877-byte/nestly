"use client";

import { keepPreviousData, useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useEffect, useState } from "react";
import { DataTable, FilterBar, Pagination, countActiveFilters, formatDate } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { Button, Field, PageHeading, Select, Tabs } from "@/components/ui";
import { useResetOnChange } from "@/hooks/useResetOnChange";
import { ReferralStatusBadge, FraudFlagBadge } from "./_components/ReferralStatusBadge";
import { ReferralTabs } from "./_components/ReferralTabs";
import { listFraudQueue, searchReferrals } from "./_lib/referral-api";
import { REFERRAL_STATUS_LABELS, ReferralStatus } from "./_lib/referral-types";
import type { ReferralAdminListItem } from "./_lib/referral-types";

const PAGE_SIZE = 20;

const STATUS_FILTER_OPTIONS = [
  { value: "", label: "All statuses" },
  ...Object.entries(REFERRAL_STATUS_LABELS).map(([value, label]) => ({ value, label })),
];

interface ReferralFilters {
  customerSearch: string;
  status: string;
}

const EMPTY_FILTERS: ReferralFilters = { customerSearch: "", status: "" };

type ReferralTab = "all" | "fraud-queue";

/**
 * Referral list + fraud review queue (task 170). The detail view and its
 * flag/confirm/dismiss actions live on `/referral/[referralId]` rather than in
 * an inline panel, so a row stays middle-clickable and the destructive
 * "confirm fraud" decision gets a real `ConfirmDialog`.
 *
 * The fraud queue is a separate endpoint that ignores the status and customer
 * filters server-side, so the filter bar is hidden on that tab rather than
 * shown and silently ignored.
 */
export default function ReferralsPage() {
  const [tab, setTab] = useState<ReferralTab>("all");
  const [filters, setFilters] = useState<ReferralFilters>(EMPTY_FILTERS);
  const [page, setPage] = useState(1);

  // Live typeahead for Customer - reuses the same searchReferrals call the
  // list below uses, same pattern as bookings/page.tsx's Booking #
  // suggestions. Suggests both referrer and referee names since the field
  // searches across both.
  const [debouncedCustomerSearch, setDebouncedCustomerSearch] = useState("");
  useEffect(() => {
    const handle = window.setTimeout(
      () => setDebouncedCustomerSearch(filters.customerSearch.trim()),
      300,
    );
    return () => window.clearTimeout(handle);
  }, [filters.customerSearch]);

  const customerSuggestionsQuery = useQuery({
    queryKey: ["referrals", "customer-suggestions", debouncedCustomerSearch] as const,
    queryFn: () => searchReferrals({ customerSearch: debouncedCustomerSearch, page: 1, pageSize: 8 }),
    enabled: debouncedCustomerSearch.length >= 2,
    placeholderData: keepPreviousData,
  });

  // Live filtering (no Search button): Customer reuses the debounce it
  // already computes for its typeahead suggestions above; Status (a
  // dropdown) applies immediately. Any change resets to page 1, same as
  // customers/page.tsx.
  useResetOnChange([tab, debouncedCustomerSearch, filters.status], () => {
    if (tab === "all") setPage(1);
  });

  const referralsQuery = useQuery({
    queryKey: ["referrals", "search", tab, debouncedCustomerSearch, filters.status, page] as const,
    queryFn: () =>
      tab === "fraud-queue"
        ? listFraudQueue({ page, pageSize: PAGE_SIZE })
        : searchReferrals({
            customerSearch: debouncedCustomerSearch || undefined,
            status: filters.status === "" ? undefined : (Number(filters.status) as ReferralStatus),
            page,
            pageSize: PAGE_SIZE,
          }),
    // Paging used to blank the table between requests; keeping the previous
    // page rendered (dimmed by DataTable's `isFetching`) is far less jarring.
    placeholderData: keepPreviousData,
  });

  const clearFilters = () => {
    setFilters(EMPTY_FILTERS);
    setDebouncedCustomerSearch("");
    setPage(1);
  };

  const switchTab = (next: ReferralTab) => {
    setTab(next);
    // Page 3 of "all referrals" is rarely page 3 of the fraud queue.
    setPage(1);
  };

  const activeFilterCount = tab === "all" ? countActiveFilters(filters) : 0;

  const columns: DataTableColumn<ReferralAdminListItem>[] = [
    {
      key: "referrer",
      header: "Referrer",
      cell: (referral) => <span className="font-medium text-fg">{referral.referrerName}</span>,
    },
    { key: "referee", header: "Referee", cell: (referral) => referral.refereeName },
    {
      key: "status",
      header: "Status",
      cell: (referral) => <ReferralStatusBadge status={referral.status} />,
    },
    {
      key: "flagged",
      header: "Fraud",
      cell: (referral) => <FraudFlagBadge flagged={referral.isFraudFlagged} />,
    },
    {
      key: "registered",
      header: "Registered",
      cell: (referral) => <span className="nums whitespace-nowrap">{formatDate(referral.registeredAtUtc)}</span>,
    },
    {
      key: "rewarded",
      header: "Rewarded",
      cell: (referral) =>
        referral.rewardedAtUtc ? (
          <span className="nums whitespace-nowrap">{formatDate(referral.rewardedAtUtc)}</span>
        ) : (
          <span className="text-fg-subtle">—</span>
        ),
    },
  ];

  return (
    <div className="w-full max-w-7xl">
      <PageHeading
        title="Referrals"
        subtitle="Every referral, its reward progress, and the queue of referrals flagged for fraud review."
      />

      <ReferralTabs />

      <div className="flex flex-col gap-6">
        <Tabs
          label="Referral list"
          value={tab}
          onChange={switchTab}
          tabs={[
            { value: "all", label: "All referrals" },
            { value: "fraud-queue", label: "Fraud queue" },
          ]}
        />

        {tab === "all" ? (
          <FilterBar
            columns={2}
            onClear={clearFilters}
            activeCount={activeFilterCount}
            busy={referralsQuery.isFetching}
          >
            <Field
              label="Customer"
              name="customerSearch"
              autoComplete="name"
              list="referral-customer-suggestions"
              placeholder="Referrer or referee name…"
              value={filters.customerSearch}
              onChange={(event) =>
                setFilters((current) => ({ ...current, customerSearch: event.target.value }))
              }
            />
            <datalist id="referral-customer-suggestions">
              {(customerSuggestionsQuery.data?.items ?? []).flatMap((referral) => [
                <option key={`${referral.id}-referrer`} value={referral.referrerName} />,
                <option key={`${referral.id}-referee`} value={referral.refereeName} />,
              ])}
            </datalist>
            <Select
              label="Status"
              options={STATUS_FILTER_OPTIONS}
              value={filters.status}
              onChange={(event) => setFilters((current) => ({ ...current, status: event.target.value }))}
            />
          </FilterBar>
        ) : null}

        <DataTable
          title={tab === "fraud-queue" ? "Fraud review queue" : "Referrals"}
          description={
            tab === "fraud-queue"
              ? "Referrals currently flagged for review. Open one to confirm the abuse or dismiss the flag."
              : "Search by customer or narrow to a single reward stage."
          }
          columns={columns}
          rows={referralsQuery.data?.items}
          rowKey={(referral) => referral.id}
          isLoading={referralsQuery.isPending}
          isFetching={referralsQuery.isFetching}
          error={referralsQuery.error}
          onRetry={() => referralsQuery.refetch()}
          caption={tab === "fraud-queue" ? "Referrals flagged for fraud review" : "Referrals matching the current filters"}
          emptyTitle={
            tab === "fraud-queue"
              ? "No referrals are flagged for review"
              : activeFilterCount > 0
                ? "No referrals match the current filters"
                : "No referrals yet"
          }
          emptyDescription={
            tab === "fraud-queue"
              ? "Flagging happens automatically on suspicious sign-ups, or manually from a referral's detail screen."
              : activeFilterCount > 0
                ? "Clear the filters to see every referral."
                : "Referrals appear here as customers sign up with a referral code."
          }
          emptyAction={
            activeFilterCount > 0 ? (
              <Button variant="secondary" onClick={clearFilters}>
                Clear filters
              </Button>
            ) : undefined
          }
          skeletonRows={8}
          minWidth="860px"
          rowActions={(referral) => (
            // A real link, not a button that opens a panel: the detail is a
            // destination and must stay middle-clickable.
            <Link
              href={`/referral/${referral.id}`}
              aria-label={`Referral from ${referral.referrerName} to ${referral.refereeName}`}
              className="inline-flex h-8 items-center rounded-lg px-3 text-xs font-medium text-fg-muted transition-colors duration-fast ease-out hover:bg-surface-3 hover:text-fg"
            >
              View
            </Link>
          )}
          footer={
            referralsQuery.data ? (
              <Pagination
                page={page}
                pageSize={PAGE_SIZE}
                totalCount={referralsQuery.data.totalCount}
                onPageChange={setPage}
                busy={referralsQuery.isFetching}
                itemLabel="referral"
              />
            ) : null
          }
        />
      </div>
    </div>
  );
}
