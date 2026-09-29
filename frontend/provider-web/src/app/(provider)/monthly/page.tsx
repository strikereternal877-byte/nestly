"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ErrorState } from "@/components/states";
import { Alert, Badge, Button, Card, EmptyState, Field, Modal, PageHeading, Skeleton, Tabs, Textarea, useToast } from "@/components/ui";
import type { BadgeTone } from "@/components/ui";
import { describeError } from "@/lib/api";
import { formatInr } from "@/lib/format";
import {
  ATTENDANCE_LABEL,
  MonthlyServiceAttendanceStatus,
  MonthlyServiceContractStatus,
  MonthlyServiceInvoiceStatus,
  MonthlyServicePlanBasis,
  VISIT_ACTIONS,
  cancelLeave,
  checkInVisit,
  checkOutVisit,
  currentPosition,
  describeDays,
  formatClock,
  listMonthlyContracts,
  listMonthlyInvoices,
  listMonthlyVisits,
  markVisit,
} from "@/lib/monthly-service";
import type { MonthlyContract, MonthlyInvoice, MonthlyVisit } from "@/lib/monthly-service";

type Tab = "visits" | "clients" | "bills";

const VISITS_KEY = "monthly-visits";

const STATUS_TONE: Record<MonthlyServiceAttendanceStatus, BadgeTone> = {
  [MonthlyServiceAttendanceStatus.Scheduled]: "info",
  [MonthlyServiceAttendanceStatus.Present]: "success",
  [MonthlyServiceAttendanceStatus.CustomerSkipped]: "neutral",
  [MonthlyServiceAttendanceStatus.ProviderLeave]: "warning",
  [MonthlyServiceAttendanceStatus.CustomerUnavailable]: "accent",
  [MonthlyServiceAttendanceStatus.Absent]: "danger",
};

function isoDate(date: Date): string {
  const y = date.getFullYear();
  const m = String(date.getMonth() + 1).padStart(2, "0");
  const d = String(date.getDate()).padStart(2, "0");
  return `${y}-${m}-${d}`;
}

function dayLabel(iso: string): string {
  const today = isoDate(new Date());
  const tomorrow = isoDate(new Date(Date.now() + 86_400_000));
  if (iso === today) return "Today";
  if (iso === tomorrow) return "Tomorrow";
  const [y, m, d] = iso.split("-").map(Number);
  return new Date(y, m - 1, d).toLocaleDateString("en-IN", { weekday: "short", day: "numeric", month: "short" });
}

/**
 * Monthly clients (docs/MONTHLY-SERVICE.md): the professional's standing
 * home engagements. "Visits" is the working screen - one day at a time,
 * check in with the customer's 4-digit code, check out, report the customer
 * not being home, or mark leave ahead of time. "Clients" is the roster, and
 * "Bills" what each month earned.
 */
export default function MonthlyPage() {
  const [tab, setTab] = useState<Tab>("visits");
  return (
    <div className="flex flex-col gap-5">
      <PageHeading title="Monthly clients" subtitle="Homes you visit on fixed days. Check in with the customer's code every visit." />
      <Tabs<Tab>
        label="Monthly clients sections"
        value={tab}
        onChange={setTab}
        tabs={[
          { value: "visits", label: "Visits" },
          { value: "clients", label: "Clients" },
          { value: "bills", label: "Earnings" },
        ]}
      />
      {tab === "visits" ? <VisitsTab /> : tab === "clients" ? <ClientsTab /> : <BillsTab />}
    </div>
  );
}

// ---- Visits ----

