"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useParams } from "next/navigation";
import { useState } from "react";
import { Breadcrumbs, DataTable, DescriptionList, formatCurrency, formatDateTime } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { Alert, Badge, Button, Card, EmptyState, KpiCard, Modal, PageHeading, Select, Skeleton, Textarea, useToast } from "@/components/ui";
import { describeError } from "@/lib/api";
import { canWriteModule } from "@/lib/permissions";
import { useAdminClaims } from "@/lib/use-admin-claims";
import {
  ACTOR_LABELS,
  ADMIN_ATTENDANCE_ACTIONS,
  ATTENDANCE_LABELS,
  MonthlyServiceContractStatus,
  MonthlyServiceDisputeStatus,
  MonthlyServiceInvoiceStatus,
  MonthlyServicePlanBasis,
  assignMonthlyProvider,
  cancelMonthlyContract,
  describeSchedule,
  formatClock,
  formatDay,
  formatPeriod,
  getMonthlyContract,
  getMonthlyContractAttendance,
  listEligibleProviders,
  pauseMonthlyContract,
  resumeMonthlyContract,
} from "../_lib/monthly-service-api";
import type { MonthlyServiceAttendanceItem, MonthlyServiceContractDetail, MonthlyServiceInvoice } from "../_lib/monthly-service-api";
import { ATTENDANCE_TONE, ContractStatusBadge, InvoiceStatusBadge, KpiIcons, MONTHLY_KEYS, RecordPaymentModal } from "../_components/shared";
import { AttendanceActionModal } from "../_components/AttendanceActionModal";

/**
 * One engagement (docs/MONTHLY-SERVICE.md): terms and address, the assigned
 * professional (assign / replace with conflict checking), the month's
 * attendance register with corrections, and its invoices.
 */
export default function MonthlyServiceContractPage() {
  const { id } = useParams<{ id: string }>();
  const claims = useAdminClaims();
  const canWriteBookings = canWriteModule(claims, "bookings");
  const canWritePayments = canWriteModule(claims, "payments");
  const query = useQuery({ queryKey: MONTHLY_KEYS.contract(id), queryFn: () => getMonthlyContract(id) });

  if (query.isPending) {
    return (
      <div className="flex flex-col gap-4" aria-hidden>
        <Skeleton className="h-16 rounded-xl" />
        <Skeleton className="h-64 rounded-2xl" />
      </div>
    );
  }

  if (query.isError) {
    return (
      <Alert tone="error" title="Couldn't load this engagement" action={<Button size="sm" variant="secondary" onClick={() => void query.refetch()}>Retry</Button>}>
        {describeError(query.error)}
      </Alert>
    );
  }

  const detail = query.data;
  const c = detail.contract;

  return (
    <div className="flex w-full flex-col gap-6">
      <PageHeading
        title={`${c.customerName} · ${c.planName}`}
        subtitle={`${describeSchedule(c)} at ${formatClock(c.visitStartTime)} · ${c.cityName}`}
        breadcrumbs={<Breadcrumbs items={[{ label: "Monthly Service", href: "/monthly-service" }, { label: c.customerName }]} />}
        actions={canWriteBookings ? <LifecycleActions detail={detail} /> : undefined}
      />

      <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
        <KpiCard icon={KpiIcons.present} tone="success" label="Present this month" value={String(detail.currentMonth.present)} />
        <KpiCard icon={KpiIcons.away} tone="warning" label="Leave / absent" value={`${detail.currentMonth.providerLeave} / ${detail.currentMonth.absent}`} />
        <KpiCard icon={KpiIcons.upcoming} tone="info" label="Upcoming" value={String(detail.currentMonth.upcoming)} />
        <KpiCard icon={KpiIcons.money} tone="brand" label="Billable so far" value={formatCurrency(detail.currentMonth.billableAmount)} />
      </div>

      <div className="grid grid-cols-1 gap-6 xl:grid-cols-3">
        <div className="flex flex-col gap-6 xl:col-span-2">
          <AttendanceCard contractId={c.id} canWrite={canWriteBookings} />
          <InvoicesCard invoices={detail.invoices} canRecord={canWritePayments} />
        </div>
        <div className="flex flex-col gap-6">
          <ProfessionalCard detail={detail} canWrite={canWriteBookings} />
          <Card title="Engagement" actions={<ContractStatusBadge status={c.status} pauseReason={c.pauseReason} />}>
            <DescriptionList
              columns={1}
              items={[
                { label: "Customer", value: `${c.customerName} (${c.customerPhone})` },
                {
                  label: "Address",
                  value: detail.address
                    ? `${detail.address.line1}${detail.address.line2 ? `, ${detail.address.line2}` : ""}, ${detail.address.city} ${detail.address.pincode}`
                    : "—",
                },
                { label: "Contact at home", value: detail.address ? `${detail.address.contactName} · ${detail.address.contactMobile}` : "—" },
                {
                  label: "Visit",
                  value:
                    detail.basis === MonthlyServicePlanBasis.Hourly && detail.hoursPerVisit
                      ? `${detail.hoursPerVisit} hours`
                      : "Task-based",
                },
                { label: "Tasks", value: detail.includedTasks.length ? detail.includedTasks.join(", ") : "—" },
                { label: "Rate / commission", value: `${formatCurrency(detail.ratePerVisit)} per visit · ${detail.commissionPercent}%` },
                { label: "Period", value: `${formatDay(c.startDate)}${c.endDate ? ` → ${formatDay(c.endDate)}` : " onwards"}` },
                { label: "Customer note", value: detail.customerNote ?? "—" },
                ...(detail.cancelledAtUtc
                  ? [{ label: "Cancelled", value: `${formatDateTime(detail.cancelledAtUtc)}${detail.cancellationReason ? ` — ${detail.cancellationReason}` : ""}` }]
                  : []),
              ]}
            />
          </Card>
        </div>
      </div>
    </div>
  );
}

