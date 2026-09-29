"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useParams } from "next/navigation";
import { useMemo, useState } from "react";
import { BannerBreadcrumb, ScreenSkeleton, inr } from "@/components/patterns";
import { PageBanner } from "@/components/PageBanner";
import { RequireAuth } from "@/components/RequireAuth";
import { Alert, Badge, Button, Card, EmptyState, Modal, Textarea, cx, useToast } from "@/components/ui";
import { describeError } from "@/lib/api";
import {
  MONTHLY_SERVICE_ACTIONS,
  MonthlyServiceAttendanceStatus,
  MonthlyServiceContractStatus,
  MonthlyServiceDisputeStatus,
  MonthlyServiceInvoiceStatus,
  actOnAttendance,
  attendanceLabel,
  cancelMyMonthlyService,
  describeDays,
  describeVisit,
  disputeAttendance,
  formatClock,
  getMonthlyAttendance,
  getMyMonthlyService,
  listMyMonthlyInvoices,
  payMonthlyInvoice,
} from "@/lib/monthly-service";
import type {
  MonthlyServiceAttendanceItem,
  MonthlyServiceAttendanceMonth,
  MonthlyServiceContract,
  MonthlyServiceInvoice,
} from "@/lib/monthly-service";
import {
  ContractStatusBadge,
  InvoiceStatusBadge,
  MY_MONTHLY_INVOICES_KEY,
  MY_MONTHLY_SERVICES_KEY,
  Stat,
  attendanceTone,
  formatDay,
  monthTitle,
} from "../_components/shared";

/**
 * One monthly engagement (docs/MONTHLY-SERVICE.md): today's visit code, the
 * month's attendance register as a calendar (skip / confirm / dispute a day),
 * and the month-end bills. Every action re-reads the server's view - the
 * server decides what is allowed (`allowedActions`), the page only offers it.
 */
export default function MonthlyServiceDetailPage() {
  return (
    <RequireAuth>
      <DetailScreen />
    </RequireAuth>
  );
}

function DetailScreen() {
  const { id } = useParams<{ id: string }>();
  const contractKey = ["my-monthly-service", id] as const;
  const contractQuery = useQuery({ queryKey: contractKey, queryFn: () => getMyMonthlyService(id) });

  if (contractQuery.isPending) {
    return (
      <main className="flex w-full flex-col" aria-hidden>
        <div className="listing-banner h-[13.5rem] w-full sm:h-[15.5rem]" />
        <ScreenSkeleton cards={3} className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14" />
      </main>
    );
  }

  if (contractQuery.isError) {
    return (
      <main className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6">
        <Alert
          tone="error"
          title="Couldn't load this monthly service"
          action={
            <Button size="sm" variant="secondary" onClick={() => contractQuery.refetch()}>
              Retry
            </Button>
          }
        >
          {describeError(contractQuery.error)}
        </Alert>
      </main>
    );
  }

  const contract = contractQuery.data;
  return (
    <main className="flex w-full flex-col">
      <PageBanner
        title={contract.planName}
        description={`${describeDays(contract.days)} at ${formatClock(contract.visitStartTime)} · ${describeVisit(contract)}`}
        breadcrumb={
          <BannerBreadcrumb
            items={[{ label: "Home", href: "/" }, { label: "Monthly services", href: "/monthly-service" }, { label: contract.planName }]}
          />
        }
      />

      <div className="mx-auto grid w-full max-w-7xl grid-cols-1 gap-6 px-4 py-10 sm:px-6 sm:py-14 lg:grid-cols-3">
        <div className="flex flex-col gap-6 lg:col-span-2">
          <TodayCard contract={contract} />
          <AttendanceCalendar contract={contract} />
          <BillsCard contract={contract} />
        </div>
        <aside className="flex flex-col gap-6">
          <SummaryCard contract={contract} />
          {contract.status !== MonthlyServiceContractStatus.Cancelled ? <CancelCard contract={contract} /> : null}
        </aside>
      </div>
    </main>
  );
}

