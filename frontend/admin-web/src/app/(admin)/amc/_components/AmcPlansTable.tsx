"use client";

import { useState } from "react";
import type { ReactNode } from "react";
import { Button } from "@/components/ui";
import { ActiveBadge, ConfirmDialog, DataTable, formatCurrency, formatDate } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { describeError } from "@/lib/api";
import type { AmcPlanAdminResponse } from "../_lib/amc-api";

/**
 * The AMC plan list, mirroring `subscription-plans/_components/PlansTable.tsx`
 * exactly - same unpaged list, same deactivate-confirms/activate-doesn't
 * asymmetry (deactivation withdraws a plan from the catalog immediately;
 * activation is the recoverable direction).
 */
export function AmcPlansTable({
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
  plans: AmcPlanAdminResponse[] | undefined;
  isLoading: boolean;
  isFetching?: boolean;
  error?: unknown;
  onRetry?: () => void;
  canWrite: boolean;
  onEdit: (plan: AmcPlanAdminResponse) => void;
  onToggleActive: (plan: AmcPlanAdminResponse) => void;
  /** Id of the row currently toggling, so only that row's button is busy. */
  togglingId?: string;
  toggleError?: unknown;
  /** The next step out of an empty list. */
  emptyAction?: ReactNode;
}) {
  const [pendingDeactivate, setPendingDeactivate] = useState<AmcPlanAdminResponse | null>(null);
  const [confirmed, setConfirmed] = useState(false);

  const isDeactivating = pendingDeactivate !== null && togglingId === pendingDeactivate.id;

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

  const columns: DataTableColumn<AmcPlanAdminResponse>[] = [
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
      key: "category",
      header: "Category",
      sortValue: (plan) => plan.categoryName,
      cell: (plan) => plan.categoryName,
    },
    {
      key: "price",
      header: "Price",
      numeric: true,
      sortValue: (plan) => plan.price,
      cell: (plan) => formatCurrency(plan.price),
    },
    {
      key: "term",
      header: "Term",
      numeric: true,
      sortValue: (plan) => plan.termMonths,
      cell: (plan) => `${plan.termMonths} mo`,
    },
    {
      key: "visits",
      header: "Visits included",
      numeric: true,
      sortValue: (plan) => plan.visitsIncluded,
      cell: (plan) => plan.visitsIncluded.toLocaleString("en-IN"),
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
        title="AMC plans"
        description="Every maintenance-contract tier. Only active plans are offered to customers."
        columns={columns}
        rows={plans}
        rowKey={(plan) => plan.id}
        isLoading={isLoading}
        isFetching={isFetching}
        error={error}
        onRetry={onRetry}
        caption="AMC plans"
        defaultSort={{ key: "price", direction: "asc" }}
        emptyTitle="No AMC plans yet"
        emptyDescription={
          canWrite
            ? "Create the first tier to start offering maintenance contracts."
            : "An admin with write access to this module can create one."
        }
        emptyAction={emptyAction}
        skeletonRows={4}
        minWidth="920px"
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
        description="It stops being offered to customers immediately. Existing contracts on this plan are unaffected."
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
            <span className="nums">{formatCurrency(pendingDeactivate.price)}</span> /{" "}
            {pendingDeactivate.termMonths} months.
          </p>
        ) : null}
      </ConfirmDialog>
    </>
  );
}