function LifecycleActions({ detail }: { detail: MonthlyServiceContractDetail }) {
  const toast = useToast();
  const queryClient = useQueryClient();
  const [cancelOpen, setCancelOpen] = useState(false);
  const [reason, setReason] = useState("");
  const id = detail.contract.id;
  const status = detail.contract.status;

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: MONTHLY_KEYS.contract(id) });
    void queryClient.invalidateQueries({ queryKey: MONTHLY_KEYS.attendance(id) });
    void queryClient.invalidateQueries({ queryKey: MONTHLY_KEYS.contracts });
  };

  const mutation = useMutation({
    mutationFn: (action: "pause" | "resume" | "cancel") =>
      action === "pause" ? pauseMonthlyContract(id) : action === "resume" ? resumeMonthlyContract(id) : cancelMonthlyContract(id, reason.trim() || null),
    onSuccess: (_, action) => {
      toast("success", action === "pause" ? "Engagement paused." : action === "resume" ? "Engagement resumed." : "Engagement cancelled.");
      setCancelOpen(false);
      refresh();
    },
    onError: (error) => toast("error", describeError(error)),
  });

  if (status === MonthlyServiceContractStatus.Cancelled) return null;

  return (
    <div className="flex flex-wrap gap-2">
      {status === MonthlyServiceContractStatus.Active ? (
        <Button size="sm" variant="secondary" loading={mutation.isPending && mutation.variables === "pause"} onClick={() => mutation.mutate("pause")}>
          Pause
        </Button>
      ) : null}
      {status === MonthlyServiceContractStatus.Paused ? (
        <Button size="sm" variant="secondary" loading={mutation.isPending && mutation.variables === "resume"} onClick={() => mutation.mutate("resume")}>
          Resume
        </Button>
      ) : null}
      <Button size="sm" variant="danger" onClick={() => setCancelOpen(true)}>
        Cancel engagement
      </Button>
      <Modal
        open={cancelOpen}
        onClose={() => !mutation.isPending && setCancelOpen(false)}
        title="Cancel this engagement?"
        description="Upcoming visits are removed. Days already recorded are still billed at month end."
        size="sm"
        footer={
          <>
            <Button variant="secondary" onClick={() => setCancelOpen(false)}>
              Keep
            </Button>
            <Button variant="danger" loading={mutation.isPending} onClick={() => mutation.mutate("cancel")}>
              Cancel engagement
            </Button>
          </>
        }
      >
        <Textarea label="Reason (optional)" maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)} />
      </Modal>
    </div>
  );
}

