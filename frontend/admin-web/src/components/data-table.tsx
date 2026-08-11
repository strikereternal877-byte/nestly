"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import type { FormEvent, ReactNode } from "react";
import { Alert, Badge, Button, EmptyState, Modal, Skeleton, cx } from "@/components/ui";
import { describeError } from "@/lib/api";

/**
 * The admin data-table system (task 221).
 *
 * Before this file every module screen hand-rolled a `<table>` with its own
 * borders, its own "Loading…" paragraph, its own empty message and its own
 * pager, so no two lists behaved the same. Everything list-shaped in the admin
 * now goes through `DataTable` + `FilterBar` + `Pagination`, and every
 * destructive action goes through `ConfirmDialog`.
 *
 * This lives outside `components/ui.tsx` on purpose: that file is byte-identical
 * across customer-web, admin-web and provider-web and is frozen for this phase.
 * These primitives are admin-shaped (dense lists, filter forms, bulk pagers)
 * and are additive — nothing here re-implements a kit component, it composes
 * them. It can be ported to the other apps as a whole file if they ever grow
 * comparable screens.
 *
 * Sorting is deliberately client-side over the rows you hand it. None of the
 * admin list endpoints accept a sort parameter, so a server-paginated screen
 * must NOT mark columns sortable: it would sort only the visible page and
 * silently lie about the ordering of the rest. Give `sortValue` to a column
 * only when the table holds the whole data set.
 */

/* -------------------------------------------------------------------------- */
/* Column model                                                               */
/* -------------------------------------------------------------------------- */

export type SortDirection = "asc" | "desc";

export interface DataTableColumn<T> {
  /** Stable identity for the column; also its sort key. */
  key: string;
  /** Header label. Kept short — headers are uppercase and must not wrap awkwardly. */
  header: ReactNode;
  /** Cell content for one row. */
  cell: (row: T) => ReactNode;
  /** Right-aligns and applies tabular figures — use for every column of amounts. */
  numeric?: boolean;
  /**
   * Makes the column sortable. Returning `null`/`undefined` sorts the row to
   * the end in both directions, which is what "no value" should do.
   */
  sortValue?: (row: T) => string | number | boolean | null | undefined;
  /** Extra classes on the `<td>` (widths, truncation). */
  className?: string;
  /** Extra classes on the `<th>`. */
  headerClassName?: string;
  /** Renders the header for assistive tech only — for an actions column. */
  hiddenHeader?: boolean;
}

export type TableDensity = "comfortable" | "compact";

const DENSITY_CELL: Record<TableDensity, string> = {
  comfortable: "px-4 py-3",
  compact: "px-3 py-1.5",
};

const DENSITY_STORAGE_KEY = "nestly.admin.table-density";

/**
 * Density is a per-admin working preference, not per-screen state, so it is
 * remembered. Read after mount rather than during render: the server render has
 * no localStorage and a mismatch would be a hydration error.
 */
function useDensityPreference(): [TableDensity, (density: TableDensity) => void] {
  const [density, setDensity] = useState<TableDensity>("comfortable");

  useEffect(() => {
    try {
      const stored = window.localStorage.getItem(DENSITY_STORAGE_KEY);
      if (stored === "compact" || stored === "comfortable") setDensity(stored);
    } catch {
      // Storage can be unavailable (private mode, blocked cookies) — the
      // default is perfectly usable, so this is not worth surfacing.
    }
  }, []);

  const update = (next: TableDensity) => {
    setDensity(next);
    try {
      window.localStorage.setItem(DENSITY_STORAGE_KEY, next);
    } catch {
      // See above.
    }
  };

  return [density, update];
}

