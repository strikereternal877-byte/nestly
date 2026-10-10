"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useEffect, useState } from "react";
import { Alert, Badge, Button, Field, Modal, PageHeading, Select } from "@/components/ui";
import {
  DataTable,
  ExportCsvButton,
  FilterBar,
  FormGrid,
  Pagination,
  countActiveFilters,
  exportRowsToCsv,
  formatDate,
} from "@/components/data-table";
import type { CsvColumn, DataTableColumn } from "@/components/data-table";
import { todayIsoDate } from "@/lib/date";
import { endOfLocalDayUtc, startOfLocalDayUtc } from "@/lib/day-range";
import { ProviderStatusBadge } from "@/components/status-badges";
import { useResetOnChange } from "@/hooks/useResetOnChange";
import { describeError } from "@/lib/api";
import { createProvider, searchProviders } from "@/lib/providers-api";
import { ProviderOnboardingStatus, ProviderStatus } from "@/lib/providers-types";
import type { CreateProviderRequest, ProviderSummary } from "@/lib/providers-types";
import { listCities } from "@/lib/serviceability-api";
import { useAdminClaims } from "@/lib/use-admin-claims";
import { ProvidersTabs } from "../_components/ProvidersTabs";

const PAGE_SIZE = 20;

const STATUS_OPTIONS: { value: string; label: string }[] = [
  { value: "", label: "Any status" },
  { value: String(ProviderStatus.PendingVerification), label: "Pending verification" },
  { value: String(ProviderStatus.Active), label: "Active" },
  { value: String(ProviderStatus.Suspended), label: "Suspended" },
  { value: String(ProviderStatus.Deactivated), label: "Deactivated" },
];

const ONBOARDING_LABELS: Record<ProviderOnboardingStatus, string> = {
  [ProviderOnboardingStatus.Registered]: "Registered",
  [ProviderOnboardingStatus.ProfileCompleted]: "Profile completed",
  [ProviderOnboardingStatus.KycSubmitted]: "KYC submitted",
  [ProviderOnboardingStatus.KycVerified]: "KYC verified",
  [ProviderOnboardingStatus.Completed]: "Onboarding complete",
};

const ONBOARDING_OPTIONS: { value: string; label: string }[] = [
  { value: "", label: "Any stage" },
  { value: String(ProviderOnboardingStatus.Registered), label: ONBOARDING_LABELS[ProviderOnboardingStatus.Registered] },
  { value: String(ProviderOnboardingStatus.ProfileCompleted), label: ONBOARDING_LABELS[ProviderOnboardingStatus.ProfileCompleted] },
  { value: String(ProviderOnboardingStatus.KycSubmitted), label: ONBOARDING_LABELS[ProviderOnboardingStatus.KycSubmitted] },
  { value: String(ProviderOnboardingStatus.KycVerified), label: ONBOARDING_LABELS[ProviderOnboardingStatus.KycVerified] },
  { value: String(ProviderOnboardingStatus.Completed), label: ONBOARDING_LABELS[ProviderOnboardingStatus.Completed] },
];

function statusLabel(status: ProviderStatus): string {
  return STATUS_OPTIONS.find((o) => o.value === String(status))?.label ?? "Unknown";
}

interface FilterFormState {
  name: string;
  phone: string;
  status: string;
  onboardingStatus: string;
  cityId: string;
  /** Local calendar dates (`YYYY-MM-DD`) - converted to UTC instants for the API at the point of use, same split as dashboard/page.tsx's own date filters. */
  createdFrom: string;
  createdTo: string;
}

const EMPTY_FILTERS: FilterFormState = {
  name: "",
  phone: "",
  status: "",
  onboardingStatus: "",
  cityId: "",
  createdFrom: "",
  createdTo: "",
};
const EMPTY_CREATE: CreateProviderRequest = { legalName: "", displayName: "", phone: "", email: "" };

/**
 * Provider Onboarding Overview dashboard's click-through target: a tile
 * links here with `status`/`onboardingStatus`/`createdFrom`/`createdTo`
 * query params (see that page's own doc comment) - this reads them as the
 * initial filter set so the list opens already scoped to the cohort the
 * tile summarized, rather than requiring the admin to re-enter the same
 * filters by hand.
 */