function VisitsTab() {
  const [date, setDate] = useState(isoDate(new Date()));
  const query = useQuery({ queryKey: [VISITS_KEY, date], queryFn: () => listMonthlyVisits(date) });

  const shift = (days: number) => {
    const [y, m, d] = date.split("-").map(Number);
    setDate(isoDate(new Date(y, m - 1, d + days)));
  };

  return (
    <div className="flex flex-col gap-4">
      <div className="flex items-center justify-between rounded-2xl border border-line bg-surface px-2 py-1.5">
        <Button variant="ghost" size="sm" aria-label="Previous day" onClick={() => shift(-1)}>
          ‹
        </Button>
        <p className="text-sm font-semibold text-fg">{dayLabel(date)}</p>
        <Button variant="ghost" size="sm" aria-label="Next day" onClick={() => shift(1)}>
          ›
        </Button>
      </div>

      {query.isPending ? (
        <div className="flex flex-col gap-3" aria-hidden>
          <Skeleton className="h-40 rounded-2xl" />
          <Skeleton className="h-40 rounded-2xl" />
        </div>
      ) : query.isError ? (
        <ErrorState title="Couldn't load visits" error={query.error} onRetry={() => query.refetch()} isRetrying={query.isRefetching} />
      ) : query.data.length === 0 ? (
        <EmptyState title="No monthly visits" description="No home is expecting you on this day." />
      ) : (
        <ul className="flex list-none flex-col gap-3">
          {query.data.map((visit) => (
            <li key={visit.attendance.id}>
              <VisitCard visit={visit} />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function VisitCard({ visit }: { visit: MonthlyVisit }) {
  const toast = useToast();
  const queryClient = useQueryClient();
  const item = visit.attendance;
  const [checkInOpen, setCheckInOpen] = useState(false);
  const [noteAction, setNoteAction] = useState<"leave" | "customer-unavailable" | null>(null);
  const [code, setCode] = useState("");
  const [note, setNote] = useState("");

  const done = (message: string) => {
    toast("success", message);
    queryClient.invalidateQueries({ queryKey: [VISITS_KEY] });
    queryClient.invalidateQueries({ queryKey: ["monthly-contracts"] });
  };

  const checkInMutation = useMutation({
    mutationFn: async () => {
      const position = await currentPosition();
      return checkInVisit(item.id, code, position?.latitude ?? null, position?.longitude ?? null);
    },
    onSuccess: () => {
      setCheckInOpen(false);
      setCode("");
      done(`Checked in at ${visit.customerName}'s home.`);
    },
  });

  const noteMutation = useMutation({
    mutationFn: () => markVisit(item.id, noteAction!, note.trim() || null),
    onSuccess: () => {
      const wasLeave = noteAction === "leave";
      setNoteAction(null);
      setNote("");
      done(wasLeave ? "Leave marked. The customer can see it." : "Reported: customer not available.");
    },
  });

  const simpleMutation = useMutation({
    mutationFn: (action: "check-out" | "cancel-leave") => (action === "check-out" ? checkOutVisit(item.id) : cancelLeave(item.id)),
    onSuccess: (_, action) => done(action === "check-out" ? "Checked out." : "Leave cancelled."),
    onError: (error) => toast("error", describeError(error)),
  });

  const can = (action: string) => item.allowedActions.includes(action);
  const address = visit.address;

  return (
    <Card>
      <div className="flex flex-col gap-4">
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0">
            <p className="truncate text-lg font-semibold text-fg">{visit.customerName}</p>
            <p className="nums text-sm text-fg-muted">
              {formatClock(item.visitStartTime)} ·{" "}
              {visit.basis === MonthlyServicePlanBasis.Hourly && visit.hoursPerVisit ? `${visit.hoursPerVisit} h` : visit.planName}
            </p>
          </div>
          <Badge tone={STATUS_TONE[item.status]}>{ATTENDANCE_LABEL[item.status]}</Badge>
        </div>

        {address ? (
          <div className="text-sm text-fg-muted">
            <p>
              {address.line1}
              {address.line2 ? `, ${address.line2}` : ""}
              {address.landmark ? ` (near ${address.landmark})` : ""}, {address.city} {address.pincode}
            </p>
            <a href={`tel:${address.contactMobile}`} className="mt-1 inline-block font-medium text-brand-600 dark:text-brand-400">
              Call {address.contactName}
            </a>
          </div>
        ) : null}

        {visit.includedTasks.length > 0 ? (
          <ul className="flex flex-wrap gap-1.5">
            {visit.includedTasks.map((task) => (
              <li key={task} className="rounded-full bg-surface-2 px-2.5 py-1 text-xs text-fg-muted">
                {task}
              </li>
            ))}
          </ul>
        ) : null}

        {item.checkedInAtUtc ? (
          <p className="text-xs text-fg-subtle">
            In at {new Date(item.checkedInAtUtc).toLocaleTimeString("en-IN", { hour: "numeric", minute: "2-digit" })}
            {item.checkedOutAtUtc
              ? ` · out at ${new Date(item.checkedOutAtUtc).toLocaleTimeString("en-IN", { hour: "numeric", minute: "2-digit" })}`
              : ""}
          </p>
        ) : null}

        <div className="flex flex-wrap gap-2">
          {can(VISIT_ACTIONS.checkIn) ? (
            <Button onClick={() => setCheckInOpen(true)}>Check in</Button>
          ) : null}
          {can(VISIT_ACTIONS.checkOut) ? (
            <Button loading={simpleMutation.isPending} onClick={() => simpleMutation.mutate("check-out")}>
              Check out
            </Button>
          ) : null}
          {can(VISIT_ACTIONS.customerUnavailable) ? (
            <Button variant="secondary" onClick={() => setNoteAction("customer-unavailable")}>
              Customer not home
            </Button>
          ) : null}
          {can(VISIT_ACTIONS.leave) ? (
            <Button variant="secondary" onClick={() => setNoteAction("leave")}>
              Mark leave
            </Button>
          ) : null}
          {can(VISIT_ACTIONS.cancelLeave) ? (
            <Button variant="secondary" loading={simpleMutation.isPending} onClick={() => simpleMutation.mutate("cancel-leave")}>
              Cancel leave
            </Button>
          ) : null}
        </div>
      </div>

      <Modal
        open={checkInOpen}
        onClose={() => !checkInMutation.isPending && setCheckInOpen(false)}
        title="Check in"
        description={`Ask ${visit.customerName} for today's 4-digit visit code.`}
        size="sm"
        footer={
          <>
            <Button variant="secondary" disabled={checkInMutation.isPending} onClick={() => setCheckInOpen(false)}>
              Cancel
            </Button>
            <Button loading={checkInMutation.isPending} disabled={code.length !== 4} onClick={() => checkInMutation.mutate()}>
              Check in
            </Button>
          </>
        }
      >
        <div className="flex flex-col gap-3">
          <Field
            label="Visit code"
            inputMode="numeric"
            autoComplete="one-time-code"
            maxLength={4}
            placeholder="••••"
            className="nums text-center text-2xl tracking-[0.5em]"
            value={code}
            onChange={(e) => setCode(e.target.value.replace(/\D/g, "").slice(0, 4))}
          />
          {checkInMutation.isError ? <Alert tone="error">{describeError(checkInMutation.error)}</Alert> : null}
        </div>
      </Modal>

      <Modal
        open={noteAction !== null}
        onClose={() => !noteMutation.isPending && setNoteAction(null)}
        title={noteAction === "leave" ? `Leave on ${dayLabel(item.date)}` : "Customer not available"}
        description={
          noteAction === "leave"
            ? "The customer is told you won't come. Leave days are not paid."
            : "Use this only if you reached the home and nobody was available."
        }
        size="sm"
        footer={
          <>
            <Button variant="secondary" disabled={noteMutation.isPending} onClick={() => setNoteAction(null)}>
              Back
            </Button>
            <Button loading={noteMutation.isPending} onClick={() => noteMutation.mutate()}>
              Confirm
            </Button>
          </>
        }
      >
        <div className="flex flex-col gap-3">
          <Textarea label="Note (optional)" maxLength={500} value={note} onChange={(e) => setNote(e.target.value)} />
          {noteMutation.isError ? <Alert tone="error">{describeError(noteMutation.error)}</Alert> : null}
        </div>
      </Modal>
    </Card>
  );
}

// ---- Clients ----

function ClientsTab() {
  const query = useQuery({ queryKey: ["monthly-contracts"], queryFn: listMonthlyContracts });
  if (query.isPending) return <Skeleton className="h-48 rounded-2xl" />;
  if (query.isError) return <ErrorState title="Couldn't load clients" error={query.error} onRetry={() => query.refetch()} isRetrying={query.isRefetching} />;
  if (query.data.length === 0)
    return <EmptyState title="No monthly clients yet" description="When the team assigns you a monthly home, it appears here." />;

  return (
    <ul className="flex list-none flex-col gap-3">
      {query.data.map((contract) => (
        <li key={contract.id}>
          <ClientCard contract={contract} />
        </li>
      ))}
    </ul>
  );
}

function ClientCard({ contract }: { contract: MonthlyContract }) {
  const m = contract.currentMonth;
  return (
    <Card>
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="truncate text-base font-semibold text-fg">{contract.customerName}</p>
          <p className="text-sm text-fg-muted">
            {describeDays(contract.days)} · {formatClock(contract.visitStartTime)} · {contract.planName}
          </p>
        </div>
        <Badge tone={contract.status === MonthlyServiceContractStatus.Active ? "success" : "warning"}>
          {contract.status === MonthlyServiceContractStatus.Active ? "Active" : "Paused"}
        </Badge>
      </div>
      {contract.customerNote ? <p className="mt-3 rounded-lg bg-surface-2 px-3 py-2 text-sm text-fg-muted">“{contract.customerNote}”</p> : null}
      <dl className="mt-4 grid grid-cols-2 gap-3 border-t border-line pt-4 text-sm sm:grid-cols-4">
        <Metric label="Present" value={String(m.present)} />
        <Metric label="Leave" value={String(m.providerLeave)} />
        <Metric label="Per visit (you)" value={formatInr(contract.netPerVisit)} />
        <Metric label="This month so far" value={formatInr(m.billableAmount)} />
      </dl>
    </Card>
  );
}

function Metric({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-xs text-fg-subtle">{label}</dt>
      <dd className="nums font-semibold text-fg">{value}</dd>
    </div>
  );
}

// ---- Earnings ----

function BillsTab() {
  const query = useQuery({ queryKey: ["monthly-invoices"], queryFn: listMonthlyInvoices });
  if (query.isPending) return <Skeleton className="h-48 rounded-2xl" />;
  if (query.isError) return <ErrorState title="Couldn't load earnings" error={query.error} onRetry={() => query.refetch()} isRetrying={query.isRefetching} />;
  if (query.data.length === 0)
    return <EmptyState title="No monthly bills yet" description="Each month's bill is made in the first days of the next month." />;

  return (
    <Card flush>
      <ul className="flex flex-col divide-y divide-line">
        {query.data.map((bill) => (
          <BillRow key={bill.id} bill={bill} />
        ))}
      </ul>
      <p className="px-5 py-3 text-xs text-fg-subtle">
        Your share is added to Earnings once the customer pays, and goes out with your regular payout.
      </p>
    </Card>
  );
}

function BillRow({ bill }: { bill: MonthlyInvoice }) {
  const [y, m] = bill.periodStart.split("-").map(Number);
  const paid = bill.status === MonthlyServiceInvoiceStatus.Paid;
  return (
    <li className="flex flex-wrap items-center justify-between gap-3 px-5 py-4">
      <div className="min-w-0">
        <p className="text-sm font-semibold text-fg">
          {bill.customerName} · {new Date(y, m - 1, 1).toLocaleDateString("en-IN", { month: "long", year: "numeric" })}
        </p>
        <p className="text-xs text-fg-muted">
          {bill.billableVisits} visit(s) · {bill.providerLeaveCount} leave · {bill.absentCount} absent
        </p>
      </div>
      <div className="text-right">
        <p className="nums text-base font-semibold text-fg">{formatInr(bill.providerNetAmount)}</p>
        <Badge tone={paid ? "success" : "warning"}>{paid ? "Paid to your earnings" : "Awaiting customer payment"}</Badge>
      </div>
    </li>
  );
}
