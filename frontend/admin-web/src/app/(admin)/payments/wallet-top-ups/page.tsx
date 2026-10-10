"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useEffect, useState } from "react";
import { Alert, Badge, Button, Field, PageHeading, Select, cx } from "@/components/ui";
import type { BadgeTone } from "@/components/ui";
import { useResetOnChange } from "@/hooks/useResetOnChange";
import {
  ConfirmDialog,
  DataTable,
  ExportCsvButton,
  Pagination,
  formatCurrency,
  formatDateTime,
} from "@/components/data-table";
import type { CsvColumn, DataTableColumn } from "@/components/data-table";
import { PaymentsTabs } from "@/components/PaymentsTabs";
import { describeError } from "@/lib/api";
import { todayIsoDate } from "@/lib/date";
import { reconcileWalletTopUp, searchWalletTopUps } from "@/lib/payments-api";
import {
  AdminWalletTopUpAttention,
  WalletTopUpReconcileOutcome,
  WalletTopUpStatus,
} from "@/lib/payments-types";
import type { AdminWalletTopUp, AdminWalletTopUpReconcileResponse } from "@/lib/payments-types";
import { useAdminClaims } from "@/lib/use-admin-claims";

const PAGE_SIZE = 20;

const STATUS_LABELS: Record<WalletTopUpStatus, string> = {
  [WalletTopUpStatus.Pending]: "Pending",
  [WalletTopUpStatus.Success]: "Credited",
  [WalletTopUpStatus.Failed]: "Failed",
};

const STATUS_TONES: Record<WalletTopUpStatus, BadgeTone> = {
  [WalletTopUpStatus.Pending]: "warning",
  [WalletTopUpStatus.Success]: "success",
  [WalletTopUpStatus.Failed]: "danger",
};

const ATTENTION_LABELS: Record<AdminWalletTopUpAttention, string> = {
  [AdminWalletTopUpAttention.None]: "",
  [AdminWalletTopUpAttention.Stuck]: "Stuck",
  [AdminWalletTopUpAttention.NeedsReview]: "Needs review",
};

const ATTENTION_TONES: Record<AdminWalletTopUpAttention, BadgeTone> = {
  [AdminWalletTopUpAttention.None]: "neutral",
  [AdminWalletTopUpAttention.Stuck]: "warning",
  [AdminWalletTopUpAttention.NeedsReview]: "danger",
};

const STATUS_OPTIONS = [
  { value: "", label: "Any status" },
  { value: String(WalletTopUpStatus.Pending), label: "Pending" },
  { value: String(WalletTopUpStatus.Success), label: "Credited" },
  { value: String(WalletTopUpStatus.Failed), label: "Failed" },
];

const CSV_COLUMNS: readonly CsvColumn<AdminWalletTopUp>[] = [
  { header: "Created", value: (item) => item.createdAtUtc },
  { header: "Customer", value: (item) => item.customerName },
  { header: "Mobile", value: (item) => item.customerMobile },
  { header: "Amount", value: (item) => item.amount },
  { header: "Currency", value: (item) => item.currency },
  { header: "Status", value: (item) => STATUS_LABELS[item.status] },
  { header: "Attention", value: (item) => ATTENTION_LABELS[item.attention] },
  { header: "Gateway order id", value: (item) => item.gatewayOrderId },
  { header: "Gateway payment ref", value: (item) => item.gatewayPaymentRef ?? "" },
  { header: "Failure reason", value: (item) => item.failureReason ?? "" },
];

/** "3h 12m" / "45m" - coarse on purpose: this list is checked periodically, not watched live. */
function formatAge(totalMinutes: number): string {
  if (totalMinutes < 60) return `${totalMinutes}m`;
  const hours = Math.floor(totalMinutes / 60);
  const minutes = totalMinutes % 60;
  if (hours < 24) return minutes === 0 ? `${hours}h` : `${hours}h ${minutes}m`;
  const days = Math.floor(hours / 24);
  const remainingHours = hours % 24;
  return remainingHours === 0 ? `${days}d` : `${days}d ${remainingHours}h`;
}

