"use client";

import { useState } from "react";
import type { ReactNode } from "react";
import { Button } from "@/components/ui";
import { ActiveBadge, ConfirmDialog, DataTable } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { describeError } from "@/lib/api";

/**
 * The "master list with an active/inactive toggle" shape (task 222).
 *
 * Roughly a dozen admin screens — every serviceability geography master and
 * mapping, the three catalog lists, promotional prices — reduce to "columns of
 * data, a status pill, and a suspend/activate control". This renders that shape
 * once, on top of `DataTable`, so those screens get the three-state contract
 * (layout-matching skeleton, empty state with a next action, error state with a
 * working Retry) without each re-deriving it.
 *
 * It replaces the previous `serviceability/_components/EntityTable`, which
 * rendered a bare `<table>`, a "Loading…" paragraph, a raw red error string and
 * a hand-rolled green/neutral status pill built from `neutral-*`/`green-*`
 * classes rather than tokens. It also lived under `serviceability/_components`
 * while being imported from `catalog` and `pricing` — hence the move to
 * `src/components`.
 *
 * Deactivation goes through `ConfirmDialog`: suspending a city, a service or a
 * pincode instantly removes it from what customers can book, and it was
 * previously a single unconfirmed click. Activation is not confirmed — it is
 * not destructive.
 */

export interface EntityTableColumn<T> {
  header: string;
  render: (item: T) => ReactNode;
  /** Right-aligns and applies tabular figures — use for every column of amounts. */
  numeric?: boolean;
  /** Extra classes on the cell (widths, truncation). */
  className?: string;
  /**
   * Makes the column sortable. Safe here because these lists are unpaged —
   * the table holds the whole data set. Do NOT add it to a server-paged list.
   */
  sortValue?: (item: T) => string | number | boolean | null | undefined;
}

/** Active/suspended pill for the detail screens, matching the list's column. */
export function StatusBadge({ isActive }: { isActive: boolean }) {
  return <ActiveBadge active={isActive} inactiveLabel="Suspended" />;
}

