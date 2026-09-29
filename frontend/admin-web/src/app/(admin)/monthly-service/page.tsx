"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { DataTable, FilterBar, Pagination, countActiveFilters, formatDate } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { Button, Field, PageHeading, Select, useToast } from "@/components/ui";
import { describeError } from "@/lib/api";
import { canWriteModule } from "@/lib/permissions";
import { useAdminClaims } from "@/lib/use-admin-claims";
import {
  CONTRACT_STATUS_LABELS,
  MonthlyServiceContractStatus,
  describeSchedule,
  formatClock,
  formatDay,
  runMonthlyDailyJob,
  searchMonthlyContracts,
} from "./_lib/monthly-service-api";
import type { MonthlyServiceContractListItem } from "./_lib/monthly-service-api";
import { ContractStatusBadge, MONTHLY_KEYS, MonthlyServiceTabs } from "./_components/shared";

const PAGE_SIZE = 20;
const STATUSES = [
  MonthlyServiceContractStatus.PendingAssignment,
  MonthlyServiceContractStatus.Active,
  MonthlyServiceContractStatus.Paused,
  MonthlyServiceContractStatus.Cancelled,
];

/**
 * Monthly Service engagements (docs/MONTHLY-SERVICE.md): every maid-style
 * contract, newest first. "Needs professional" is the operations queue -
 * open one to assign a professional. Filters apply as you type.
 */
export default function MonthlyServiceContractsPage() {
  const router = useRouter();
  const toast = useToast();
  const queryClient = useQueryClient();
  const claims = useAdminClaims();
  const canWrite = canWriteModule(claims, "bookings");

  const [status, setStatus] = useState<string>("");
  const [searchInput, setSearchInput] = useState("");
  const [search, setSearch] = useState("");
  const [page, setPage] = useState(1);

  useEffect(() => {
    const handle = window.setTimeout(() => setSearch(searchInput.trim()), 300);
    return () => window.clearTimeout(handle);
  }, [searchInput]);

  useEffect(() => setPage(1), [status, search]);

  const query = useQuery({
    queryKey: [...MONTHLY_KEYS.contracts, status, search, page],
    queryFn: () =>
      searchMonthlyContracts({
        status: status === "" ? undefined : (Number(status) as MonthlyServiceContractStatus),
        customerSearch: search || undefined,
        page,
        pageSize: PAGE_SIZE,
      }),
    placeholderData: (previous) => previous,
  });

  const runJob = useMutation({
    mutationFn: runMonthlyDailyJob,
    onSuccess: (r) => {
      toast(
        "success",
        `Daily run: ${r.attendanceRowsScheduled} scheduled, ${r.daysClosedAsAbsent} closed, ${r.invoicesIssued} invoiced, ${r.contractsPausedForNonPayment} paused.`,
      );
      void queryClient.invalidateQueries({ queryKey: MONTHLY_KEYS.contracts });
      void queryClient.invalidateQueries({ queryKey: MONTHLY_KEYS.invoices });
    },
    onError: (error) => toast("error", describeError(error)),
  });

  const columns: DataTableColumn<MonthlyServiceContractListItem>[] = [
    {
      key: "customer",
      header: "Customer",
      cell: (row) => (
        <div className="min-w-0">
          <p className="truncate font-medium text-fg">{row.customerName}</p>
          <p className="nums text-xs text-fg-muted">{row.customerPhone}</p>
        </div>
      ),
      sortValue: (row) => row.customerName,
    },
    {
      key: "plan",
      header: "Plan",
      cell: (row) => (
        <div className="min-w-0">
          <p className="truncate text-fg">{row.planName}</p>
          <p className="text-xs text-fg-muted">{row.cityName}</p>
        </div>
      ),
      sortValue: (row) => row.planName,
    },
    {
      key: "schedule",
      header: "Schedule",
      cell: (row) => (
        <span className="text-sm text-fg">
          {describeSchedule(row)} · {formatClock(row.visitStartTime)}
        </span>
      ),
    },
    {
      key: "provider",
      header: "Professional",
      cell: (row) => row.providerName ?? <span className="text-fg-subtle">Not assigned</span>,
      sortValue: (row) => row.providerName,
    },
    { key: "start", header: "Starts", cell: (row) => formatDay(row.startDate), sortValue: (row) => row.startDate },
    {
      key: "status",
      header: "Status",
      cell: (row) => <ContractStatusBadge status={row.status} pauseReason={row.pauseReason} />,
      sortValue: (row) => row.status,
    },
    { key: "created", header: "Requested", cell: (row) => formatDate(row.createdAtUtc), sortValue: (row) => row.createdAtUtc },
  ];

  return (
    <div className="flex w-full flex-col gap-6">
      <PageHeading
        title="Monthly Service"
        subtitle="Maid-style engagements: same professional on fixed days, attendance-based month-end billing."
        actions={
          canWrite ? (
            <Button size="sm" variant="secondary" loading={runJob.isPending} onClick={() => runJob.mutate()}>
              Run daily job now
            </Button>
          ) : undefined
        }
      />
      <MonthlyServiceTabs />

      <FilterBar
        activeCount={countActiveFilters({ status, search })}
        onClear={() => {
          setStatus("");
          setSearchInput("");
          setSearch("");
        }}
        busy={query.isFetching}
        columns={2}
      >
        <Field
          label="Customer"
          placeholder="Name or mobile"
          value={searchInput}
          onChange={(e) => setSearchInput(e.target.value)}
        />
        <Select
          label="Status"
          value={status}
          onChange={(e) => setStatus(e.target.value)}
          options={[{ value: "", label: "All statuses" }, ...STATUSES.map((s) => ({ value: String(s), label: CONTRACT_STATUS_LABELS[s] }))]}
        />
      </FilterBar>

      <DataTable
        title="Engagements"
        description="Open one to assign a professional, view attendance or record payments."
        columns={columns}
        rows={query.data?.items}
        rowKey={(row) => row.id}
        onRowClick={(row) => router.push(`/monthly-service/${row.id}`)}
        isLoading={query.isPending}
        isFetching={query.isFetching}
        error={query.error}
        onRetry={() => void query.refetch()}
        minWidth="960px"
        caption="Monthly service engagements matching the current filters"
        emptyTitle="No engagements match"
        emptyDescription="Customers request monthly services from the customer app; they show up here to be assigned."
      />

      {query.data && query.data.totalCount > 0 ? (
        <Pagination
          page={query.data.page}
          pageSize={query.data.pageSize}
          totalCount={query.data.totalCount}
          onPageChange={setPage}
          itemLabel="engagement"
          itemLabelPlural="engagements"
          busy={query.isFetching}
        />
      ) : null}
    </div>
  );
}