/** What "Reconcile now" did, in words - the money outcome first, because that is what the admin is chasing. */
function describeOutcome({ outcome, topUp }: AdminWalletTopUpReconcileResponse): { tone: "success" | "info" | "warning"; text: string } {
  const who = `${topUp.customerName}'s ${topUp.currency} ${formatCurrency(topUp.amount)} top-up`;
  switch (outcome) {
    case WalletTopUpReconcileOutcome.Credited:
      return { tone: "success", text: `The gateway confirmed the payment: ${who} was credited to the wallet.` };
    case WalletTopUpReconcileOutcome.MarkedFailed:
      return { tone: "warning", text: `The gateway says the payment did not go through: ${who} is marked failed and nothing was credited.` };
    case WalletTopUpReconcileOutcome.StillPending:
      return { tone: "info", text: `The gateway has no final answer for ${who} yet (or could not be reached). Nothing changed - try again shortly, or check the gateway dashboard.` };
    default:
      return { tone: "info", text: `Nothing to apply for ${who}: it is already ${STATUS_LABELS[topUp.status].toLowerCase()} and the gateway agrees.` };
  }
}

/**
 * Wallet top-ups (customers adding money to their wallet through the gateway) - `WalletTopUpsController`. The list
 * exists so a top-up that goes wrong is visible to a person instead of only to a log line:
 *
 * - **Stuck** - still pending long after the background sweep should have resolved it. "Reconcile now" asks the
 *   gateway directly and applies a definite answer.
 * - **Needs review** - a gateway callback disagreed with the amount that was asked for. Nothing is credited; decide
 *   with the gateway dashboard, then use the customer's wallet adjustment if money really is owed.
 * - **Credited in the last 24 hours** - the total to set against the gateway's own settlement report each day.
 *
 * Reconcile cannot invent a credit: it runs the same check, behind the same once-only update, as the sweep.
 */