export function EntityTable<T extends { id: string; isActive: boolean }>({
  items,
  columns,
  isLoading,
  isFetching = false,
  errorMessage,
  error,
  onRetry,
  emptyMessage,
  emptyAction,
  canWrite,
  onToggleActive,
  togglingId,
  toggleError,
  title,
  description,
  actions,
  entityLabel = "record",
  labelOf,
  minWidth,
  maxHeight,
  skeletonRows = 5,
  hideDensityToggle = false,
  footer,
  extraRowActions,
}: {
  items: T[] | undefined;
  columns: readonly EntityTableColumn<T>[];
  isLoading: boolean;
  /** Background refetch — dims the body rather than replacing it. */
  isFetching?: boolean;
  errorMessage?: string | null;
  /** Pass `query.error` straight through as an alternative to `errorMessage`. */
  error?: unknown;
  onRetry?: () => void;
  emptyMessage: string;
  /** The next step out of an empty list. */
  emptyAction?: ReactNode;
  /** Whether the current admin holds the module's write permission (SRS 12.2.3) - hides mutating controls otherwise. */
  canWrite: boolean;
  onToggleActive: (item: T) => void;
  /** Id of the row currently being toggled, to disable just that row's button rather than the whole table. */
  togglingId?: string;
  /** Failure of the toggle — keeps the confirmation open with the reason. */
  toggleError?: unknown;
  title?: string;
  description?: string;
  actions?: ReactNode;
  /** Used in the confirmation copy: "Suspend this city?". */
  entityLabel?: string;
  /** Human name of a row, shown in the confirmation so the admin sees what they are acting on. */
  labelOf?: (item: T) => string;
  minWidth?: string;
  /** Caps the table's own height and scrolls internally, keeping other page sections reachable without scrolling past every row. */
  maxHeight?: string;
  skeletonRows?: number;
  hideDensityToggle?: boolean;
  footer?: ReactNode;
  /** Extra per-row action(s) rendered alongside the built-in Suspend/Activate button (e.g. a pin/unpin toggle) - hidden automatically when `canWrite` is false, same as the built-in button. */
  extraRowActions?: (item: T) => ReactNode;
}) {
  const [pendingSuspend, setPendingSuspend] = useState<T | null>(null);
  const [confirmed, setConfirmed] = useState(false);

  const isSuspending = pendingSuspend !== null && togglingId === pendingSuspend.id;

  /**
   * Close the confirmation only once the suspension it started has actually
   * finished, and only if it succeeded. Closing on click — which is what this
   * did — meant `loading` was never true and the `toggleError` every caller
   * passes could never render: a failed suspend dismissed the dialog and left
   * the row looking active with no explanation anywhere on screen.
   *
   * `confirmed` (a React state update) and `togglingId` reflecting the
   * mutation's new pending state (driven by the caller's own `useMutation`,
   * an external store) are not guaranteed to land in the same commit -
   * `onConfirm` sets `confirmed` and calls the caller's `mutate()` in the
   * same click handler, but react-query's own pending notification can
   * arrive a render later. Without `hasStartedRef`, the effect below saw
   * `confirmed = true` and `isSuspending` still `false` (the mutation
   * hadn't visibly started yet) and read that exactly like "finished with
   * no error", closing the dialog immediately - before the request had even
   * gone out, let alone failed. A fast-failing mutation (a 4xx/5xx that
   * returns quickly) hit this on effectively every attempt, silently
   * dropping the failure with no dialog, no error, no explanation. The ref
   * makes the "no error, so close" branch wait for a render where the
   * mutation was actually observed pending first.
   *
   * State, not a ref: both this and the settle check below are "adjusting
   * state when a prop changes" (react.dev/learn/you-might-not-need-an-effect),
   * done during render rather than in an effect - a ref would need reading
   * (and, to reset it, writing) during that same render, which is exactly
   * what react-hooks/refs exists to catch, since a ref mutation doesn't
   * itself schedule the re-render its own new value would need to be seen.
   */
  const [hasStarted, setHasStarted] = useState(false);
  if (isSuspending && !hasStarted) setHasStarted(true);

  // shouldSettle collapses the three dependencies above into one transition
  // to detect - true for exactly one render, the one where a started
  // mutation has just stopped being pending - and comparing against its own
  // last-seen value during render is what fires the close-out exactly once
  // per settle rather than on every render where all three still hold.
  const shouldSettle = confirmed && !isSuspending && hasStarted;
  const [wasSettling, setWasSettling] = useState(false);
  if (shouldSettle !== wasSettling) {
    setWasSettling(shouldSettle);
    if (shouldSettle) {
      setConfirmed(false);
      setHasStarted(false);
      if (!toggleError) setPendingSuspend(null);
    }
  }

  const tableColumns: DataTableColumn<T>[] = [
    ...columns.map((column) => ({
      key: column.header,
      header: column.header,
      cell: column.render,
      numeric: column.numeric,
      className: column.className,
      sortValue: column.sortValue,
    })),
    {
      key: "__status",
      header: "Status",
      cell: (item: T) => <StatusBadge isActive={item.isActive} />,
      sortValue: (item: T) => item.isActive,
    },
  ];

  return (
    <>
      <DataTable<T>
        title={title}
        description={description}
        actions={actions}
        columns={tableColumns}
        rows={items}
        rowKey={(item) => item.id}
        isLoading={isLoading}
        isFetching={isFetching}
        error={error}
        errorMessage={errorMessage}
        onRetry={onRetry}
        emptyTitle={emptyMessage}
        emptyDescription={
          canWrite ? undefined : "An admin with write access to this module can add one."
        }
        emptyAction={emptyAction}
        minWidth={minWidth}
        maxHeight={maxHeight}
        skeletonRows={skeletonRows}
        hideDensityToggle={hideDensityToggle}
        footer={footer}
        rowActions={
          canWrite
            ? (item) => (
                <>
                  <Button
                    type="button"
                    size="sm"
                    variant={item.isActive ? "secondary" : "subtle"}
                    disabled={togglingId === item.id}
                    loading={togglingId === item.id && !pendingSuspend}
                    onClick={() => {
                      if (item.isActive) {
                        setPendingSuspend(item);
                      } else {
                        onToggleActive(item);
                      }
                    }}
                  >
                    {item.isActive ? "Suspend" : "Activate"}
                  </Button>
                  {extraRowActions?.(item)}
                </>
              )
            : undefined
        }
      />

      <ConfirmDialog
        open={pendingSuspend !== null}
        title={`Suspend this ${entityLabel}?`}
        description="It stops being available to customers immediately. You can activate it again at any time."
        confirmLabel="Suspend"
        cancelLabel="Keep active"
        loading={isSuspending}
        error={toggleError ? describeError(toggleError) : null}
        onCancel={() => {
          setConfirmed(false);
          setPendingSuspend(null);
        }}
        onConfirm={() => {
          if (!pendingSuspend) return;
          setConfirmed(true);
          onToggleActive(pendingSuspend);
        }}
      >
        {pendingSuspend && labelOf ? (
          <p className="text-sm text-fg-muted">
            Suspending <span className="font-medium text-fg">{labelOf(pendingSuspend)}</span>.
          </p>
        ) : null}
      </ConfirmDialog>
    </>
  );
}