function compareSortValues(
  a: string | number | boolean | null | undefined,
  b: string | number | boolean | null | undefined,
): number {
  // Empties always sink, whichever way the column is pointing.
  const aEmpty = a === null || a === undefined || a === "";
  const bEmpty = b === null || b === undefined || b === "";
  if (aEmpty && bEmpty) return 0;
  if (aEmpty) return 1;
  if (bEmpty) return -1;

  if (typeof a === "number" && typeof b === "number") return a - b;
  if (typeof a === "boolean" && typeof b === "boolean") return Number(a) - Number(b);

  // `numeric` so "Zone 10" sorts after "Zone 9", and locale-aware so the
  // comparison is stable for the mixed-script names in the city/locality data.
  return String(a).localeCompare(String(b), undefined, { numeric: true, sensitivity: "base" });
}

/* -------------------------------------------------------------------------- */
/* DataTable                                                                  */
/* -------------------------------------------------------------------------- */

interface DataTableProps<T> {
  columns: readonly DataTableColumn<T>[];
  /** `undefined` while the first load is in flight. */
  rows: readonly T[] | undefined;
  rowKey: (row: T) => string;

  /** Card header. Omit all three to render the table with no toolbar. */
  title?: string;
  description?: string;
  /** Toolbar controls on the right of the header (a "New…" button, an export). */
  actions?: ReactNode;

  /** First load — renders a skeleton shaped like the real table. */
  isLoading?: boolean;
  /** Background refetch — dims the body instead of replacing it. */
  isFetching?: boolean;
  /** Pass `query.error` straight through; it is run through `describeError`. */
  error?: unknown;
  /** Overrides the message derived from `error`. */
  errorMessage?: string | null;
  onRetry?: () => void;

  emptyTitle?: string;
  emptyDescription?: string;
  /** The next step out of an empty list. An empty state without one is a dead end. */
  emptyAction?: ReactNode;

  /** Per-row controls, rendered right-aligned in a trailing actions column. */
  rowActions?: (row: T) => ReactNode;
  onRowClick?: (row: T) => void;

  defaultSort?: { key: string; direction?: SortDirection };
  /** Vertical cap that turns the sticky header on, e.g. `"30rem"`. */
  maxHeight?: string;
  /** Minimum table width before horizontal scrolling starts, e.g. `"880px"`. */
  minWidth?: string;
  /** Number of placeholder rows while loading. Match the usual page size. */
  skeletonRows?: number;
  /** Accessible name for the table when the toolbar has no title. */
  caption?: string;
  /** Hides the comfortable/compact control (short, fixed-length tables). */
  hideDensityToggle?: boolean;
  /** Pagination or a summary line, rendered in the card footer. */
  footer?: ReactNode;
  className?: string;
}