export default function WalletTopUpsPage() {
  const claims = useAdminClaims();
  const canReconcile = claims?.permissions.includes("payments.write") ?? false;
  const queryClient = useQueryClient();

  const [page, setPage] = useState(1);
  const [statusFilter, setStatusFilter] = useState("");
  const [needsAttentionOnly, setNeedsAttentionOnly] = useState(false);
  const [search, setSearch] = useState("");
  const [debouncedSearch, setDebouncedSearch] = useState("");
  const [reconciling, setReconciling] = useState<AdminWalletTopUp | null>(null);
  const [notice, setNotice] = useState<{ tone: "success" | "info" | "warning"; text: string } | null>(null);

  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedSearch(search.trim()), 300);
    return () => window.clearTimeout(handle);
  }, [search]);

  // Any filter change resets to page 1 - staying on page 3 of a now-smaller result set would show an empty page.
  useResetOnChange([statusFilter, needsAttentionOnly, debouncedSearch], () => setPage(1));

  const query = useQuery({
    queryKey: ["admin-wallet-top-ups", page, statusFilter, needsAttentionOnly, debouncedSearch] as const,
    queryFn: () =>
      searchWalletTopUps({
        status: statusFilter === "" ? undefined : (Number(statusFilter) as WalletTopUpStatus),
        needsAttention: needsAttentionOnly ? true : undefined,
        search: debouncedSearch || undefined,
        page,
        pageSize: PAGE_SIZE,
      }),
    placeholderData: keepPreviousData,
  });

  const reconcileMutation = useMutation({
    mutationFn: (topUp: AdminWalletTopUp) => reconcileWalletTopUp(topUp.id),
    onSuccess: (response) => {
      setReconciling(null);
      setNotice(describeOutcome(response));
      queryClient.invalidateQueries({ queryKey: ["admin-wallet-top-ups"] });
      // A credit changes the customer's balance, which their page shows.
      queryClient.invalidateQueries({ queryKey: ["admin-customer-detail"] });
    },
  });

  const columns: DataTableColumn<AdminWalletTopUp>[] = [
    {
      key: "created",
      header: "Created",
      sortValue: (item) => item.createdAtUtc,
      cell: (item) => (
        <>
          <div className="nums">{formatDateTime(item.createdAtUtc)}</div>
          {item.ageMinutes !== null ? (
            <div className="mt-0.5 text-xs text-fg-subtle">open for {formatAge(item.ageMinutes)}</div>
          ) : null}
        </>
      ),
    },
    {
      key: "customer",
      header: "Customer",
      cell: (item) => (
        <>
          <Link
            href={`/customers/${item.customerId}`}
            className="font-medium text-fg underline-offset-4 hover:text-brand-600 hover:underline dark:hover:text-brand-400"
          >
            {item.customerName}
          </Link>
          <div className="nums mt-0.5 text-xs text-fg-subtle">{item.customerMobile}</div>
        </>
      ),
    },
    {
      key: "amount",
      header: "Amount",
      numeric: true,
      sortValue: (item) => item.amount,
      cell: (item) => (
        <span className="nums">
          {item.currency} {formatCurrency(item.amount)}
        </span>
      ),
    },
    {
      key: "status",
      header: "Status",
      cell: (item) => (
        <>
          <Badge tone={STATUS_TONES[item.status]}>{STATUS_LABELS[item.status]}</Badge>
          {item.status === WalletTopUpStatus.Failed && item.failureReason ? (
            <p className="mt-1 max-w-[16rem] text-xs text-fg-subtle">{item.failureReason}</p>
          ) : null}
        </>
      ),
    },
    {
      key: "attention",
      header: "Needs attention",
      cell: (item) =>
        item.attention === AdminWalletTopUpAttention.None ? (
          <span className="text-fg-subtle">—</span>
        ) : (
          <div>
            <Badge tone={ATTENTION_TONES[item.attention]}>{ATTENTION_LABELS[item.attention]}</Badge>
            {item.attentionReason ? <p className="mt-1 max-w-[20rem] text-xs text-fg-subtle">{item.attentionReason}</p> : null}
          </div>
        ),
    },
    {
      key: "gateway",
      header: "Gateway",
      cell: (item) => (
        <>
          <div className="break-all font-mono text-xs text-fg">{item.gatewayOrderId}</div>
          {item.gatewayPaymentRef ? (
            <div className="mt-0.5 break-all font-mono text-xs text-fg-subtle">ref {item.gatewayPaymentRef}</div>
          ) : null}
        </>
      ),
    },
    {
      key: "actions",
      header: "",
      hiddenHeader: true,
      cell: (item) => {
        // A Failed top-up is offered too: a payment that arrives after it was written off still has to credit.
        const reconcilable = item.status === WalletTopUpStatus.Pending || item.status === WalletTopUpStatus.Failed;
        if (!reconcilable) {
          return <span className="text-fg-subtle">—</span>;
        }

        return canReconcile ? (
          <Button size="sm" variant="secondary" onClick={() => setReconciling(item)}>
            Reconcile now
          </Button>
        ) : (
          <span className="text-xs text-fg-subtle">Needs payments.write</span>
        );
      },
    },
  ];

  const summary = query.data;
  const filtersActive = statusFilter !== "" || needsAttentionOnly || search !== "";

  return (
    <div className="w-full max-w-7xl">
      <PageHeading
        title="Wallet Top-ups"
        subtitle="Money customers add to their wallet through the payment gateway - stuck and mismatched top-ups surface here, newest first."
      />

      <PaymentsTabs />

      {notice ? (
        <div className="mt-4">
          <Alert tone={notice.tone}>{notice.text}</Alert>
        </div>
      ) : null}
      {!canReconcile ? (
        <div className="mt-4">
          <Alert tone="info">
            You can review top-ups but not reconcile one - that needs the &quot;payments.write&quot; permission
            (Finance Admin or Super Admin).
          </Alert>
        </div>
      ) : null}

      {summary ? (
        <div className="mt-6 flex flex-wrap items-center gap-3">
          <SummaryChip
            tone="warning"
            label={`${summary.pendingCount} pending`}
            active={statusFilter === String(WalletTopUpStatus.Pending)}
            onClick={() =>
              setStatusFilter((current) => (current === String(WalletTopUpStatus.Pending) ? "" : String(WalletTopUpStatus.Pending)))
            }
          />
          <SummaryChip
            tone={summary.needsReviewCount > 0 ? "danger" : "warning"}
            label={`${summary.stuckCount} stuck · ${summary.needsReviewCount} need review`}
            active={needsAttentionOnly}
            onClick={() => setNeedsAttentionOnly((current) => !current)}
          />
          <Badge tone="success">
            Credited in the last 24 hours:{" "}
            <span className="nums">
              {formatCurrency(summary.creditedLast24HoursAmount)} ({summary.creditedLast24HoursCount})
            </span>
          </Badge>
          <span className="text-xs text-fg-subtle">Set this against the gateway&apos;s own settlement report.</span>
        </div>
      ) : null}

      <div className="mt-4 flex flex-wrap items-end gap-3">
        <div className="w-44">
          <Select label="Status" options={STATUS_OPTIONS} value={statusFilter} onChange={(e) => setStatusFilter(e.target.value)} />
        </div>
        <div className="max-w-xs flex-1">
          <Field
            label="Search"
            name="search"
            placeholder="Customer name, mobile or gateway order id"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
        </div>
        {filtersActive ? (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={() => {
              setSearch("");
              setStatusFilter("");
              setNeedsAttentionOnly(false);
            }}
          >
            Clear filters
          </Button>
        ) : null}
      </div>

      <div className="mt-4">
        <DataTable
          title={needsAttentionOnly ? "Needs attention" : "All top-ups"}
          actions={
            <ExportCsvButton rows={query.data?.items} columns={CSV_COLUMNS} fileName={`wallet-top-ups-export-${todayIsoDate()}.csv`} />
          }
          columns={columns}
          rows={query.data?.items}
          rowKey={(item) => item.id}
          isLoading={query.isPending}
          isFetching={query.isFetching}
          error={query.error}
          onRetry={() => query.refetch()}
          skeletonRows={8}
          minWidth="1100px"
          caption="Wallet top-ups matching the current filters, newest first"
          emptyTitle={needsAttentionOnly ? "Nothing needs attention" : "No top-ups match these filters"}
          emptyDescription={
            needsAttentionOnly
              ? "No top-up is stuck or flagged for review."
              : "Clear the filters to see every wallet top-up."
          }
          footer={
            query.data ? (
              <Pagination
                page={page}
                pageSize={PAGE_SIZE}
                totalCount={query.data.totalCount}
                onPageChange={setPage}
                busy={query.isFetching}
                itemLabel="top-up"
              />
            ) : null
          }
        />
      </div>

      <ConfirmDialog
        open={reconciling !== null}
        title={reconciling ? `Reconcile ${reconciling.customerName}'s top-up` : "Reconcile top-up"}
        description={
          reconciling
            ? `Asks the payment gateway how this ${reconciling.currency} ${formatCurrency(reconciling.amount)} payment ended. If it was paid, the amount is credited to the wallet once; if it failed, the top-up is marked failed. Nothing changes while the gateway still says pending. Safe to repeat, and recorded in the audit trail.`
            : undefined
        }
        confirmLabel="Check with gateway"
        tone="primary"
        loading={reconcileMutation.isPending}
        error={reconcileMutation.isError ? describeError(reconcileMutation.error) : null}
        onCancel={() => {
          setReconciling(null);
          reconcileMutation.reset();
        }}
        onConfirm={() => reconciling && reconcileMutation.mutate(reconciling)}
      />
    </div>
  );
}

/**
 * A summary figure that doubles as a filter - clicking narrows the table, clicking again clears it. The active
 * state is a ring rather than a different tone, so the badge's colour still says what the figure is.
 */
function SummaryChip({
  tone,
  label,
  active,
  onClick,
}: {
  tone: BadgeTone;
  label: string;
  active: boolean;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-pressed={active}
      className={cx(
        "rounded-full outline-offset-2 transition-shadow",
        active ? "ring-2 ring-brand-500 ring-offset-2 ring-offset-bg dark:ring-offset-bg-raised" : "",
      )}
    >
      <Badge tone={tone}>{label}</Badge>
    </button>
  );
}