function ProfessionalCard({ detail, canWrite }: { detail: MonthlyServiceContractDetail; canWrite: boolean }) {
  const toast = useToast();
  const queryClient = useQueryClient();
  const c = detail.contract;
  const [picking, setPicking] = useState(c.providerId === null);
  const [selected, setSelected] = useState("");
  const open = c.status !== MonthlyServiceContractStatus.Cancelled;

  const eligible = useQuery({
    queryKey: ["admin-monthly-eligible", c.id],
    queryFn: () => listEligibleProviders(c.id),
    enabled: canWrite && open && picking,
  });

  const assign = useMutation({
    mutationFn: () => assignMonthlyProvider(c.id, selected),
    onSuccess: (updated) => {
      toast("success", `${updated.contract.providerName} assigned. Upcoming visits are scheduled.`);
      setPicking(false);
      setSelected("");
      void queryClient.invalidateQueries({ queryKey: MONTHLY_KEYS.contract(c.id) });
      void queryClient.invalidateQueries({ queryKey: MONTHLY_KEYS.attendance(c.id) });
      void queryClient.invalidateQueries({ queryKey: MONTHLY_KEYS.contracts });
    },
  });

  const chosen = eligible.data?.find((p) => p.id === selected);

  return (
    <Card
      title="Professional"
      description={c.providerId ? "Same professional every visit. Replace only if they can no longer serve this home." : "Assign a professional to start the schedule."}
      actions={
        canWrite && open && c.providerId && !picking ? (
          <Button size="sm" variant="ghost" onClick={() => setPicking(true)}>
            Replace
          </Button>
        ) : undefined
      }
    >
      {c.providerName ? <p className="mb-3 text-base font-semibold text-fg">{c.providerName}</p> : null}

      {canWrite && open && picking ? (
        eligible.isPending ? (
          <Skeleton className="h-10 rounded-lg" />
        ) : eligible.isError ? (
          <Alert tone="error">{describeError(eligible.error)}</Alert>
        ) : eligible.data.length === 0 ? (
          <EmptyState title="No eligible professionals" description="No active professional has this skill or serves this city yet." />
        ) : (
          <div className="flex flex-col gap-3">
            <Select
              label="Choose professional"
              value={selected}
              onChange={(e) => setSelected(e.target.value)}
              options={[
                { value: "", label: "Select…" },
                ...eligible.data
                  .filter((p) => p.id !== c.providerId)
                  .map((p) => ({
                    value: p.id,
                    label: `${p.displayName} · ${p.activeContractCount} home(s)${p.hasSkill && p.servesCity ? "" : !p.hasSkill ? " · no skill mapped" : " · outside city"}${p.conflict ? " · CONFLICT" : ""}`,
                  })),
              ]}
            />
            {chosen?.conflict ? <Alert tone="warning">{chosen.conflict}</Alert> : null}
            {assign.isError ? <Alert tone="error">{describeError(assign.error)}</Alert> : null}
            <div className="flex gap-2">
              <Button size="sm" disabled={!selected || Boolean(chosen?.conflict)} loading={assign.isPending} onClick={() => assign.mutate()}>
                {c.providerId ? "Replace professional" : "Assign"}
              </Button>
              {c.providerId ? (
                <Button size="sm" variant="ghost" onClick={() => setPicking(false)}>
                  Cancel
                </Button>
              ) : null}
            </div>
          </div>
        )
      ) : !c.providerName ? (
        <p className="text-sm text-fg-muted">Not assigned yet.</p>
      ) : null}
    </Card>
  );
}