function TodayCard({ contract }: { contract: MonthlyServiceContract }) {
  if (contract.status === MonthlyServiceContractStatus.PendingAssignment) {
    return (
      <Alert tone="info" title="We're finding your professional">
        Our team is matching a verified professional to your schedule. You&apos;ll see their name here once assigned —
        nothing is charged until visits begin.
      </Alert>
    );
  }

  if (contract.status === MonthlyServiceContractStatus.Paused) {
    return (
      <Alert tone="warning" title="This service is paused">
        {contract.unpaidAmount > 0
          ? `Visits are on hold until the pending bill of ${inr(contract.unpaidAmount)} is paid. Paying resumes the service automatically.`
          : "Visits are on hold. Contact support to resume."}
      </Alert>
    );
  }

  const today = contract.today;
  if (!today) {
    return (
      <Card title="Today">
        <p className="text-sm text-fg-muted">No visit scheduled today.</p>
      </Card>
    );
  }

  return (
    <Card title="Today's visit" description={`${formatDay(today.date)} at ${formatClock(today.visitStartTime)}`}>
      <div className="flex flex-wrap items-center justify-between gap-4">
        {today.dayCode ? (
          <div>
            <p className="text-xs font-medium uppercase tracking-wide text-fg-subtle">Visit code</p>
            <p className="nums mt-1 text-4xl font-semibold tracking-[0.35em] text-brand-600 dark:text-brand-400">{today.dayCode}</p>
            <p className="mt-1 text-xs text-fg-muted">Tell this code to your professional when they arrive — it marks their attendance.</p>
          </div>
        ) : (
          <div>
            <Badge tone={attendanceTone[today.status]}>{attendanceLabel[today.status]}</Badge>
            {today.checkedInAtUtc ? (
              <p className="mt-2 text-sm text-fg-muted">
                Checked in at {new Date(today.checkedInAtUtc).toLocaleTimeString("en-IN", { hour: "numeric", minute: "2-digit" })}
                {today.checkedOutAtUtc
                  ? ` · left at ${new Date(today.checkedOutAtUtc).toLocaleTimeString("en-IN", { hour: "numeric", minute: "2-digit" })}`
                  : ""}
              </p>
            ) : null}
          </div>
        )}
        <DayActions item={today} contractId={contract.id} />
      </div>
    </Card>
  );
}

function SummaryCard({ contract }: { contract: MonthlyServiceContract }) {
  const m = contract.currentMonth;
  return (
    <Card title="This month" actions={<ContractStatusBadge contract={contract} />}>
      <div className="grid grid-cols-2 gap-4">
        <Stat label="Came" value={String(m.present)} />
        <Stat label="Skipped" value={String(m.customerSkipped)} />
        <Stat label="Their leave" value={String(m.providerLeave)} />
        <Stat label="Upcoming" value={String(m.upcoming)} />
      </div>
      <div className="mt-4 border-t border-line pt-4">
        <Stat label="Charged so far" value={inr(m.billableAmount)} hint={`${m.billableVisits} visit(s) × ${inr(contract.ratePerVisit)}`} />
      </div>

      <dl className="mt-4 flex flex-col gap-3 border-t border-line pt-4 text-sm">
        <div>
          <dt className="text-xs text-fg-subtle">Professional</dt>
          <dd className="font-medium text-fg">{contract.provider?.displayName ?? "Being assigned"}</dd>
        </div>
        {contract.address ? (
          <div>
            <dt className="text-xs text-fg-subtle">Address</dt>
            <dd className="text-fg">
              {contract.address.label} — {contract.address.line1}, {contract.address.city} {contract.address.pincode}
            </dd>
          </div>
        ) : null}
        <div>
          <dt className="text-xs text-fg-subtle">Started</dt>
          <dd className="text-fg">{formatDay(contract.startDate)}</dd>
        </div>
        {contract.includedTasks.length > 0 ? (
          <div>
            <dt className="text-xs text-fg-subtle">Included</dt>
            <dd className="text-fg">{contract.includedTasks.join(", ")}</dd>
          </div>
        ) : null}
      </dl>
    </Card>
  );
}

