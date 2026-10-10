"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useEffect, useState } from "react";
import { Alert, Badge, Button, Field, PageHeading, Textarea, cx } from "@/components/ui";
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
import { getPaymentReconciliation, voidPaymentTransaction } from "@/lib/payments-api";
import { PaymentReconciliationCategory } from "@/lib/payments-types";
import type { AdminPaymentReconciliationItem } from "@/lib/payments-types";
import { useAdminClaims } from "@/lib/use-admin-claims";

const PAGE_SIZE = 20;

const CATEGORY_LABELS: Record<PaymentReconciliationCategory, string> = {
  [PaymentReconciliationCategory.StuckPending]: "Stuck pending",
  [PaymentReconciliationCategory.Failed]: "Failed",
  [PaymentReconciliationCategory.Orphaned]: "Orphaned",
};

const RECONCILIATION_CSV_COLUMNS: readonly CsvColumn<AdminPaymentReconciliationItem>[] = [
  { header: "Bucket", value: (item) => CATEGORY_LABELS[item.category] },
  { header: "Booking #", value: (item) => item.bookingReference },
  { header: "Booking status", value: (item) => item.bookingStatusLabel },
  { header: "Customer", value: (item) => item.customerName },
  { header: "Amount", value: (item) => item.amount },
  { header: "Currency", value: (item) => item.currency },
  { header: "Open since", value: (item) => item.openSinceUtc },
];

const CATEGORY_TONES: Record<PaymentReconciliationCategory, BadgeTone> = {
  [PaymentReconciliationCategory.StuckPending]: "warning",
  [PaymentReconciliationCategory.Failed]: "danger",
  [PaymentReconciliationCategory.Orphaned]: "accent",
};

const CATEGORY_DESCRIPTIONS: Record<PaymentReconciliationCategory, string> = {
  [PaymentReconciliationCategory.StuckPending]: "A gateway order was created but never resolved - open more than 30 minutes.",
  [PaymentReconciliationCategory.Failed]: "The most recent attempt failed and the booking is still awaiting payment.",
  [PaymentReconciliationCategory.Orphaned]: "No successful payment and nothing currently in flight - abandoned checkout or a voided order.",
};

/** "3h 12m" / "45m" - deliberately coarser than seconds since this queue is checked periodically, not watched live. */
function formatAge(totalMinutes: number): string {
  if (totalMinutes < 60) return `${totalMinutes}m`;
  const hours = Math.floor(totalMinutes / 60);
  const minutes = totalMinutes % 60;
  if (hours < 24) return minutes === 0 ? `${hours}h` : `${hours}h ${minutes}m`;
  const days = Math.floor(hours / 24);
  const remainingHours = hours % 24;
  return remainingHours === 0 ? `${days}d` : `${days}d ${remainingHours}h`;
}

/**
 * Payment reconciliation queue (docs/OPEN-FIXES-FEATURES.csv "Payment
 * reconciliation" row): "gateway orders versus booking status" for bookings
 * still Awaiting Payment/Payment Failed - `PaymentsController.GetReconciliation`.
 * Three buckets, oldest first:
 *
 * - **Stuck pending** - a gateway order open past the stuck threshold. The
 *   only row with a direct action here (Void): it marks OUR record only, no
 *   gateway call, so the order can no longer be retried once voided (falls
 *   into Orphaned next time this list refreshes).
 * - **Failed** - the customer can still retry from the app; no admin action
 *   is needed unless they report being stuck, in which case the booking's
 *   own detail page has the full history.
 * - **Orphaned** - no successful payment and nothing in flight. Resolved via
 *   the booking detail page's existing actions (a manual/offline payment
 *   record, or admin cancellation), linked from each row rather than
 *   duplicated here.
 */
