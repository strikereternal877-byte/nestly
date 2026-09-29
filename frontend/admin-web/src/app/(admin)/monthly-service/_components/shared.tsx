"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { NavTabs } from "@/components/nav-tabs";
import { formatCurrency } from "@/components/data-table";
import { Alert, Badge, Button, Field, Modal, Select, useToast } from "@/components/ui";
import type { BadgeTone } from "@/components/ui";
import { describeError } from "@/lib/api";
import {
  CONTRACT_STATUS_LABELS,
  INVOICE_STATUS_LABELS,
  MonthlyServiceAttendanceStatus,
  MonthlyServiceContractStatus,
  MonthlyServiceInvoiceStatus,
  MonthlyServicePauseReason,
  MonthlyServicePaymentMethod,
  PAYMENT_METHOD_LABELS,
  formatPeriod,
  recordMonthlyPayment,
} from "../_lib/monthly-service-api";
import type { MonthlyServiceInvoice } from "../_lib/monthly-service-api";

export const MONTHLY_KEYS = {
  contracts: ["admin-monthly-contracts"] as const,
  contract: (id: string) => ["admin-monthly-contract", id] as const,
  attendance: (id: string) => ["admin-monthly-attendance", id] as const,
  disputes: ["admin-monthly-disputes"] as const,
  invoices: ["admin-monthly-invoices"] as const,
  plans: ["admin-monthly-plans"] as const,
};

export function MonthlyServiceTabs() {
  return (
    <NavTabs
      label="Monthly service sections"
      tabs={[
        { href: "/monthly-service", label: "Engagements" },
        { href: "/monthly-service/disputes", label: "Disputes" },
        { href: "/monthly-service/invoices", label: "Invoices" },
        { href: "/monthly-service/plans", label: "Plans" },
      ]}
    />
  );
}

const CONTRACT_TONE: Record<MonthlyServiceContractStatus, BadgeTone> = {
  [MonthlyServiceContractStatus.PendingAssignment]: "warning",
  [MonthlyServiceContractStatus.Active]: "success",
  [MonthlyServiceContractStatus.Paused]: "danger",
  [MonthlyServiceContractStatus.Cancelled]: "neutral",
};

export function ContractStatusBadge({ status, pauseReason }: { status: MonthlyServiceContractStatus; pauseReason: MonthlyServicePauseReason | null }) {
  const label =
    status === MonthlyServiceContractStatus.Paused && pauseReason === MonthlyServicePauseReason.OverdueInvoice
      ? "Paused — unpaid"
      : CONTRACT_STATUS_LABELS[status];
  return <Badge tone={CONTRACT_TONE[status]}>{label}</Badge>;
}

export const ATTENDANCE_TONE: Record<MonthlyServiceAttendanceStatus, BadgeTone> = {
  [MonthlyServiceAttendanceStatus.Scheduled]: "info",
  [MonthlyServiceAttendanceStatus.Present]: "success",
  [MonthlyServiceAttendanceStatus.CustomerSkipped]: "neutral",
  [MonthlyServiceAttendanceStatus.ProviderLeave]: "warning",
  [MonthlyServiceAttendanceStatus.CustomerUnavailable]: "accent",
  [MonthlyServiceAttendanceStatus.Absent]: "danger",
};

const INVOICE_TONE: Record<MonthlyServiceInvoiceStatus, BadgeTone> = {
  [MonthlyServiceInvoiceStatus.Issued]: "warning",
  [MonthlyServiceInvoiceStatus.Overdue]: "danger",
  [MonthlyServiceInvoiceStatus.Paid]: "success",
};

export function InvoiceStatusBadge({ status }: { status: MonthlyServiceInvoiceStatus }) {
  return <Badge tone={INVOICE_TONE[status]}>{INVOICE_STATUS_LABELS[status]}</Badge>;
}

const OFFLINE_METHODS = [MonthlyServicePaymentMethod.Cash, MonthlyServicePaymentMethod.Upi, MonthlyServicePaymentMethod.BankTransfer];

/** Records a payment collected outside the app (cash / UPI / bank transfer) - docs/MONTHLY-SERVICE.md BILLING. */
export function RecordPaymentModal({ invoice, onClose }: { invoice: MonthlyServiceInvoice | null; onClose: () => void }) {
  const toast = useToast();
  const queryClient = useQueryClient();
  const [method, setMethod] = useState(MonthlyServicePaymentMethod.Upi);
  const [reference, setReference] = useState("");

  useEffect(() => {
    if (invoice) {
      setMethod(MonthlyServicePaymentMethod.Upi);
      setReference("");
    }
  }, [invoice]);

  const mutation = useMutation({
    mutationFn: () => recordMonthlyPayment(invoice!.id, method, reference.trim() || null),
    onSuccess: (paid) => {
      toast("success", `${formatCurrency(paid.amount)} recorded for ${paid.customerName}.`);
      void queryClient.invalidateQueries({ queryKey: MONTHLY_KEYS.invoices });
      void queryClient.invalidateQueries({ queryKey: MONTHLY_KEYS.contract(paid.contractId) });
      void queryClient.invalidateQueries({ queryKey: MONTHLY_KEYS.contracts });
      onClose();
    },
  });

  return (
    <Modal
      open={invoice !== null}
      onClose={() => !mutation.isPending && onClose()}
      title="Record payment"
      description={
        invoice
          ? `${invoice.customerName} · ${formatPeriod(invoice.periodStart)} · ${formatCurrency(invoice.amount)}. The professional's share is credited to their earnings.`
          : undefined
      }
      size="sm"
      footer={
        <>
          <Button variant="secondary" disabled={mutation.isPending} onClick={onClose}>
            Cancel
          </Button>
          <Button loading={mutation.isPending} onClick={() => mutation.mutate()}>
            Mark as paid
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        <Select
          label="Collected via"
          value={String(method)}
          onChange={(e) => setMethod(Number(e.target.value) as MonthlyServicePaymentMethod)}
          options={OFFLINE_METHODS.map((m) => ({ value: String(m), label: PAYMENT_METHOD_LABELS[m] }))}
        />
        <Field
          label="Reference (optional)"
          placeholder="UPI transaction id, receipt number…"
          maxLength={100}
          value={reference}
          onChange={(e) => setReference(e.target.value)}
        />
        {mutation.isError ? <Alert tone="error">{describeError(mutation.error)}</Alert> : null}
      </div>
    </Modal>
  );
}

const KPI_ICON = {
  viewBox: "0 0 24 24",
  fill: "none",
  stroke: "currentColor",
  strokeWidth: "1.75",
  strokeLinecap: "round",
  strokeLinejoin: "round",
  className: "h-5 w-5",
  "aria-hidden": true,
} as const;

export const KpiIcons = {
  present: (
    <svg {...KPI_ICON}>
      <circle cx="12" cy="12" r="9" />
      <path d="m8 12.5 2.5 2.5L16 9.5" />
    </svg>
  ),
  away: (
    <svg {...KPI_ICON}>
      <circle cx="12" cy="12" r="9" />
      <path d="M9 9l6 6M15 9l-6 6" />
    </svg>
  ),
  upcoming: (
    <svg {...KPI_ICON}>
      <rect x="4" y="5" width="16" height="15" rx="2" />
      <path d="M8 3v4M16 3v4M4 10h16" />
    </svg>
  ),
  money: (
    <svg {...KPI_ICON}>
      <path d="M7 5h10M7 9h10M13 5c2.5 0 3.5 2 3.5 4S14.5 13 12 13H7l7 6" />
    </svg>
  ),
};