function filtersFromSearchParams(params: URLSearchParams): FilterFormState {
  return {
    name: params.get("name") ?? "",
    phone: params.get("phone") ?? "",
    status: params.get("status") ?? "",
    onboardingStatus: params.get("onboardingStatus") ?? "",
    cityId: params.get("cityId") ?? "",
    createdFrom: params.get("createdFrom") ?? "",
    createdTo: params.get("createdTo") ?? "",
  };
}

const PROVIDER_CSV_COLUMNS: readonly CsvColumn<ProviderSummary>[] = [
  { header: "Name", value: (provider) => provider.displayName },
  { header: "Phone", value: (provider) => provider.phone },
  { header: "Email", value: (provider) => provider.email ?? "" },
  { header: "Serves", value: (provider) => provider.serviceCities.join("; ") },
  { header: "Status", value: (provider) => statusLabel(provider.status) },
  { header: "Onboarding", value: (provider) => ONBOARDING_LABELS[provider.onboardingStatus] },
  { header: "Created", value: (provider) => provider.createdAt },
];

/**
 * Admin provider directory: search/list plus admin-created provider records
 * (PROVIDER.md API surface "Provider CRUD", task 150a). Mirrors
 * customers/page.tsx's shape. Create is only shown to admins holding
 * "provider.write" - the API enforces this server-side regardless.
 *
 * Built on the task 221 pattern. Creation moved from an inline panel that
 * pushed the whole list down into a `Modal`, which restores focus to the "New
 * provider" button on close. Columns are NOT sortable: the list is paged
 * server-side and the endpoint takes no sort parameter.
 *
 * Wrapped in Suspense: `useSearchParams` (reading the Provider Onboarding
 * Overview dashboard's click-through filters) opts the tree below it out of
 * static rendering, and Next's App Router requires a Suspense boundary
 * around that or the production build fails (same pattern
 * coupons/redemptions/page.tsx uses).
 */
export default function ProvidersPage() {
  return (
    <Suspense fallback={<div className="w-full max-w-7xl px-6 py-10" />}>
      <ProvidersPageContent />
    </Suspense>
  );
}