function AttendanceCard({ contractId, canWrite }: { contractId: string; canWrite: boolean }) {
  const now = new Date();
  const [cursor, setCursor] = useState({ year: now.getFullYear(), month: now.getMonth() + 1 });
  const [acting, setActing] = useState<MonthlyServiceAttendanceItem | null>(null);

  const query = useQuery({
    queryKey: [...MONTHLY_KEYS.attendance(contractId), cursor.year, cursor.month],
    queryFn: () => getMonthlyContractAttendance(contractId, cursor.year, cursor.month),
  });

  const shift = (delta: number) =>
    setCursor((c) => {
      const d = new Date(c.year, c.month - 1 + delta, 1);
      return { year: d.getFullYear(), month: d.getMonth() + 1 };
    });

  const columns: DataTableColumn<MonthlyServiceAttendanceItem>[] = [
    { key: "date", header: "Date", cell: (r) => formatDay(r.date), sortValue: (r) => r.date },
    {
      key: "status",
      header: "Status",
      cell: (r) => (
        <div className="flex flex-wrap items-center gap-1.5">
          <Badge tone={ATTENDANCE_TONE[r.status]}>{ATTENDANCE_LABELS[r.status]}</Badge>
          {r.disputeStatus === MonthlyServiceDisputeStatus.Open ? <Badge tone="danger">Disputed</Badge> : null}
          {r.isInvoiced ? <Badge tone="neutral">Invoiced</Badge> : null}
        </div>
      ),
      sortValue: (r) => r.status,
    },
    { key: "by", header: "Marked by", cell: (r) => (r.markedBy === null ? "—" : ACTOR_LABELS[r.markedBy]) },
    {
      key: "times",
      header: "In / out",
      cell: (r) =>
        r.checkedInAtUtc
          ? `${new Date(r.checkedInAtUtc).toLocaleTimeString("en-IN", { hour: "numeric", minute: "2-digit" })}${
              r.checkedOutAtUtc ? ` – ${new Date(r.checkedOutAtUtc).toLocaleTimeString("en-IN", { hour: "numeric", minute: "2-digit" })}` : ""
            }`
          : "—",
    },
    { key: "note", header: "Note", cell: (r) => <span className="line-clamp-2 text-xs text-fg-muted">{r.disputeReason ?? r.note ?? "—"}</span> },
  ];

  const summary = query.data?.summary;

  return (
    <>
      <DataTable
        title={`Attendance · ${new Date(cursor.year, cursor.month - 1, 1).toLocaleDateString("en-IN", { month: "long", year: "numeric" })}`}
        description={
          summary
            ? `${summary.present} present · ${summary.customerSkipped} skipped · ${summary.providerLeave} leave · ${summary.absent} absent · ${summary.upcoming} upcoming · ${formatCurrency(summary.billableAmount)} billable`
            : undefined
        }
        actions={
          <div className="flex items-center gap-1">
            <Button size="sm" variant="ghost" aria-label="Previous month" onClick={() => shift(-1)}>
              ‹
            </Button>
            <Button size="sm" variant="ghost" aria-label="Next month" onClick={() => shift(1)}>
              ›
            </Button>
          </div>
        }
        columns={columns}
        rows={query.data?.items}
        rowKey={(r) => r.id}
        isLoading={query.isPending}
        isFetching={query.isFetching}
        error={query.error}
        onRetry={() => void query.refetch()}
        maxHeight="28rem"
        minWidth="720px"
        caption="Attendance register for the selected month"
        emptyTitle="No visits this month"
        emptyDescription="Days appear once a professional is assigned and the schedule reaches this month."
        rowActions={
          canWrite
            ? (r) =>
                r.allowedActions.includes(ADMIN_ATTENDANCE_ACTIONS.resolveDispute) ? (
                  <Button size="sm" variant="secondary" onClick={() => setActing(r)}>
                    Resolve
                  </Button>
                ) : r.allowedActions.includes(ADMIN_ATTENDANCE_ACTIONS.correct) ? (
                  <Button size="sm" variant="ghost" onClick={() => setActing(r)}>
                    Correct
                  </Button>
                ) : null
            : undefined
        }
      />
      <AttendanceActionModal
        item={acting}
        onClose={() => setActing(null)}
        invalidateKeys={[MONTHLY_KEYS.attendance(contractId), MONTHLY_KEYS.contract(contractId), MONTHLY_KEYS.disputes]}
      />
    </>
  );
}

function InvoicesCard({ invoices, canRecord }: { invoices: MonthlyServiceInvoice[]; canRecord: boolean }) {
  const [recording, setRecording] = useState<MonthlyServiceInvoice | null>(null);
  const columns: DataTableColumn<MonthlyServiceInvoice>[] = [
    { key: "period", header: "Month", cell: (i) => formatPeriod(i.periodStart), sortValue: (i) => i.periodStart },
    { key: "pro", header: "Professional", cell: (i) => i.providerName },
    { key: "visits", header: "Visits", numeric: true, cell: (i) => i.billableVisits },
    { key: "amount", header: "Amount", numeric: true, cell: (i) => formatCurrency(i.amount) },
    { key: "net", header: "Pro share", numeric: true, cell: (i) => formatCurrency(i.providerNetAmount) },
    { key: "due", header: "Due", cell: (i) => formatDay(i.dueDate) },
    { key: "status", header: "Status", cell: (i) => <InvoiceStatusBadge status={i.status} /> },
  ];
  return (
    <>
      <DataTable
        title="Invoices"
        description="Issued on the 2nd of each month for the previous month's billable visits."
        columns={columns}
        rows={invoices}
        rowKey={(i) => i.id}
        minWidth="720px"
        hideDensityToggle
        caption="Invoices for this engagement"
        emptyTitle="No invoices yet"
        emptyDescription="The first invoice is issued after the first month with billable visits."
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
      <RecordPaymentModal invoice={recording} onClose={() => setRecording(null)} />
    </>
  );
}