export function DataTable<T>({
  columns,
  rows,
  rowKey,
  title,
  description,
  actions,
  isLoading = false,
  isFetching = false,
  error,
  errorMessage,
  onRetry,
  emptyTitle = "Nothing to show",
  emptyDescription,
  emptyAction,
  rowActions,
  onRowClick,
  defaultSort,
  // Capped by default so a long list scrolls inside its own card with a
  // sticky header instead of growing the page — every list gets this for
  // free; callers pass a taller/shorter value to override.
  maxHeight = "70vh",
  minWidth,
  skeletonRows = 6,
  caption,
  hideDensityToggle = false,
  footer,
  className = "",
}: DataTableProps<T>) {
  const [density, setDensity] = useDensityPreference();
  const [sort, setSort] = useState<{ key: string; direction: SortDirection } | null>(
    defaultSort ? { key: defaultSort.key, direction: defaultSort.direction ?? "asc" } : null,
  );

  const message = errorMessage ?? (error ? describeError(error) : null);
  const cellPadding = DENSITY_CELL[density];
  const hasHeader = Boolean(title || description || actions) || !hideDensityToggle;

  const sortedRows = useMemo(() => {
    if (!rows) return undefined;
    if (!sort) return rows;
    const column = columns.find((candidate) => candidate.key === sort.key);
    if (!column?.sortValue) return rows;
    const getValue = column.sortValue;
    const factor = sort.direction === "asc" ? 1 : -1;
    return [...rows].sort((a, b) => factor * compareSortValues(getValue(a), getValue(b)));
  }, [rows, sort, columns]);

  const toggleSort = (key: string) => {
    setSort((current) =>
      current?.key === key
        ? { key, direction: current.direction === "asc" ? "desc" : "asc" }
        : { key, direction: "asc" },
    );
  };

  const totalColumns = columns.length + (rowActions ? 1 : 0);

  const body = (() => {
    if (isLoading) {
      return (
        <TableBodyMessage colSpan={totalColumns} padded={false}>
          <div className="divide-y divide-line">
            {Array.from({ length: skeletonRows }, (_, index) => (
              <div key={index} className={cx("flex items-center gap-4", cellPadding)}>
                {columns.map((column, columnIndex) => (
                  <Skeleton
                    key={column.key}
                    className={cx("h-4", columnIndex === 0 ? "w-40" : "w-24")}
                  />
                ))}
                {rowActions ? <Skeleton className="ml-auto h-4 w-16" /> : null}
              </div>
            ))}
          </div>
        </TableBodyMessage>
      );
    }

    if (message) {
      return (
        <TableBodyMessage colSpan={totalColumns}>
          <Alert
            tone="error"
            title="Could not load this list"
            action={
              onRetry ? (
                <Button size="sm" variant="secondary" onClick={onRetry}>
                  Retry
                </Button>
              ) : undefined
            }
          >
            {message}
          </Alert>
        </TableBodyMessage>
      );
    }

    if (!sortedRows || sortedRows.length === 0) {
      return (
        <TableBodyMessage colSpan={totalColumns}>
          <EmptyState
            title={emptyTitle}
            description={emptyDescription}
            action={emptyAction}
            className="border-0 py-10"
          />
        </TableBodyMessage>
      );
    }

    return null;
  })();

  return (
    <section
      className={cx(
        "w-full animate-fade-in overflow-hidden rounded-2xl border border-line bg-surface shadow-sm",
        className,
      )}
    >
      {hasHeader ? (
        <div className="flex flex-wrap items-start justify-between gap-3 border-b border-line px-4 py-3 sm:px-5">
          <div className="min-w-0">
            {title ? <h2 className="text-base font-semibold text-fg">{title}</h2> : null}
            {description ? (
              <p className="mt-1 text-sm leading-relaxed text-fg-muted">{description}</p>
            ) : null}
          </div>
          <div className="flex shrink-0 items-center gap-2">
            {isFetching && !isLoading ? (
              <span className="text-xs text-fg-subtle" role="status">
                Refreshing…
              </span>
            ) : null}
            {actions}
            {hideDensityToggle ? null : <DensityToggle value={density} onChange={setDensity} />}
          </div>
        </div>
      ) : null}

      <div
        className={cx("w-full overflow-x-auto", maxHeight && "overflow-y-auto")}
        style={maxHeight ? { maxHeight } : undefined}
      >
        <table
          className="w-full border-collapse text-left text-sm"
          style={minWidth ? { minWidth } : undefined}
        >
          {caption ? <caption className="sr-only">{caption}</caption> : null}
          <thead
            className={cx(
              "bg-surface-2 text-left",
              // Sticky only matters once the body can scroll under it. The
              // border lives on the cells, not the row: a `<thead>` border
              // disappears under `position: sticky` in Chrome.
              maxHeight && "sticky top-0 z-10",
            )}
          >
            <tr>
              {columns.map((column) => {
                const isSorted = sort?.key === column.key;
                return (
                  <th
                    key={column.key}
                    scope="col"
                    aria-sort={
                      column.sortValue
                        ? isSorted
                          ? sort.direction === "asc"
                            ? "ascending"
                            : "descending"
                          : "none"
                        : undefined
                    }
                    className={cx(
                      "whitespace-nowrap border-b border-line text-xs font-semibold uppercase tracking-wide text-fg-muted",
                      cellPadding,
                      column.numeric && "text-right",
                      column.headerClassName,
                    )}
                  >
                    {column.hiddenHeader ? (
                      <span className="sr-only">{column.header}</span>
                    ) : column.sortValue ? (
                      <button
                        type="button"
                        onClick={() => toggleSort(column.key)}
                        className={cx(
                          "inline-flex items-center gap-1 rounded transition-colors duration-fast ease-out hover:text-fg",
                          isSorted && "text-fg",
                          column.numeric && "flex-row-reverse",
                        )}
                      >
                        {column.header}
                        <SortGlyph direction={isSorted ? sort.direction : null} />
                      </button>
                    ) : (
                      column.header
                    )}
                  </th>
                );
              })}
              {rowActions ? (
                <th
                  scope="col"
                  className={cx(
                    "whitespace-nowrap border-b border-line text-right text-xs font-semibold uppercase tracking-wide text-fg-muted",
                    cellPadding,
                  )}
                >
                  <span className="sr-only">Actions</span>
                </th>
              ) : null}
            </tr>
          </thead>

          <tbody
            className={cx(
              "divide-y divide-line",
              isFetching && !isLoading && "opacity-60 transition-opacity duration-fast ease-out",
            )}
          >
            {body ??
              sortedRows!.map((row) => (
                <tr
                  key={rowKey(row)}
                  onClick={onRowClick ? () => onRowClick(row) : undefined}
                  // A `<tr>` is not natively interactive: without
                  // tabIndex/onKeyDown a clickable row is mouse-only,
                  // invisible to keyboard and screen-reader users despite the
                  // pointer cursor telling sighted mouse users it acts like a
                  // link (mirrors the fix in the shared `TR` primitive).
                  tabIndex={onRowClick ? 0 : undefined}
                  onKeyDown={
                    onRowClick
                      ? (event) => {
                          if (event.key !== "Enter" && event.key !== " ") return;
                          event.preventDefault();
                          onRowClick(row);
                        }
                      : undefined
                  }
                  className={cx(
                    "transition-colors duration-fast ease-out",
                    onRowClick ? "cursor-pointer hover:bg-surface-2" : "hover:bg-surface-2/60",
                  )}
                >
                  {columns.map((column) => (
                    <td
                      key={column.key}
                      className={cx(
                        "align-middle text-fg",
                        cellPadding,
                        column.numeric && "nums text-right",
                        column.className,
                      )}
                    >
                      {column.cell(row)}
                    </td>
                  ))}
                  {rowActions ? (
                    <td className={cx("align-middle", cellPadding)}>
                      <div className="flex items-center justify-end gap-1.5">{rowActions(row)}</div>
                    </td>
                  ) : null}
                </tr>
              ))}
          </tbody>
        </table>
      </div>

      {footer ? (
        <div className="border-t border-line bg-surface-2 px-4 py-3 sm:px-5">{footer}</div>
      ) : null}
    </section>
  );
}