// ---- Calendar ----

function AttendanceCalendar({ contract }: { contract: MonthlyServiceContract }) {
  const now = new Date();
  const [cursor, setCursor] = useState({ year: now.getFullYear(), month: now.getMonth() + 1 });
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const query = useQuery({
    queryKey: ["my-monthly-attendance", contract.id, cursor.year, cursor.month],
    queryFn: () => getMonthlyAttendance(contract.id, cursor.year, cursor.month),
  });

  const shift = (delta: number) => {
    setSelectedId(null);
    setCursor((c) => {
      const d = new Date(c.year, c.month - 1 + delta, 1);
      return { year: d.getFullYear(), month: d.getMonth() + 1 };
    });
  };

  const selected = query.data?.items.find((i) => i.id === selectedId) ?? null;

  return (
    <Card
      title="Attendance"
      description="Tap a day to see details, skip an upcoming visit, or report a problem."
    >
      <div className="mb-4 flex items-center justify-between rounded-xl border border-line px-2 py-1">
        <Button size="sm" variant="ghost" aria-label="Previous month" onClick={() => shift(-1)}>
          ‹
        </Button>
        <span className="text-sm font-semibold text-fg">{monthTitle(cursor.year, cursor.month)}</span>
        <Button size="sm" variant="ghost" aria-label="Next month" onClick={() => shift(1)}>
          ›
        </Button>
      </div>
      {query.isPending ? (
        <div className="h-72 animate-pulse rounded-xl bg-surface-2" aria-hidden />
      ) : query.isError ? (
        <Alert tone="error">{describeError(query.error)}</Alert>
      ) : (
        <>
          <MonthGrid month={query.data} selectedId={selectedId} onSelect={setSelectedId} />
          <Legend />
          {selected ? (
            <div className="mt-4 rounded-xl border border-line bg-surface-2 p-4">
              <DayDetail item={selected} contractId={contract.id} />
            </div>
          ) : null}
          {query.data.items.length === 0 ? (
            <p className="mt-4 text-sm text-fg-muted">No visits in this month.</p>
          ) : null}
        </>
      )}
    </Card>
  );
}

function MonthGrid({
  month,
  selectedId,
  onSelect,
}: {
  month: MonthlyServiceAttendanceMonth;
  selectedId: string | null;
  onSelect: (id: string) => void;
}) {
  const cells = useMemo(() => {
    const first = new Date(month.year, month.month - 1, 1);
    const daysInMonth = new Date(month.year, month.month, 0).getDate();
    const leading = (first.getDay() + 6) % 7; // Monday-first
    const byDate = new Map(month.items.map((i) => [Number(i.date.slice(8, 10)), i]));
    const list: ({ day: number; item: MonthlyServiceAttendanceItem | undefined } | null)[] = Array(leading).fill(null);
    for (let day = 1; day <= daysInMonth; day++) list.push({ day, item: byDate.get(day) });
    return list;
  }, [month]);

  const todayKey = new Date().toDateString();

  return (
    <div>
      <div className="grid grid-cols-7 gap-1 text-center text-[0.6875rem] font-medium uppercase tracking-wide text-fg-subtle">
        {["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"].map((d) => (
          <div key={d} className="py-1">
            {d}
          </div>
        ))}
      </div>
      <div className="grid grid-cols-7 gap-1">
        {cells.map((cell, index) => {
          if (!cell) return <div key={`blank-${index}`} />;
          const isToday = new Date(month.year, month.month - 1, cell.day).toDateString() === todayKey;
          const item = cell.item;
          const base = "flex aspect-square flex-col items-center justify-center rounded-lg text-sm transition-colors";
          if (!item) {
            return (
              <div key={cell.day} className={cx(base, "text-fg-subtle", isToday && "ring-1 ring-brand-400")}>
                <span className="nums">{cell.day}</span>
              </div>
            );
          }
          return (
            <button
              key={cell.day}
              type="button"
              onClick={() => onSelect(item.id)}
              aria-pressed={selectedId === item.id}
              aria-label={`${formatDay(item.date)}: ${attendanceLabel[item.status]}`}
              className={cx(
                base,
                DAY_STYLE[item.status],
                "font-medium focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand-500",
                selectedId === item.id && "ring-2 ring-fg",
                isToday && selectedId !== item.id && "ring-2 ring-brand-500",
              )}
            >
              <span className="nums">{cell.day}</span>
              {item.disputeStatus === MonthlyServiceDisputeStatus.Open ? (
                <span className="mt-0.5 h-1 w-1 rounded-full bg-danger" aria-hidden />
              ) : null}
            </button>
          );
        })}
      </div>
    </div>
  );
}

