"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useEffect, useState } from "react";
import { DataTable, FilterBar, Pagination, countActiveFilters, formatCurrency } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { Button, KpiCard, PageHeading, Select } from "@/components/ui";
import { canWriteModule } from "@/lib/permissions";
import { useAdminClaims } from "@/lib/use-admin-claims";
import {
  INVOICE_STATUS_LABELS,
  MonthlyServiceInvoiceStatus,
  PAYMENT_METHOD_LABELS,
  formatDay,
  formatPeriod,
  searchMonthlyInvoices,
} from "../_lib/monthly-service-api";
import type { MonthlyServiceInvoice } from "../_lib/monthly-service-api";
import { InvoiceStatusBadge, KpiIcons, MONTHLY_KEYS, MonthlyServiceTabs, RecordPaymentModal } from "../_components/shared";

const PAGE_SIZE = 20;
const STATUSES = [MonthlyServiceInvoiceStatus.Issued, MonthlyServiceInvoiceStatus.Overdue, MonthlyServiceInvoiceStatus.Paid];

/**
 * Month-end Monthly Service invoices (docs/MONTHLY-SERVICE.md BILLING), with
 * the outstanding total and offline payment recording (cash / UPI / bank
 * transfer) - the expected main collection channel at launch.
 */
export default function MonthlyServiceInvoicesPage() {
  const claims = useAdminClaims();
  const canRecord = canWriteModule(claims, "payments");
  const [status, setStatus] = useState("");
  const [page, setPage] = useState(1);
  const [recording, setRecording] = useState<MonthlyServiceInvoice | null>(null);

  useEffect(() => setPage(1), [status]);

  const query = useQuery({
    queryKey: [...MONTHLY_KEYS.invoices, status, page],
    queryFn: () =>
      searchMonthlyInvoices({
        status: status === "" ? undefined : (Number(status) as MonthlyServiceInvoiceStatus),
        page,
        pageSize: PAGE_SIZE,
      }),
    placeholderData: (previous) => previous,
  });

  const columns: DataTableColumn<MonthlyServiceInvoice>[] = [
    { key: "period", header: "Month", cell: (i) => formatPeriod(i.periodStart), sortValue: (i) => i.periodStart },
    {
      key: "customer",
      header: "Customer",
      cell: (i) => (
        <Link href={`/monthly-service/${i.contractId}`} className="font-medium text-brand-600 hover:underline dark:text-brand-400">
          {i.customerName}
        </Link>
      ),
      sortValue: (i) => i.customerName,
    },
    { key: "pro", header: "Professional", cell: (i) => i.providerName, sortValue: (i) => i.providerName },
    { key: "visits", header: "Visits", numeric: true, cell: (i) => i.billableVisits, sortValue: (i) => i.billableVisits },
    { key: "amount", header: "Amount", numeric: true, cell: (i) => formatCurrency(i.amount), sortValue: (i) => i.amount },
    { key: "commission", header: "Commission", numeric: true, cell: (i) => formatCurrency(i.commissionAmount) },
    { key: "due", header: "Due", cell: (i) => formatDay(i.dueDate), sortValue: (i) => i.dueDate },
    {
      key: "status",
      header: "Status",
      cell: (i) => (
        <div className="flex flex-col items-start gap-0.5">
          <InvoiceStatusBadge status={i.status} />
          {i.paymentMethod !== null ? <span className="text-xs text-fg-subtle">{PAYMENT_METHOD_LABELS[i.paymentMethod]}</span> : null}
        </div>
      ),
      sortValue: (i) => i.status,
    },
  ];

  return (
    <div className="flex w-full flex-col gap-6">
      <PageHeading title="Monthly Service" subtitle="Month-end invoices, issued on the 2nd for the previous month's billable visits." />
      <MonthlyServiceTabs />

      <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
        <KpiCard icon={KpiIcons.money} tone="danger" label="Outstanding" value={query.data ? formatCurrency(query.data.outstandingAmount) : "—"} />
      </div>

      <FilterBar activeCount={countActiveFilters({ status })} onClear={() => setStatus("")} busy={query.isFetching} columns={2}>
        <Select
          label="Status"
          value={status}
          onChange={(e) => setStatus(e.target.value)}
          options={[{ value: "", label: "All" }, ...STATUSES.map((s) => ({ value: String(s), label: INVOICE_STATUS_LABELS[s] }))]}
        />
      </FilterBar>

      <DataTable
        title="Invoices"
        columns={columns}
        rows={query.data?.items}
        rowKey={(i) => i.id}
        isLoading={query.isPending}
        isFetching={query.isFetching}
        error={query.error}
        onRetry={() => void query.refetch()}
        minWidth="980px"
        caption="Monthly service invoices matching the current filter"
        emptyTitle="No invoices"
        emptyDescription="Invoices appear after the first month with billable visits."
        rowActions={
          canRecord
            ? (i) =>
                i.status !== MonthlyServiceInvoiceStatus.Paid ? (
                  <Button size="sm" variant="secondary" onClick={() => setRecording(i)}>
                    Record payment
                  </Button>
                ) : null
            : undefined
        }
      />

      {query.data && query.data.totalCount > 0 ? (
        <Pagination
          page={query.data.page}
          pageSize={query.data.pageSize}
          totalCount={query.data.totalCount}
          onPageChange={setPage}
          itemLabel="invoice"
          itemLabelPlural="invoices"
          busy={query.isFetching}
        />
      ) : null}

      <RecordPaymentModal invoice={recording} onClose={() => setRecording(null)} />
    </div>
  );
}