function ProvidersPageContent() {
  const claims = useAdminClaims();
  const canWrite = claims?.permissions.includes("provider.write") ?? false;
  const router = useRouter();
  const queryClient = useQueryClient();
  const searchParams = useSearchParams();

  // Read once, on mount: the Onboarding Overview dashboard's tiles land here
  // with the day's cohort pre-filtered (see filtersFromSearchParams's doc
  // comment). Lazy useState initializer so a later, unrelated re-render never
  // stomps on filters the admin has since edited by hand.
  const [filters, setFilters] = useState<FilterFormState>(() => filtersFromSearchParams(searchParams));
  const [page, setPage] = useState(1);
  const [showCreateForm, setShowCreateForm] = useState(false);
  const [createForm, setCreateForm] = useState<CreateProviderRequest>(EMPTY_CREATE);
  const [createError, setCreateError] = useState<string | null>(null);
  // Row-select plus "Export selected" (task: premium UX audit) - the same
  // scope bookings/page.tsx's own bulk-action doc comment settled on: export
  // is unambiguously safe, while a bulk status change (suspend, activate)
  // would touch live provider accounts and belongs behind the same
  // per-provider review (a suspension reason, the KYC/background-check gate)
  // the detail page already requires one at a time. Keyed by provider id,
  // valued with the row itself so a selection survives paging - DataTable
  // only ever holds the current page's rows.
  const [selectedProviders, setSelectedProviders] = useState<Map<string, ProviderSummary>>(new Map());

  const citiesQuery = useQuery({ queryKey: ["cities"], queryFn: () => listCities() });

  // Live filtering (no Search button - task: "auto search when searching
  // something"): text fields (Name, Phone) are debounced 300ms before they
  // hit the query, same convention as the Name typeahead below and as
  // payments/reconciliation/page.tsx's search box; dropdowns/date pickers
  // apply immediately, same as every other filter page. Both debounced
  // values are seeded from the same URL-param read as `filters` (not "") so
  // a dashboard tile click-through (see filtersFromSearchParams) shows its
  // filtered list on first render, no 300ms gap and no click needed.
  const [debouncedName, setDebouncedName] = useState(() => filtersFromSearchParams(searchParams).name);
  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedName(filters.name.trim()), 300);
    return () => window.clearTimeout(handle);
  }, [filters.name]);

  const [debouncedPhone, setDebouncedPhone] = useState(() => filtersFromSearchParams(searchParams).phone);
  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedPhone(filters.phone.trim()), 300);
    return () => window.clearTimeout(handle);
  }, [filters.phone]);

  // Any filter change resets to page 1 and drops the row selection - staying
  // on page 3 of a now-smaller result set would just show an empty page (same
  // pattern as payments/reconciliation/page.tsx), and a selection made under
  // the old filters would be confusing carried over into a different result
  // set (same reasoning as bookings/page.tsx's own reset).
  useResetOnChange(
    [debouncedName, debouncedPhone, filters.status, filters.onboardingStatus, filters.cityId, filters.createdFrom, filters.createdTo],
    () => {
      setPage(1);
      setSelectedProviders(new Map());
    },
  );

  // Live typeahead for Name - reuses the same server-side search this page
  // already calls (searchProviders), same pattern as bookings/page.tsx's
  // Booking # suggestions. Shares `debouncedName` with the main query above
  // rather than debouncing twice.
  const nameSuggestionsQuery = useQuery({
    queryKey: ["admin-providers-name-suggestions", debouncedName],
    queryFn: () => searchProviders({ name: debouncedName, page: 1, pageSize: 8 }),
    enabled: debouncedName.length >= 2,
    placeholderData: keepPreviousData,
  });

  const query = useQuery({
    queryKey: [
      "admin-providers",
      page,
      debouncedName,
      debouncedPhone,
      filters.status,
      filters.onboardingStatus,
      filters.cityId,
      filters.createdFrom,
      filters.createdTo,
    ] as const,
    queryFn: () =>
      searchProviders({
        name: debouncedName || undefined,
        phone: debouncedPhone || undefined,
        status: filters.status === "" ? undefined : (Number(filters.status) as ProviderStatus),
        onboardingStatus:
          filters.onboardingStatus === "" ? undefined : (Number(filters.onboardingStatus) as ProviderOnboardingStatus),
        cityId: filters.cityId || undefined,
        // Local day boundaries, not `${date}T00:00:00Z` - see lib/day-range's
        // own doc comment on why (5h30m IST shift).
        createdFromUtc: filters.createdFrom ? (startOfLocalDayUtc(filters.createdFrom) ?? undefined) : undefined,
        createdToUtc: filters.createdTo ? (endOfLocalDayUtc(filters.createdTo) ?? undefined) : undefined,
        page,
        pageSize: PAGE_SIZE,
      }),
    placeholderData: keepPreviousData,
  });

  const createMutation = useMutation({
    mutationFn: () =>
      createProvider({
        legalName: createForm.legalName,
        displayName: createForm.displayName,
        phone: createForm.phone,
        email: createForm.email || undefined,
      }),
    onSuccess: (created) => {
      setCreateError(null);
      setCreateForm(EMPTY_CREATE);
      setShowCreateForm(false);
      queryClient.invalidateQueries({ queryKey: ["admin-providers"] });
      router.push(`/providers/${created.id}`);
    },
    onError: (err) => setCreateError(describeError(err)),
  });

  const onClear = () => {
    setFilters(EMPTY_FILTERS);
    setDebouncedName("");
    setDebouncedPhone("");
    setPage(1);
    setSelectedProviders(new Map());
  };

  const toggleSelection = (keys: Set<string>) => {
    const rows = query.data?.items ?? [];
    setSelectedProviders((current) => {
      const next = new Map(current);
      // A key present in `keys` but missing from `next` is a fresh selection
      // on the currently loaded page - look up its row there. A key already
      // in `next` that stays in `keys` is untouched. A key removed from
      // `keys` is deselected. Mirrors bookings/page.tsx's own toggleSelection.
      keys.forEach((key) => {
        if (next.has(key)) return;
        const row = rows.find((provider) => provider.id === key);
        if (row) next.set(key, row);
      });
      Array.from(next.keys()).forEach((key) => {
        if (!keys.has(key)) next.delete(key);
      });
      return next;
    });
  };

  const onExportSelected = () => {
    exportRowsToCsv(Array.from(selectedProviders.values()), PROVIDER_CSV_COLUMNS, `providers-export-${todayIsoDate()}.csv`);
  };

  /**
   * Closing without submitting (Cancel, the Modal's own X/Escape/backdrop) has
   * to clear `createForm`/`createError` here, not only in the mutation's
   * `onSuccess` - otherwise the next "New provider" open resumes mid-edit on
   * whatever was typed (or the last error) last time, since `useState`'s
   * initial value only applies once, on mount.
   */
  const closeCreateForm = () => {
    setShowCreateForm(false);
    setCreateForm(EMPTY_CREATE);
    setCreateError(null);
  };

  const createDisabled =
    !createForm.legalName.trim() || !createForm.displayName.trim() || !createForm.phone.trim();

  const columns: DataTableColumn<ProviderSummary>[] = [
    {
      key: "name",
      header: "Name",
      cell: (provider) => (
        <Link
          href={`/providers/${provider.id}`}
          className="font-medium text-fg underline-offset-4 hover:text-brand-600 hover:underline dark:hover:text-brand-400"
        >
          {provider.displayName}
        </Link>
      ),
    },
    { key: "phone", header: "Phone", cell: (provider) => <span className="nums">{provider.phone}</span> },
    { key: "email", header: "Email", cell: (provider) => provider.email ?? "—" },
    {
      key: "serviceCities",
      header: "Serves",
      cell: (provider) =>
        provider.serviceCities.length > 0 ? (
          <span className="truncate">{provider.serviceCities.join(", ")}</span>
        ) : (
          <span className="text-fg-subtle">Not configured</span>
        ),
    },
    {
      key: "status",
      header: "Status",
      cell: (provider) => <ProviderStatusBadge status={provider.status} label={statusLabel(provider.status)} />,
    },
    {
      key: "onboarding",
      header: "Onboarding",
      cell: (provider) => (
        <Badge
          tone={provider.onboardingStatus === ProviderOnboardingStatus.Completed ? "success" : "neutral"}
        >
          {ONBOARDING_LABELS[provider.onboardingStatus]}
        </Badge>
      ),
    },
    {
      key: "created",
      header: "Created",
      cell: (provider) => <span className="nums">{formatDate(provider.createdAt)}</span>,
    },
  ];

  return (
    <div className="w-full max-w-7xl">
      <PageHeading
        title="Providers"
        subtitle="Manage service providers: profile, KYC approval and performance (PROVIDER.md)."
        actions={
          canWrite ? <Button onClick={() => setShowCreateForm(true)}>New provider</Button> : undefined
        }
      />
      <ProvidersTabs />

      <FilterBar
        onClear={onClear}
        activeCount={countActiveFilters(filters)}
        busy={query.isFetching}
        columns={4}
      >
        <Field
          label="Name"
          name="name"
          autoComplete="name"
          list="provider-name-suggestions"
          value={filters.name}
          onChange={(e) => setFilters((f) => ({ ...f, name: e.target.value }))}
        />
        <datalist id="provider-name-suggestions">
          {(nameSuggestionsQuery.data?.items ?? []).map((provider) => (
            <option key={provider.id} value={provider.displayName} />
          ))}
        </datalist>
        <Field
          label="Phone"
          name="phone"
          autoComplete="tel"
          value={filters.phone}
          onChange={(e) => setFilters((f) => ({ ...f, phone: e.target.value }))}
        />
        <Select
          label="Status"
          value={filters.status}
          onChange={(e) => setFilters((f) => ({ ...f, status: e.target.value }))}
          options={STATUS_OPTIONS}
        />
        <Select
          label="Serves city"
          value={filters.cityId}
          onChange={(e) => setFilters((f) => ({ ...f, cityId: e.target.value }))}
          options={[
            { value: "", label: "Any city" },
            ...(citiesQuery.data ?? []).map((city) => ({ value: city.id, label: city.name })),
          ]}
        />
        <Select
          label="Onboarding stage"
          value={filters.onboardingStatus}
          onChange={(e) => setFilters((f) => ({ ...f, onboardingStatus: e.target.value }))}
          options={ONBOARDING_OPTIONS}
        />
        {/* Registered-date range - what the Provider Onboarding Overview
            dashboard's tiles filter on (createdFrom/createdTo). */}
        <Field
          label="Registered from"
          type="date"
          value={filters.createdFrom}
          onChange={(e) => setFilters((f) => ({ ...f, createdFrom: e.target.value }))}
        />
        <Field
          label="Registered to"
          type="date"
          value={filters.createdTo}
          onChange={(e) => setFilters((f) => ({ ...f, createdTo: e.target.value }))}
        />
      </FilterBar>

      <div className="mt-6">
        <DataTable
          title="Results"
          // Server-paged (task 221 pattern), so the default export is the
          // current page - same rows DataTable is already rendering. Once
          // rows are picked, "Export selected" takes over (only that
          // selection, which can span multiple pages visited).
          actions={
            selectedProviders.size > 0 ? (
              <>
                <span className="text-xs text-fg-subtle">{selectedProviders.size} selected</span>
                <Button size="sm" variant="secondary" onClick={onExportSelected}>
                  Export selected
                </Button>
                <Button size="sm" variant="ghost" onClick={() => setSelectedProviders(new Map())}>
                  Clear selection
                </Button>
              </>
            ) : (
              <ExportCsvButton
                rows={query.data?.items}
                columns={PROVIDER_CSV_COLUMNS}
                fileName={`providers-export-${todayIsoDate()}.csv`}
              />
            )
          }
          columns={columns}
          rows={query.data?.items}
          rowKey={(provider) => provider.id}
          isLoading={query.isPending}
          isFetching={query.isFetching}
          error={query.error}
          onRetry={() => query.refetch()}
          skeletonRows={8}
          minWidth="920px"
          caption="Providers matching the current filters"
          emptyTitle="No providers match these filters"
          emptyDescription="Try a partial name or phone number, or clear the filters to see every provider."
          emptyAction={
            <Button variant="secondary" onClick={onClear}>
              Clear filters
            </Button>
          }
          selection={{
            selectedKeys: new Set(selectedProviders.keys()),
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
                itemLabel="provider"
              />
            ) : null
          }
        />
      </div>

      <Modal
        open={showCreateForm}
        onClose={closeCreateForm}
        title="Create provider"
        description="Provider type is always Individual in v1 (PROVIDER.md OPEN DECISIONS #2)."
        footer={
          <>
            <Button variant="secondary" onClick={closeCreateForm} disabled={createMutation.isPending}>
              Cancel
            </Button>
            <Button
              disabled={createDisabled}
              loading={createMutation.isPending}
              onClick={() => createMutation.mutate()}
            >
              Create provider
            </Button>
          </>
        }
      >
        <div className="flex flex-col gap-4">
          {createError ? <Alert>{createError}</Alert> : null}
          <FormGrid>
            <Field
              label="Legal name"
              required
              value={createForm.legalName}
              onChange={(e) => setCreateForm((f) => ({ ...f, legalName: e.target.value }))}
            />
            <Field
              label="Display name"
              required
              hint="Shown to customers."
              value={createForm.displayName}
              onChange={(e) => setCreateForm((f) => ({ ...f, displayName: e.target.value }))}
            />
            <Field
              label="Phone"
              required
              inputMode="numeric"
              value={createForm.phone}
              onChange={(e) => setCreateForm((f) => ({ ...f, phone: e.target.value }))}
            />
            <Field
              label="Email"
              type="email"
              hint="Optional."
              value={createForm.email ?? ""}
              onChange={(e) => setCreateForm((f) => ({ ...f, email: e.target.value }))}
            />
          </FormGrid>
        </div>
      </Modal>
    </div>
  );
}