/** Full-width row used for the skeleton/error/empty states inside the table. */
function TableBodyMessage({
  colSpan,
  children,
  padded = true,
}: {
  colSpan: number;
  children: ReactNode;
  padded?: boolean;
}) {
  return (
    <tr>
      <td colSpan={colSpan} className={padded ? "px-4 py-6" : "p-0"}>
        {children}
      </td>
    </tr>
  );
}

function SortGlyph({ direction }: { direction: SortDirection | null }) {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2.5"
      strokeLinecap="round"
      strokeLinejoin="round"
      className={cx("h-3 w-3 shrink-0", direction ? "text-brand-600 dark:text-brand-400" : "text-fg-subtle")}
      aria-hidden
    >
      {direction === "desc" ? <path d="m6 9 6 6 6-6" /> : null}
      {direction === "asc" ? <path d="m6 15 6-6 6 6" /> : null}
      {direction === null ? <path d="m8 10 4-4 4 4M8 14l4 4 4-4" /> : null}
    </svg>
  );
}

/** Comfortable/compact switch. Two states, so a segmented control, not a menu. */
export function DensityToggle({
  value,
  onChange,
}: {
  value: TableDensity;
  onChange: (value: TableDensity) => void;
}) {
  return (
    <div
      className="inline-flex items-center gap-0.5 rounded-lg border border-line bg-surface-2 p-0.5"
      role="group"
      aria-label="Row density"
    >
      {(["comfortable", "compact"] as const).map((option) => (
        <button
          key={option}
          type="button"
          aria-pressed={value === option}
          onClick={() => onChange(option)}
          className={cx(
            "rounded-md px-2 py-1 text-xs font-medium capitalize transition-colors duration-fast ease-out",
            value === option
              ? "bg-surface text-fg shadow-xs"
              : "text-fg-muted hover:text-fg",
          )}
        >
          {option}
        </button>
      ))}
    </div>
  );
}