const DAY_STYLE: Record<MonthlyServiceAttendanceStatus, string> = {
  [MonthlyServiceAttendanceStatus.Scheduled]: "bg-info-soft text-info hover:brightness-95",
  [MonthlyServiceAttendanceStatus.Present]: "bg-success text-white hover:brightness-110",
  [MonthlyServiceAttendanceStatus.CustomerSkipped]: "bg-surface-3 text-fg-muted line-through hover:brightness-95",
  [MonthlyServiceAttendanceStatus.ProviderLeave]: "bg-warning-soft text-warning hover:brightness-95",
  [MonthlyServiceAttendanceStatus.CustomerUnavailable]: "bg-accent-100 text-accent-700 hover:brightness-95 dark:bg-accent-500/15 dark:text-accent-300",
  [MonthlyServiceAttendanceStatus.Absent]: "bg-danger-soft text-danger hover:brightness-95",
};

function Legend() {
  const entries: [MonthlyServiceAttendanceStatus, string][] = [
    [MonthlyServiceAttendanceStatus.Present, "Came"],
    [MonthlyServiceAttendanceStatus.Scheduled, "Upcoming"],
    [MonthlyServiceAttendanceStatus.CustomerSkipped, "Skipped"],
    [MonthlyServiceAttendanceStatus.ProviderLeave, "Their leave"],
    [MonthlyServiceAttendanceStatus.Absent, "Did not come"],
  ];
  return (
    <ul className="mt-4 flex flex-wrap gap-x-4 gap-y-2 text-xs text-fg-muted">
      {entries.map(([status, label]) => (
        <li key={status} className="flex items-center gap-1.5">
          <span className={cx("h-3 w-3 rounded", DAY_STYLE[status])} aria-hidden />
          {label}
        </li>
      ))}
    </ul>
  );
}

function DayDetail({ item, contractId }: { item: MonthlyServiceAttendanceItem; contractId: string }) {
  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm font-semibold text-fg">
          {formatDay(item.date)} · {formatClock(item.visitStartTime)}
        </p>
        <Badge tone={attendanceTone[item.status]}>{attendanceLabel[item.status]}</Badge>
      </div>
      {item.note ? <p className="text-sm text-fg-muted">Note: {item.note}</p> : null}
      {item.disputeStatus !== MonthlyServiceDisputeStatus.None ? (
        <p className="text-sm text-fg-muted">
          Reported: {item.disputeReason}
          {item.disputeStatus === MonthlyServiceDisputeStatus.Open
            ? " — under review"
            : item.disputeStatus === MonthlyServiceDisputeStatus.Upheld
              ? " — accepted, not charged"
              : ` — reviewed${item.disputeResolutionNote ? `: ${item.disputeResolutionNote}` : ""}`}
        </p>
      ) : null}
      <p className="text-xs text-fg-subtle">
        {item.isBillable ? "Charged in this month's bill." : "Not charged."}
        {item.isInvoiced ? " Already billed." : ""}
      </p>
      <DayActions item={item} contractId={contractId} />
    </div>
  );
}

