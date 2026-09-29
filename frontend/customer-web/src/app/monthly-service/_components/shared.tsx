import { Badge } from "@/components/ui";
import type { BadgeTone } from "@/components/ui";
import {
  MonthlyServiceAttendanceStatus,
  MonthlyServiceContractStatus,
  MonthlyServiceInvoiceStatus,
  MonthlyServicePauseReason,
} from "@/lib/monthly-service";
import type { MonthlyServiceContract } from "@/lib/monthly-service";

export const MY_MONTHLY_SERVICES_KEY = ["my-monthly-services"] as const;
export const MY_MONTHLY_INVOICES_KEY = ["my-monthly-invoices"] as const;

export function ContractStatusBadge({ contract }: { contract: Pick<MonthlyServiceContract, "status" | "pauseReason"> }) {
  switch (contract.status) {
    case MonthlyServiceContractStatus.PendingAssignment:
      return <Badge tone="warning">Finding your professional</Badge>;
    case MonthlyServiceContractStatus.Active:
      return <Badge tone="success">Active</Badge>;
    case MonthlyServiceContractStatus.Paused:
      return (
        <Badge tone="danger">
          {contract.pauseReason === MonthlyServicePauseReason.OverdueInvoice ? "Paused — payment due" : "Paused"}
        </Badge>
      );
    default:
      return <Badge tone="neutral">Cancelled</Badge>;
  }
}

export const attendanceTone: Record<MonthlyServiceAttendanceStatus, BadgeTone> = {
  [MonthlyServiceAttendanceStatus.Scheduled]: "info",
  [MonthlyServiceAttendanceStatus.Present]: "success",
  [MonthlyServiceAttendanceStatus.CustomerSkipped]: "neutral",
  [MonthlyServiceAttendanceStatus.ProviderLeave]: "warning",
  [MonthlyServiceAttendanceStatus.CustomerUnavailable]: "accent",
  [MonthlyServiceAttendanceStatus.Absent]: "danger",
};

export function InvoiceStatusBadge({ status }: { status: MonthlyServiceInvoiceStatus }) {
  switch (status) {
    case MonthlyServiceInvoiceStatus.Paid:
      return <Badge tone="success">Paid</Badge>;
    case MonthlyServiceInvoiceStatus.Overdue:
      return <Badge tone="danger">Overdue</Badge>;
    default:
      return <Badge tone="warning">Due</Badge>;
  }
}

/** "2026-10-05" -> "5 Oct 2026", without timezone shifting (it is a calendar date, not an instant). */
export function formatDay(isoDate: string, withYear = true): string {
  const [y, m, d] = isoDate.split("-").map(Number);
  return new Date(y, m - 1, d).toLocaleDateString("en-IN", {
    day: "numeric",
    month: "short",
    ...(withYear ? { year: "numeric" } : {}),
  });
}

export function monthTitle(year: number, month: number): string {
  return new Date(year, month - 1, 1).toLocaleDateString("en-IN", { month: "long", year: "numeric" });
}

export function Stat({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <div className="min-w-0">
      <p className="text-[0.6875rem] font-medium uppercase tracking-wide text-fg-subtle">{label}</p>
      <p className="nums mt-0.5 truncate text-sm font-semibold text-fg">{value}</p>
      {hint ? <p className="mt-0.5 text-xs text-fg-muted">{hint}</p> : null}
    </div>
  );
}