/* -------------------------------------------------------------------------- */
/* Filter bar                                                                 */
/* -------------------------------------------------------------------------- */

/**
 * The filter pattern that sits above every list: a responsive grid of kit
 * controls, then one action row. Submitting is explicit rather than
 * filter-on-keystroke — these screens are backed by paged server queries, and
 * a request per keystroke is both slow and unreadable.
 */
export function FilterBar({
  children,
  onSubmit,
  onClear,
  activeCount = 0,
  submitLabel = "Search",
  busy = false,
  columns = 4,
  actions,
  className = "",
}: {
  children: ReactNode;
  onSubmit: () => void;
  /** Omit for a filter set that cannot be meaningfully emptied. */
  onClear?: () => void;
  /** How many filters currently hold a value — shown as a pill. */
  activeCount?: number;
  submitLabel?: string;
  busy?: boolean;
  columns?: 2 | 3 | 4;
  /** Extra controls beside Search/Clear (an export button, a saved view). */
  actions?: ReactNode;
  className?: string;
}) {
  const submit = (event: FormEvent) => {
    event.preventDefault();
    onSubmit();
  };

  const grid =
    columns === 2
      ? "sm:grid-cols-2"
      : columns === 3
        ? "sm:grid-cols-2 lg:grid-cols-3"
        : "sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4";

  return (
    <form
      onSubmit={submit}
      className={cx("rounded-2xl border border-line bg-surface p-4 shadow-sm sm:p-5", className)}
      aria-label="Filters"
    >
      <div className={cx("grid grid-cols-1 gap-4", grid)}>{children}</div>

      <div className="mt-4 flex flex-wrap items-center justify-between gap-3 border-t border-line pt-4">
        <div className="flex items-center gap-2 text-sm text-fg-muted">
          {activeCount > 0 ? (
            <Badge tone="brand">
              {activeCount} filter{activeCount === 1 ? "" : "s"} applied
            </Badge>
          ) : (
            <span className="text-fg-subtle">No filters applied</span>
          )}
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {actions}
          {onClear ? (
            <Button type="button" variant="ghost" onClick={onClear} disabled={activeCount === 0}>
              Clear
            </Button>
          ) : null}
          <Button type="submit" loading={busy}>
            {submitLabel}
          </Button>
        </div>
      </div>
    </form>
  );
}

/**
 * Counts the filter values that are actually set, for `FilterBar.activeCount`.
 *
 * Typed as `object` rather than `Record<string, unknown>` so a named filter
 * interface — which has no index signature of its own — can be passed without
 * a cast, matching the `query()` helpers in the API clients.
 */
export function countActiveFilters(filters: object): number {
  return Object.values(filters).filter(
    (value) => value !== undefined && value !== null && value !== "" && value !== false,
  ).length;
}

/* -------------------------------------------------------------------------- */
/* Pagination                                                                 */
/* -------------------------------------------------------------------------- */

/**
 * Prev/next pager with the count line, sized for the server-paged admin lists.
 * No page-number strip: the counts here run to thousands of pages, where a
 * numbered strip is noise.
 */