function DayActions({ item, contractId }: { item: MonthlyServiceAttendanceItem; contractId: string }) {
  const toast = useToast();
  const queryClient = useQueryClient();
  const [disputeOpen, setDisputeOpen] = useState(false);
  const [reason, setReason] = useState("");

  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: ["my-monthly-attendance", contractId] });
    queryClient.invalidateQueries({ queryKey: ["my-monthly-service", contractId] });
    queryClient.invalidateQueries({ queryKey: MY_MONTHLY_SERVICES_KEY });
  };

  const actMutation = useMutation({
    mutationFn: (action: "skip" | "unskip" | "confirm") => actOnAttendance(item.id, action),
    onSuccess: (updated) => {
      toast("success", `${formatDay(updated.date, false)}: ${attendanceLabel[updated.status]}.`);
      refresh();
    },
    onError: (error) => toast("error", describeError(error)),
  });

  const disputeMutation = useMutation({
    mutationFn: () => disputeAttendance(item.id, reason.trim()),
    onSuccess: () => {
      toast("success", "Thanks — we'll review this before your bill is made.");
      setDisputeOpen(false);
      setReason("");
      refresh();
    },
  });

  const can = (action: string) => item.allowedActions.includes(action);
  if (!item.allowedActions.length) return null;

  return (
    <div className="flex flex-wrap gap-2">
      {can(MONTHLY_SERVICE_ACTIONS.confirm) ? (
        <Button size="sm" loading={actMutation.isPending} onClick={() => actMutation.mutate("confirm")}>
          They came
        </Button>
      ) : null}
      {can(MONTHLY_SERVICE_ACTIONS.skip) ? (
        <Button size="sm" variant="secondary" loading={actMutation.isPending} onClick={() => actMutation.mutate("skip")}>
          Skip this day
        </Button>
      ) : null}
      {can(MONTHLY_SERVICE_ACTIONS.unskip) ? (
        <Button size="sm" variant="secondary" loading={actMutation.isPending} onClick={() => actMutation.mutate("unskip")}>
          Undo skip
        </Button>
      ) : null}
      {can(MONTHLY_SERVICE_ACTIONS.dispute) ? (
        <Button size="sm" variant="ghost" onClick={() => setDisputeOpen(true)}>
          Report a problem
        </Button>
      ) : null}

      <Modal
        open={disputeOpen}
        onClose={() => !disputeMutation.isPending && setDisputeOpen(false)}
        title={`Report ${formatDay(item.date, false)}`}
        description="Tell us what went wrong. Our team reviews it before the month's bill is made."
        size="sm"
        footer={
          <>
            <Button variant="secondary" disabled={disputeMutation.isPending} onClick={() => setDisputeOpen(false)}>
              Cancel
            </Button>
            <Button loading={disputeMutation.isPending} disabled={!reason.trim()} onClick={() => disputeMutation.mutate()}>
              Submit
            </Button>
          </>
        }
      >
        <div className="flex flex-col gap-3">
          <Textarea
            label="What happened?"
            placeholder="e.g. Did not come, or left after 20 minutes"
            maxLength={500}
            value={reason}
            onChange={(e) => setReason(e.target.value)}
          />
          {disputeMutation.isError ? <Alert tone="error">{describeError(disputeMutation.error)}</Alert> : null}
        </div>
      </Modal>
    </div>
  );
}

// ---- Bills ----

