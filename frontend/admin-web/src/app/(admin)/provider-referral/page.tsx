"use client";

import { keepPreviousData, useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useEffect, useState } from "react";
import { DataTable, FilterBar, Pagination, countActiveFilters, formatDate } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { Button, Field, PageHeading, Select, Tabs } from "@/components/ui";
import { useResetOnChange } from "@/hooks/useResetOnChange";
import { ProviderFraudFlagBadge, ProviderReferralStatusBadge } from "./_components/ProviderReferralStatusBadge";
import { ProviderReferralTabs } from "./_components/ProviderReferralTabs";
import { listProviderReferralFraudQueue, searchProviderReferrals } from "./_lib/provider-referral-api";
import { PROVIDER_REFERRAL_STATUS_LABELS, ProviderReferralStatus } from "./_lib/provider-referral-types";
import type { ProviderReferralAdminListItem } from "./_lib/provider-referral-types";

const PAGE_SIZE = 20;

const STATUS_FILTER_OPTIONS = [
  { value: "", label: "All statuses" },
  ...Object.entries(PROVIDER_REFERRAL_STATUS_LABELS).map(([value, label]) => ({ value, label })),
];

interface ProviderReferralFilters {
  providerSearch: string;
  status: string;
}

const EMPTY_FILTERS: ProviderReferralFilters = { providerSearch: "", status: "" };

type ProviderReferralTab = "all" | "fraud-queue";

/**
 * Provider referral list + fraud review queue (PROVIDER-REFERRAL.md), mirrors
 * (admin)/referral/page.tsx. The detail view and its flag/confirm/dismiss
 * actions live on `/provider-referral/[referralId]` rather than in an inline
 * panel, same reasoning as the customer-side screen.
 */
export default function ProviderReferralsPage() {
  const [tab, setTab] = useState<ProviderReferralTab>("all");
  const [filters, setFilters] = useState<ProviderReferralFilters>(EMPTY_FILTERS);
  const [page, setPage] = useState(1);

  // Live filtering (no Search button): Provider is a free-text field,
  // debounced 300ms before it hits the query - shared with the typeahead
  // suggestions below rather than debouncing twice. Status (a dropdown)
  // applies immediately.
  const [debouncedProviderSearch, setDebouncedProviderSearch] = useState("");
  useEffect(() => {
    const handle = window.setTimeout(
      () => setDebouncedProviderSearch(filters.providerSearch.trim()),
      300,
    );
    return () => window.clearTimeout(handle);
  }, [filters.providerSearch]);

  // Any filter change resets to page 1 - staying on a now out-of-range page
  // would just show an empty result (same pattern as customers/page.tsx).
  useResetOnChange([debouncedProviderSearch, filters.status], () => setPage(1));

  const providerSuggestionsQuery = useQuery({
    queryKey: ["provider-referrals", "provider-suggestions", debouncedProviderSearch] as const,
    queryFn: () =>
      searchProviderReferrals({ providerSearch: debouncedProviderSearch, page: 1, pageSize: 8 }),
    enabled: debouncedProviderSearch.length >= 2,
    placeholderData: keepPreviousData,
  });

  const referralsQuery = useQuery({
    queryKey: ["provider-referrals", "search", tab, debouncedProviderSearch, filters.status, page] as const,
    queryFn: () =>
      tab === "fraud-queue"
        ? listProviderReferralFraudQueue({ page, pageSize: PAGE_SIZE })
        : searchProviderReferrals({
            providerSearch: debouncedProviderSearch || undefined,
            status:
              filters.status === "" ? undefined : (Number(filters.status) as ProviderReferralStatus),
            page,
            pageSize: PAGE_SIZE,
          }),
    placeholderData: keepPreviousData,
  });

  const clearFilters = () => {
    setFilters(EMPTY_FILTERS);
    setDebouncedProviderSearch("");
    setPage(1);
  };

  const switchTab = (next: ProviderReferralTab) => {
    setTab(next);
    setPage(1);
  };

  const activeFilterCount = tab === "all" ? countActiveFilters(filters) : 0;

  const columns: DataTableColumn<ProviderReferralAdminListItem>[] = [
    {
      key: "referrer",
      header: "Referrer",
      cell: (referral) => <span className="font-medium text-fg">{referral.referrerName}</span>,
    },
    { key: "referee", header: "Referee", cell: (referral) => referral.refereeName },
    {
      key: "status",
      header: "Status",
      cell: (referral) => <ProviderReferralStatusBadge status={referral.status} />,
    },
    {
      key: "flagged",
      header: "Fraud",
      cell: (referral) => <ProviderFraudFlagBadge flagged={referral.isFraudFlagged} />,
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
        title="Provider referrals"
        subtitle="Every provider referral, its reward progress, and the queue flagged for fraud review."
      />

      <ProviderReferralTabs />

      <div className="flex flex-col gap-6">
        <Tabs
          label="Provider referral list"
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
              label="Provider"
              name="providerSearch"
              autoComplete="name"
              list="provider-referral-provider-suggestions"
              placeholder="Referrer or referee name…"
              value={filters.providerSearch}
              onChange={(event) =>
                setFilters((current) => ({ ...current, providerSearch: event.target.value }))
              }
            />
            <datalist id="provider-referral-provider-suggestions">
              {(providerSuggestionsQuery.data?.items ?? []).flatMap((referral) => [
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
          title={tab === "fraud-queue" ? "Fraud review queue" : "Provider referrals"}
          description={
            tab === "fraud-queue"
              ? "Referrals currently flagged for review. Open one to confirm the abuse or dismiss the flag."
              : "Search by provider or narrow to a single reward stage."
          }
          columns={columns}
          rows={referralsQuery.data?.items}
          rowKey={(referral) => referral.id}
          isLoading={referralsQuery.isPending}
          isFetching={referralsQuery.isFetching}
          error={referralsQuery.error}
          onRetry={() => referralsQuery.refetch()}
          caption={tab === "fraud-queue" ? "Provider referrals flagged for fraud review" : "Provider referrals matching the current filters"}
          emptyTitle={
            tab === "fraud-queue"
              ? "No provider referrals are flagged for review"
              : activeFilterCount > 0
                ? "No provider referrals match the current filters"
                : "No provider referrals yet"
          }
          emptyDescription={
            tab === "fraud-queue"
              ? "Flag a suspicious referral from its detail screen."
              : activeFilterCount > 0
                ? "Clear the filters to see every referral."
                : "Referrals appear here as providers register with another provider's code."
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
            <Link
              href={`/provider-referral/${referral.id}`}
              aria-label={`Provider referral from ${referral.referrerName} to ${referral.refereeName}`}
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