export function Pagination({
  page,
  pageSize,
  totalCount,
  onPageChange,
  itemLabel = "result",
  itemLabelPlural,
  busy = false,
}: {
  page: number;
  pageSize: number;
  totalCount: number;
  onPageChange: (page: number) => void;
  itemLabel?: string;
  itemLabelPlural?: string;
  busy?: boolean;
}) {
  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize));
  const plural = itemLabelPlural ?? `${itemLabel}s`;
  const firstOnPage = totalCount === 0 ? 0 : (page - 1) * pageSize + 1;
  const lastOnPage = Math.min(page * pageSize, totalCount);

  return (
    <div className="flex flex-wrap items-center justify-between gap-3">
      <p className="text-sm text-fg-muted">
        <span className="nums">
          {firstOnPage}–{lastOnPage}
        </span>{" "}
        of <span className="nums font-medium text-fg">{totalCount.toLocaleString("en-IN")}</span>{" "}
        {totalCount === 1 ? itemLabel : plural}
        <span className="text-fg-subtle">
          {" "}
          · page <span className="nums">{page}</span> of <span className="nums">{totalPages}</span>
        </span>
      </p>
      <div className="flex items-center gap-2">
        <Button
          type="button"
          size="sm"
          variant="secondary"
          disabled={busy || page <= 1}
          onClick={() => onPageChange(Math.max(1, page - 1))}
        >
          Previous
        </Button>
        <Button
          type="button"
          size="sm"
          variant="secondary"
          disabled={busy || page >= totalPages}
          onClick={() => onPageChange(Math.min(totalPages, page + 1))}
        >
          Next
        </Button>
      </div>
    </div>
  );
}

/* -------------------------------------------------------------------------- */
/* Destructive-action confirmation                                            */
/* -------------------------------------------------------------------------- */

/**
 * One confirmation shape for every irreversible or high-blast-radius admin
 * action — suspend a provider, delete a banner, cancel a booking, force a
 * refund. `window.confirm` was the alternative and it cannot be styled, cannot
 * show the record being acted on, and cannot show an in-flight state.
 */