function BillsCard({ contract }: { contract: MonthlyServiceContract }) {
  const toast = useToast();
  const queryClient = useQueryClient();
  const query = useQuery({ queryKey: MY_MONTHLY_INVOICES_KEY, queryFn: listMyMonthlyInvoices });
  const bills = (query.data ?? []).filter((i) => i.contractId === contract.id);

  const payMutation = useMutation({
    mutationFn: (invoice: MonthlyServiceInvoice) => payMonthlyInvoice(invoice.id),
    onSuccess: (paid) => {
      toast("success", `Paid ${inr(paid.amount)}. Thank you!`);
      queryClient.invalidateQueries({ queryKey: MY_MONTHLY_INVOICES_KEY });
      queryClient.invalidateQueries({ queryKey: ["my-monthly-service", contract.id] });
      queryClient.invalidateQueries({ queryKey: MY_MONTHLY_SERVICES_KEY });
    },
    onError: (error) => toast("error", describeError(error)),
  });

  return (
    <Card title="Monthly bills" description="Made in the first days of each month, for the visits that happened.">
      {query.isPending ? (
        <div className="h-24 animate-pulse rounded-xl bg-surface-2" aria-hidden />
      ) : query.isError ? (
        <Alert tone="error">{describeError(query.error)}</Alert>
      ) : bills.length === 0 ? (
        <EmptyState title="No bills yet" description="Your first bill arrives at the start of next month." />
      ) : (
        <ul className="flex flex-col divide-y divide-line">
          {bills.map((bill) => (
            <li key={bill.id} className="flex flex-wrap items-center justify-between gap-3 py-3 first:pt-0 last:pb-0">
              <div className="min-w-0">
                <p className="text-sm font-semibold text-fg">{monthTitle(Number(bill.periodStart.slice(0, 4)), Number(bill.periodStart.slice(5, 7)))}</p>
                <p className="text-xs text-fg-muted">
                  {bill.billableVisits} visit(s) × {inr(bill.ratePerVisit)} · {bill.customerSkippedCount} skipped · {bill.providerLeaveCount} leave ·{" "}
                  {bill.absentCount} missed
                </p>
                <p className="text-xs text-fg-subtle">
                  {bill.status === MonthlyServiceInvoiceStatus.Paid && bill.paidAtUtc
                    ? `Paid on ${new Date(bill.paidAtUtc).toLocaleDateString("en-IN", { day: "numeric", month: "short" })}`
                    : `Due by ${formatDay(bill.dueDate)}`}
                </p>
              </div>
              <div className="flex items-center gap-3">
                <p className="nums text-base font-semibold text-fg">{inr(bill.amount)}</p>
                <InvoiceStatusBadge status={bill.status} />
                {bill.status !== MonthlyServiceInvoiceStatus.Paid ? (
                  <Button size="sm" loading={payMutation.isPending && payMutation.variables?.id === bill.id} onClick={() => payMutation.mutate(bill)}>
                    Pay now
                  </Button>
                ) : null}
              </div>
            </li>
          ))}
        </ul>
      )}
    </Card>
  );
}

// ---- Cancel ----

function CancelCard({ contract }: { contract: MonthlyServiceContract }) {
  const toast = useToast();
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const [reason, setReason] = useState("");

  const mutation = useMutation({
    mutationFn: () => cancelMyMonthlyService(contract.id, reason.trim() || null),
    onSuccess: () => {
      toast("success", "Your monthly service is cancelled.");
      setOpen(false);
      queryClient.invalidateQueries({ queryKey: ["my-monthly-service", contract.id] });
      queryClient.invalidateQueries({ queryKey: ["my-monthly-attendance", contract.id] });
      queryClient.invalidateQueries({ queryKey: MY_MONTHLY_SERVICES_KEY });
    },
  });

  return (
    <Card title="Stop this service">
      <p className="text-sm text-fg-muted">
        Upcoming visits are removed straight away. Visits that already happened this month are billed at month end.
      </p>
      <Button className="mt-4" variant="secondary" size="sm" onClick={() => setOpen(true)}>
        Cancel monthly service
      </Button>
      <Modal
        open={open}
        onClose={() => !mutation.isPending && setOpen(false)}
        title="Cancel this monthly service?"
        size="sm"
        footer={
          <>
            <Button variant="secondary" disabled={mutation.isPending} onClick={() => setOpen(false)}>
              Keep it
            </Button>
            <Button variant="danger" loading={mutation.isPending} onClick={() => mutation.mutate()}>
              Cancel service
            </Button>
          </>
        }
      >
        <div className="flex flex-col gap-3">
          <Textarea
            label="Reason (optional)"
            maxLength={500}
            value={reason}
            onChange={(e) => setReason(e.target.value)}
          />
          {mutation.isError ? <Alert tone="error">{describeError(mutation.error)}</Alert> : null}
        </div>
      </Modal>
    </Card>
  );
}
