"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useState } from "react";
import { DataTable } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { Badge, Button, PageHeading } from "@/components/ui";
import { canWriteModule } from "@/lib/permissions";
import { useAdminClaims } from "@/lib/use-admin-claims";
import { ATTENDANCE_LABELS, formatDay, listMonthlyDisputes } from "../_lib/monthly-service-api";
import type { MonthlyServiceAttendanceItem, MonthlyServiceDispute } from "../_lib/monthly-service-api";
import { ATTENDANCE_TONE, MONTHLY_KEYS, MonthlyServiceTabs } from "../_components/shared";
import { AttendanceActionModal } from "../_components/AttendanceActionModal";

/**
 * Open attendance disputes (docs/MONTHLY-SERVICE.md): a customer says a
 * charged day should not be charged. Each one holds that engagement's
 * month-end invoice until it is resolved here - oldest first.
 */
export default function MonthlyServiceDisputesPage() {
  const claims = useAdminClaims();
  const canWrite = canWriteModule(claims, "bookings");
  const [acting, setActing] = useState<MonthlyServiceAttendanceItem | null>(null);
  const query = useQuery({ queryKey: MONTHLY_KEYS.disputes, queryFn: listMonthlyDisputes });

  const columns: DataTableColumn<MonthlyServiceDispute>[] = [
    { key: "date", header: "Day", cell: (d) => formatDay(d.attendance.date), sortValue: (d) => d.attendance.date },
    {
      key: "customer",
      header: "Customer",
      cell: (d) => (
        <Link href={`/monthly-service/${d.attendance.contractId}`} className="font-medium text-brand-600 hover:underline dark:text-brand-400">
          {d.customerName}
        </Link>
      ),
      sortValue: (d) => d.customerName,
    },
    { key: "pro", header: "Professional", cell: (d) => d.providerName, sortValue: (d) => d.providerName },
    {
      key: "recorded",
      header: "Recorded as",
      cell: (d) => <Badge tone={ATTENDANCE_TONE[d.attendance.status]}>{ATTENDANCE_LABELS[d.attendance.status]}</Badge>,
    },
    { key: "reason", header: "Customer says", cell: (d) => <span className="line-clamp-2 text-sm">{d.attendance.disputeReason}</span> },
  ];

  return (
    <div className="flex w-full flex-col gap-6">
      <PageHeading title="Monthly Service" subtitle="Attendance disputes waiting for a decision. Each holds its month's invoice." />
      <MonthlyServiceTabs />
      <DataTable
        title="Open disputes"
        description={query.data ? `${query.data.length} waiting` : undefined}
        columns={columns}
        rows={query.data}
        rowKey={(d) => d.attendance.id}
        isLoading={query.isPending}
        isFetching={query.isFetching}
        error={query.error}
        onRetry={() => void query.refetch()}
        minWidth="820px"
        caption="Open monthly service attendance disputes"
        emptyTitle="No open disputes"
        emptyDescription="Customers can report a charged day from their attendance calendar until the month is invoiced."
        rowActions={
          canWrite
            ? (d) => (
                <Button size="sm" variant="secondary" onClick={() => setActing(d.attendance)}>
                  Resolve
                </Button>
              )
            : undefined
        }
      />
      <AttendanceActionModal
        item={acting}
        onClose={() => setActing(null)}
        invalidateKeys={[MONTHLY_KEYS.disputes, ...(acting ? [MONTHLY_KEYS.attendance(acting.contractId), MONTHLY_KEYS.contract(acting.contractId)] : [])]}
      />
    </div>
  );
}