export function ConfirmDialog({
  open,
  title,
  description,
  confirmLabel = "Confirm",
  cancelLabel = "Cancel",
  tone = "danger",
  loading = false,
  error,
  onConfirm,
  onCancel,
  children,
}: {
  open: boolean;
  title: string;
  description?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  /** `danger` for anything destructive; `primary` for a merely significant action. */
  tone?: "danger" | "primary";
  loading?: boolean;
  /** Failure of the confirmed action — keeps the dialog open with the reason. */
  error?: string | null;
  onConfirm: () => void;
  onCancel: () => void;
  /** Extra context: the record's name, a required reason field. */
  children?: ReactNode;
}) {
  return (
    <Modal
      open={open}
      onClose={loading ? () => {} : onCancel}
      title={title}
      description={description}
      size="sm"
      footer={
        <>
          <Button type="button" variant="secondary" onClick={onCancel} disabled={loading}>
            {cancelLabel}
          </Button>
          <Button type="button" variant={tone} onClick={onConfirm} loading={loading}>
            {confirmLabel}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {children}
        {error ? <Alert tone="error">{error}</Alert> : null}
        {!children && !error ? (
          <p className="text-sm text-fg-muted">This cannot be undone.</p>
        ) : null}
      </div>
    </Modal>
  );
}

/* -------------------------------------------------------------------------- */
/* Page furniture                                                             */
/* -------------------------------------------------------------------------- */

/** Breadcrumb trail for the detail screens, fed to `PageHeading.breadcrumbs`. */
export function Breadcrumbs({
  items,
}: {
  items: readonly { label: string; href?: string }[];
}) {
  return (
    <nav aria-label="Breadcrumb">
      <ol className="flex flex-wrap items-center gap-1.5 text-xs text-fg-muted">
        {items.map((item, index) => (
          <li key={`${item.label}-${index}`} className="flex items-center gap-1.5">
            {index > 0 ? (
              <span aria-hidden className="text-fg-subtle">
                /
              </span>
            ) : null}
            {item.href ? (
              <a
                href={item.href}
                className="rounded underline-offset-4 transition-colors duration-fast ease-out hover:text-fg hover:underline"
              >
                {item.label}
              </a>
            ) : (
              <span className="font-medium text-fg">{item.label}</span>
            )}
          </li>
        ))}
      </ol>
    </nav>
  );
}

/**
 * Label/value grid for the read-only halves of the detail screens. A real
 * `<dl>` rather than a two-column table, because this is describing one record
 * rather than comparing many.
 */
export function DescriptionList({
  items,
  columns = 2,
  className = "",
}: {
  items: readonly { label: string; value: ReactNode }[];
  columns?: 1 | 2 | 3;
  className?: string;
}) {
  const grid = columns === 1 ? "" : columns === 2 ? "sm:grid-cols-2" : "sm:grid-cols-2 lg:grid-cols-3";
  return (
    <dl className={cx("grid grid-cols-1 gap-x-6 gap-y-4", grid, className)}>
      {items.map((item) => (
        <div key={item.label} className="min-w-0">
          <dt className="text-xs font-medium uppercase tracking-wide text-fg-subtle">
            {item.label}
          </dt>
          <dd className="mt-1 break-words text-sm text-fg">{item.value}</dd>
        </div>
      ))}
    </dl>
  );
}

/** Responsive field grid so every admin form lays out the same way. */
export function FormGrid({
  children,
  columns = 2,
  className = "",
}: {
  children: ReactNode;
  columns?: 1 | 2 | 3;
  className?: string;
}) {
  const grid = columns === 1 ? "" : columns === 2 ? "sm:grid-cols-2" : "sm:grid-cols-2 lg:grid-cols-3";
  return <div className={cx("grid grid-cols-1 gap-4", grid, className)}>{children}</div>;
}

/**
 * Searchable single-select for pickers with too many options to scan in a
 * plain `<select>` (a category/parent-service/service list). Filters options
 * by label as the admin types; falls back to a native `<select>`'s keyboard
 * semantics via a listbox popover rather than reinventing them.
 */
export function SearchableSelect({
  label,
  value,
  onChange,
  options,
  placeholder = "Search…",
  error,
  required,
  disabled,
}: {
  label: string;
  /** Selected option's `value`, or `""` for none. */
  value: string;
  onChange: (value: string) => void;
  options: readonly { value: string; label: string }[];
  placeholder?: string;
  error?: string;
  required?: boolean;
  disabled?: boolean;
}) {
  const [query, setQuery] = useState("");
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const inputId = `searchable-select-${label.toLowerCase().replace(/\s+/g, "-")}`;

  const selected = options.find((option) => option.value === value);

  // Reflect the current selection in the input text whenever it changes
  // externally (form reset, initial load) and the field isn't mid-edit.
  useEffect(() => {
    if (!open) setQuery(selected?.label ?? "");
  }, [selected, open]);

  useEffect(() => {
    if (!open) return;
    const onPointerDown = (event: MouseEvent) => {
      if (!rootRef.current?.contains(event.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", onPointerDown);
    return () => document.removeEventListener("mousedown", onPointerDown);
  }, [open]);

  const filtered = useMemo(() => {
    const needle = query.trim().toLowerCase();
    if (!needle || query === selected?.label) return options;
    return options.filter((option) => option.label.toLowerCase().includes(needle));
  }, [options, query, selected]);

  return (
    <div className="flex flex-col gap-1.5" ref={rootRef}>
      <label htmlFor={inputId} className="text-sm font-medium text-fg">
        {label}
        {required ? (
          <span className="ml-0.5 text-danger" aria-hidden>
            *
          </span>
        ) : null}
      </label>
      <div className="relative">
        <input
          id={inputId}
          role="combobox"
          aria-expanded={open}
          aria-controls={`${inputId}-listbox`}
          aria-autocomplete="list"
          autoComplete="off"
          disabled={disabled}
          placeholder={placeholder}
          value={query}
          onFocus={() => {
            setOpen(true);
            setQuery("");
          }}
          onChange={(event) => {
            setQuery(event.target.value);
            setOpen(true);
          }}
          onKeyDown={(event) => {
            if (event.key === "Escape") setOpen(false);
          }}
          aria-invalid={error ? true : undefined}
          className={cx(
            "w-full rounded-lg border bg-surface px-3 py-2 text-sm text-fg shadow-xs outline-none transition duration-fast ease-out placeholder:text-fg-subtle disabled:cursor-not-allowed disabled:bg-surface-2 disabled:text-fg-subtle",
            error
              ? "border-danger focus:border-danger focus:ring-2 focus:ring-danger/25"
              : "border-line hover:border-line-strong focus:border-brand-600 focus:ring-2 focus:ring-brand-600/25",
          )}
        />
        {open ? (
          <ul
            id={`${inputId}-listbox`}
            role="listbox"
            className="absolute z-20 mt-1 max-h-56 w-full overflow-y-auto rounded-lg border border-line bg-surface py-1 shadow-md"
          >
            {filtered.length === 0 ? (
              <li className="px-3 py-2 text-sm text-fg-subtle">No matches</li>
            ) : (
              filtered.map((option) => (
                <li key={option.value} role="option" aria-selected={option.value === value}>
                  <button
                    type="button"
                    className={cx(
                      "block w-full px-3 py-2 text-left text-sm hover:bg-surface-2",
                      option.value === value && "bg-surface-2 font-medium text-fg",
                    )}
                    onClick={() => {
                      onChange(option.value);
                      setQuery(option.label);
                      setOpen(false);
                    }}
                  >
                    {option.label}
                  </button>
                </li>
              ))
            )}
          </ul>
        ) : null}
      </div>
      {error ? <p className="text-xs font-medium text-danger">{error}</p> : null}
    </div>
  );
}

/** Right-aligned form footer. Cancel sits left of the primary, never after it. */
export function FormActions({
  children,
  align = "end",
  className = "",
}: {
  children: ReactNode;
  align?: "start" | "end";
  className?: string;
}) {
  return (
    <div
      className={cx(
        "flex flex-wrap items-center gap-2",
        align === "end" ? "justify-end" : "justify-start",
        className,
      )}
    >
      {children}
    </div>
  );
}

/* -------------------------------------------------------------------------- */
/* Shared cell renderers                                                      */
/* -------------------------------------------------------------------------- */

/** Active/inactive pill — the single most repeated cell in this admin. */
export function ActiveBadge({
  active,
  activeLabel = "Active",
  inactiveLabel = "Inactive",
}: {
  active: boolean;
  activeLabel?: string;
  inactiveLabel?: string;
}) {
  return (
    <Badge tone={active ? "success" : "neutral"}>{active ? activeLabel : inactiveLabel}</Badge>
  );
}

/** Renders an amount in rupees with tabular figures and Indian digit grouping. */
export function formatCurrency(amount: number): string {
  return `₹${amount.toLocaleString("en-IN", {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  })}`;
}

/**
 * Formats a UTC instant from the API in the viewer's local zone. Distinct from
 * `lib/date.ts`, which is for calendar dates — this one is for timestamps,
 * where converting to local time is exactly what is wanted.
 */
export function formatDateTime(utc: string | null | undefined): string {
  if (!utc) return "—";
  const parsed = new Date(utc);
  return Number.isNaN(parsed.getTime()) ? "—" : parsed.toLocaleString();
}

/** Date-only counterpart to `formatDateTime` for a timestamp column. */
export function formatDate(utc: string | null | undefined): string {
  if (!utc) return "—";
  const parsed = new Date(utc);
  return Number.isNaN(parsed.getTime()) ? "—" : parsed.toLocaleDateString();
}
