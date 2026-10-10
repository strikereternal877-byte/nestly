"use client";

import { useState } from "react";
import type { ReactNode } from "react";
import { Badge, Button } from "@/components/ui";
import { ActiveBadge, ConfirmDialog, DataTable, formatCurrency, formatDate } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { describeError } from "@/lib/api";
import { billingCycleLabel, type SubscriptionPlan } from "../_lib/plans-api";

/**
 * The plan list (PRODUCT-ENHANCEMENTS.md #1). Unpaged — the endpoint returns
 * every plan — so the columns are safely sortable.
 *
 * Deactivation goes through `ConfirmDialog`: it withdraws a tier from the
 * customer app immediately, and it was previously a single unconfirmed click
 * on a button that disabled every other row while it ran. Activation is not
 * confirmed; it is the recoverable direction.
 */
export function PlansTable({
  plans,
  isLoading,
  isFetching,
  error,
  onRetry,
  canWrite,
  onEdit,
  onToggleActive,
  togglingId,
  toggleError,
  emptyAction,
}: {
  plans: SubscriptionPlan[] | undefined;
  isLoading: boolean;
  isFetching?: boolean;
  error?: unknown;
  onRetry?: () => void;
  canWrite: boolean;
  onEdit: (plan: SubscriptionPlan) => void;
  onToggleActive: (plan: SubscriptionPlan) => void;
  /** Id of the row currently toggling, so only that row's button is busy. */
  togglingId?: string;
  toggleError?: unknown;
  /** The next step out of an empty list. */
  emptyAction?: ReactNode;
}) {
  const [pendingDeactivate, setPendingDeactivate] = useState<SubscriptionPlan | null>(null);
  const [confirmed, setConfirmed] = useState(false);

  const isDeactivating = pendingDeactivate !== null && togglingId === pendingDeactivate.id;

  // Close the dialog only once the deactivation it started has finished, and
  // only if it succeeded — closing on click would make `toggleError`
  // unreachable and leave a failed deactivation silently unexplained.
  // "Adjusting state when a prop changes" (react.dev/learn/you-might-not-
  // need-an-effect), not an effect: shouldSettle is true for exactly one
  // render (a confirmed toggle that has just stopped being pending), and
  // comparing against its own last-seen value during render is what fires
  // the close-out exactly once per settle rather than on every render where
  // both conditions still hold.
  const shouldSettle = confirmed && !isDeactivating;
  const [wasSettling, setWasSettling] = useState(false);
  if (shouldSettle !== wasSettling) {
    setWasSettling(shouldSettle);
    if (shouldSettle) {
      setConfirmed(false);
      if (!toggleError) setPendingDeactivate(null);
    }
  }

  const columns: DataTableColumn<SubscriptionPlan>[] = [
    {
      key: "name",
      header: "Name",
      sortValue: (plan) => plan.name,
      cell: (plan) => (
        <div className="min-w-0">
          <p className="font-medium text-fg">{plan.name}</p>
          {plan.description ? (
            <p className="mt-0.5 line-clamp-2 text-xs text-fg-muted">{plan.description}</p>
          ) : null}
        </div>
      ),
    },
    {
      key: "price",
      header: "Price",
      numeric: true,
      sortValue: (plan) => plan.price,
      cell: (plan) => formatCurrency(plan.price),
    },
    {
      key: "cycle",
      header: "Cycle",
      sortValue: (plan) => plan.billingCycle,
      cell: (plan) => billingCycleLabel(plan.billingCycle),
    },
    {
      key: "freeVisits",
      header: "Free visits",
      numeric: true,
      sortValue: (plan) => plan.freeVisitsIncluded,
      cell: (plan) => plan.freeVisitsIncluded.toLocaleString("en-IN"),
    },
    {
      key: "discount",
      header: "Discount",
      numeric: true,
      sortValue: (plan) => plan.discountPercent,
      cell: (plan) => `${plan.discountPercent}%`,
    },
    {
      key: "priority",
      header: "Priority slots",
      sortValue: (plan) => plan.prioritySlotFlag,
      cell: (plan) =>
        plan.prioritySlotFlag ? (
          <Badge tone="brand">Included</Badge>
        ) : (
          <span className="text-fg-subtle">—</span>
        ),
    },
    {
      key: "status",
      header: "Status",
      sortValue: (plan) => plan.isActive,
      cell: (plan) => <ActiveBadge active={plan.isActive} />,
    },
    {
      key: "created",
      header: "Created",
      sortValue: (plan) => plan.createdAtUtc,
      cell: (plan) => <span className="nums whitespace-nowrap">{formatDate(plan.createdAtUtc)}</span>,
    },
  ];

  return (
    <>
      <DataTable
        title="Plans"
        description="Every Glavyx Plus tier. Only active plans are offered to customers."
        columns={columns}
        rows={plans}
        rowKey={(plan) => plan.id}
        isLoading={isLoading}
        isFetching={isFetching}
        error={error}
        onRetry={onRetry}
        caption="Subscription plans"
        defaultSort={{ key: "price", direction: "asc" }}
        emptyTitle="No plans yet"
        emptyDescription={
          canWrite
            ? "Create the first tier to start offering Glavyx Plus."
            : "An admin with write access to this module can create one."
        }
        emptyAction={emptyAction}
        skeletonRows={4}
        minWidth="980px"
        rowActions={
          canWrite
            ? (plan) => (
                <>
                  <Button type="button" size="sm" variant="ghost" onClick={() => onEdit(plan)}>
                    Edit
                  </Button>
                  <Button
                    type="button"
                    size="sm"
                    variant={plan.isActive ? "secondary" : "subtle"}
                    disabled={togglingId === plan.id}
                    loading={togglingId === plan.id && !plan.isActive}
                    onClick={() =>
                      plan.isActive ? setPendingDeactivate(plan) : onToggleActive(plan)
                    }
                  >
                    {plan.isActive ? "Deactivate" : "Activate"}
                  </Button>
                </>
              )
            : undefined
        }
      />

      <ConfirmDialog
        open={pendingDeactivate !== null}
        title="Deactivate this plan?"
        description="It stops being offered to customers immediately. Existing subscriptions on this plan are unaffected."
        confirmLabel="Deactivate"
        cancelLabel="Keep active"
        loading={isDeactivating}
        error={toggleError ? describeError(toggleError) : null}
        onCancel={() => {
          setConfirmed(false);
          setPendingDeactivate(null);
        }}
        onConfirm={() => {
          if (!pendingDeactivate) return;
          setConfirmed(true);
          onToggleActive(pendingDeactivate);
        }}
      >
        {pendingDeactivate ? (
          <p className="text-sm text-fg-muted">
            Deactivating <span className="font-medium text-fg">{pendingDeactivate.name}</span> —{" "}
            <span className="nums">{formatCurrency(pendingDeactivate.price)}</span>{" "}
            {billingCycleLabel(pendingDeactivate.billingCycle).toLowerCase()}.
          </p>
        ) : null}
      </ConfirmDialog>
    </>
  );
}