export default function PaymentReconciliationPage() {
  const claims = useAdminClaims();
  const canVoid = claims?.permissions.includes("payments.write") ?? false;
  const queryClient = useQueryClient();

  const [page, setPage] = useState(1);
  const [voidingItem, setVoidingItem] = useState<AdminPaymentReconciliationItem | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  // Bucket filter: "" means every category. Clicking a summary badge below
  // sets this directly, same as typing into Search - both just narrow the
  // one query, no separate "Search" button (this queue is small/bounded,
  // unlike the full booking search screen's server-paged list).
  const [categoryFilter, setCategoryFilter] = useState<"" | PaymentReconciliationCategory>("");
  const [search, setSearch] = useState("");
  const [debouncedSearch, setDebouncedSearch] = useState("");
  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedSearch(search.trim()), 300);
    return () => window.clearTimeout(handle);
  }, [search]);

  // Any filter change resets to page 1 - staying on page 3 of a now-smaller
  // result set would just show an empty page.
  useResetOnChange([categoryFilter, debouncedSearch], () => setPage(1));

  const query = useQuery({
    queryKey: ["admin-payments-reconciliation", page, categoryFilter, debouncedSearch] as const,
    queryFn: () =>
      getPaymentReconciliation(page, PAGE_SIZE, {
        category: categoryFilter === "" ? undefined : categoryFilter,
        search: debouncedSearch || undefined,
      }),
    placeholderData: keepPreviousData,
  });

  const columns: DataTableColumn<AdminPaymentReconciliationItem>[] = [
    {
      key: "category",
      header: "Bucket",
      cell: (item) => (
        <div>
          <Badge tone={CATEGORY_TONES[item.category]}>{CATEGORY_LABELS[item.category]}</Badge>
          <p className="mt-1 max-w-[16rem] text-xs text-fg-subtle">{CATEGORY_DESCRIPTIONS[item.category]}</p>
        </div>
      ),
    },
    {
      key: "booking",
      header: "Booking",
      cell: (item) => (
        <>
          <Link
            href={`/bookings/${item.bookingId}`}
            className="nums font-medium text-fg underline-offset-4 hover:text-brand-600 hover:underline dark:hover:text-brand-400"
          >
            {item.bookingReference}
          </Link>
          <div className="mt-0.5 text-xs text-fg-subtle">{item.bookingStatusLabel}</div>
        </>
      ),
    },
    { key: "customer", header: "Customer", cell: (item) => item.customerName },
    {
      key: "amount",
      header: "Amount",
      numeric: true,
      cell: (item) => (
        <span className="nums">
          {item.currency} {formatCurrency(item.amount)}
        </span>
      ),
    },
    {
      key: "age",
      header: "Open since",
      cell: (item) => (
        <>
          <Badge tone={CATEGORY_TONES[item.category]}>{formatAge(item.ageMinutes)}</Badge>
          <div className="nums mt-1 text-xs text-fg-subtle">{formatDateTime(item.openSinceUtc)}</div>
        </>
      ),
    },
    {
      key: "actions",
      header: "",
      cell: (item) => {
        if (item.category === PaymentReconciliationCategory.StuckPending) {
          return canVoid ? (
            <Button size="sm" variant="secondary" onClick={() => setVoidingItem(item)}>
              Void
            </Button>
          ) : (
            <span className="text-xs text-fg-subtle">Needs payments.write</span>
          );
        }

        return (
          <Link href={`/bookings/${item.bookingId}`} className="text-sm text-brand-600 hover:underline dark:text-brand-400">
            Resolve on booking
          </Link>
        );
      },
    },
  ];

  return (
    <div className="w-full max-w-7xl">
      <PageHeading
        title="Payment Reconciliation"
        subtitle="Gateway orders versus booking status - stuck, failed and orphaned payments that need ops attention, oldest first."
      />

      <PaymentsTabs />

      {notice ? (
        <div className="mt-4">
          <Alert tone="success">{notice}</Alert>
        </div>
      ) : null}
      {!canVoid ? (
        <div className="mt-4">
          <Alert tone="info">
            You can review this queue but not void a stuck order - that needs the &quot;payments.write&quot; permission
            (Finance Admin or Super Admin).
          </Alert>
        </div>
      ) : null}

      {query.data ? (
        <div className="mt-6 flex flex-wrap gap-3">
          <CategoryFilterBadge
            tone={CATEGORY_TONES[PaymentReconciliationCategory.StuckPending]}
            label={`${query.data.stuckPendingCount} stuck pending`}
            active={categoryFilter === PaymentReconciliationCategory.StuckPending}
            onClick={() =>
              setCategoryFilter((current) =>
                current === PaymentReconciliationCategory.StuckPending ? "" : PaymentReconciliationCategory.StuckPending,
              )
            }
          />
          <CategoryFilterBadge
            tone={CATEGORY_TONES[PaymentReconciliationCategory.Failed]}
            label={`${query.data.failedCount} failed`}
            active={categoryFilter === PaymentReconciliationCategory.Failed}
            onClick={() =>
              setCategoryFilter((current) =>
                current === PaymentReconciliationCategory.Failed ? "" : PaymentReconciliationCategory.Failed,
              )
            }
          />
          <CategoryFilterBadge
            tone={CATEGORY_TONES[PaymentReconciliationCategory.Orphaned]}
            label={`${query.data.orphanedCount} orphaned`}
            active={categoryFilter === PaymentReconciliationCategory.Orphaned}
            onClick={() =>
              setCategoryFilter((current) =>
                current === PaymentReconciliationCategory.Orphaned ? "" : PaymentReconciliationCategory.Orphaned,
              )
            }
          />
        </div>
      ) : null}

      <div className="mt-4 flex flex-wrap items-end gap-3">
        <div className="max-w-xs flex-1">
          <Field
            label="Search"
            name="search"
            placeholder="Booking # or customer name"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
        </div>
        {search || categoryFilter !== "" ? (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={() => {
              setSearch("");
              setCategoryFilter("");
            }}
          >
            Clear filters
          </Button>
        ) : null}
      </div>

      <div className="mt-4">
        <DataTable
          title="Needs attention"
          actions={
            <ExportCsvButton
              rows={query.data?.items}
              columns={RECONCILIATION_CSV_COLUMNS}
              fileName={`payment-reconciliation-export-${todayIsoDate()}.csv`}
            />
          }
          columns={columns}
          rows={query.data?.items}
          rowKey={(item) => item.paymentTransactionId ?? item.bookingId}
          isLoading={query.isPending}
          isFetching={query.isFetching}
          error={query.error}
          onRetry={() => query.refetch()}
          skeletonRows={8}
          minWidth="1040px"
          caption="Bookings awaiting payment that need admin attention, oldest first"
          emptyTitle="Nothing to reconcile"
          emptyDescription="Every booking awaiting payment either has a fresh, still-in-flight order or has moved on."
          footer={
            query.data ? (
              <Pagination
                page={page}
                pageSize={PAGE_SIZE}
                totalCount={query.data.totalCount}
                onPageChange={setPage}
                busy={query.isFetching}
                itemLabel="row"
              />
            ) : null
          }
        />
      </div>

      <VoidModal
        item={voidingItem}
        onClose={() => setVoidingItem(null)}
        onVoided={(reference) => {
          setVoidingItem(null);
          setNotice(`Voided the stuck pending order for ${reference}.`);
          queryClient.invalidateQueries({ queryKey: ["admin-payments-reconciliation"] });
          queryClient.invalidateQueries({ queryKey: ["admin-payments"] });
        }}
      />
    </div>
  );
}

/**
 * One of the three bucket summary badges, doubling as the category filter -
 * clicking narrows the table to that bucket; clicking the active one again
 * clears back to "All buckets" (no separate reset control needed for this
 * one filter). The active state uses a ring rather than a different tone so
 * the badge's own colour still reads as "this bucket", not "this is
 * selected".
 */
function CategoryFilterBadge({
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

/**
 * Voids a stuck pending transaction (`PaymentsController.Void`) - marks OUR
 * record only, no gateway call. The reason is optional; the backend
 * substitutes a default when omitted.
 *
 * Built on the shared `ConfirmDialog` (task: premium UX audit, "No
 * confirmation guard before irreversible actions") rather than a hand-rolled
 * `Modal` footer, so every destructive admin action shares one confirm/cancel
 * shape instead of each page inventing its own.
 */
function VoidModal({
  item,
  onClose,
  onVoided,
}: {
  item: AdminPaymentReconciliationItem | null;
  onClose: () => void;
  onVoided: (bookingReference: string) => void;
}) {
  const [reason, setReason] = useState("");

  const voidMutation = useMutation({
    mutationFn: () => voidPaymentTransaction(item!.paymentTransactionId!, { reason: reason.trim() || undefined }),
    onSuccess: () => {
      onVoided(item!.bookingReference);
      setReason("");
    },
  });

  return (
    <ConfirmDialog
      open={item !== null}
      onCancel={onClose}
      onConfirm={() => voidMutation.mutate()}
      title={item ? `Void payment order — ${item.bookingReference}` : "Void payment order"}
      description={
        item
          ? `Marks this stuck ${item.currency} ${formatCurrency(item.amount)} order as cancelled in our records. No gateway call is made, and it can no longer be retried - the booking will need to be reconciled separately (a manual payment record, or cancellation).`
          : undefined
      }
      confirmLabel="Void order"
      loading={voidMutation.isPending}
      error={voidMutation.isError ? describeError(voidMutation.error) : null}
    >
      <Textarea
        label="Reason (optional)"
        name="reason"
        value={reason}
        onChange={(e) => setReason(e.target.value)}
        placeholder="e.g. Confirmed with customer the order was abandoned"
      />
    </ConfirmDialog>
  );
}
