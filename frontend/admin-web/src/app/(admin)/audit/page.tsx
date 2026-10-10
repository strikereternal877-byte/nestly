"use client";

import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { Badge, Field, PageHeading, Select } from "@/components/ui";
import { DataTable, FilterBar, Pagination, countActiveFilters } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { useResetOnChange } from "@/hooks/useResetOnChange";
import { API_V1, apiFetch } from "@/lib/api";
import {
  AUDIT_ACTOR_TYPES,
  AUDIT_OUTCOMES,
  DEFAULT_AUDIT_LOG_FILTERS,
  actorTypeLabel,
  buildAuditLogQuery,
  outcomeLabel,
  type AuditLogEntry,
  type AuditLogFilters,
  type PagedAuditLogResponse,
} from "@/lib/audit";

/**
 * Audit log viewer (task 130, SRS 21): a filterable read over the same audit
 * trail task 95g's login audit and task 96d's permission-check audit already
 * write to - see `AuditLogController`/`AuditLogQueryService` on the API side.
 * Gated server-side behind "audit.read"; nav visibility for this page follows
 * the same permission (lib/permissions.ts).
 *
 * Server-paged, so no column here is sortable: the endpoint takes no sort
 * parameter and a header sort would silently reorder only the visible page.
 */

const ACTOR_TYPE_OPTIONS = [
  { value: "", label: "Any actor type" },
  ...AUDIT_ACTOR_TYPES.map((type) => ({ value: type, label: type })),
];

const OUTCOME_OPTIONS = [
  { value: "", label: "Any outcome" },
  ...AUDIT_OUTCOMES.map((outcome) => ({ value: outcome, label: outcome })),
];

/**
 * Outcome is the one genuinely coloured cell on this screen: "Deny"/"Failure"
 * is what an admin scans an audit trail for, so it carries the danger tone
 * rather than sitting in the same neutral pill as everything else.
 */
function OutcomeBadge({ outcome }: { outcome: number }) {
  const label = outcomeLabel(outcome);
  const granted = label === "Grant" || label === "Success";
  return <Badge tone={granted ? "success" : "danger"}>{label}</Badge>;
}

/**
 * The API serialises `occurredOnUtc` without a trailing `Z` in some code
 * paths; without normalising, `new Date()` reads it as local time and every
 * timestamp on the screen is silently off by the viewer's UTC offset.
 */
function formatTimestamp(occurredOnUtc: string): string {
  const normalised = /[Zz]|[+-]\d{2}:\d{2}$/.test(occurredOnUtc) ? occurredOnUtc : `${occurredOnUtc}Z`;
  const parsed = new Date(normalised);
  return Number.isNaN(parsed.getTime()) ? "—" : parsed.toLocaleString();
}

/** Only the user-set filters count towards the pill - `page`/`pageSize` always hold a value. */
function activeFilterCount(filters: AuditLogFilters): number {
  return countActiveFilters({
    actorType: filters.actorType,
    actorId: filters.actorId.trim(),
    entityName: filters.entityName.trim(),
    action: filters.action.trim(),
    outcome: filters.outcome,
    fromDate: filters.fromDate,
    toDate: filters.toDate,
  });
}

const COLUMNS: DataTableColumn<AuditLogEntry>[] = [
  {
    key: "occurredOn",
    header: "Timestamp (local)",
    className: "whitespace-nowrap",
    cell: (entry) => <span className="nums">{formatTimestamp(entry.occurredOnUtc)}</span>,
  },
  {
    key: "actor",
    header: "Actor",
    cell: (entry) => (
      <div className="min-w-0">
        <p className="font-medium text-fg">{actorTypeLabel(entry.actorType)}</p>
        {entry.actorId ? (
          <p className="nums mt-0.5 break-all text-xs text-fg-subtle">{entry.actorId}</p>
        ) : null}
      </div>
    ),
  },
  {
    key: "entity",
    header: "Entity",
    cell: (entry) => (
      <div className="min-w-0">
        <p className="text-fg">{entry.entityName}</p>
        <p className="nums mt-0.5 break-all text-xs text-fg-subtle">{entry.entityId}</p>
      </div>
    ),
  },
  { key: "action", header: "Action", cell: (entry) => entry.action },
  { key: "outcome", header: "Outcome", cell: (entry) => <OutcomeBadge outcome={entry.outcome} /> },
  {
    key: "ip",
    header: "IP",
    className: "whitespace-nowrap",
    cell: (entry) =>
      entry.ipAddress ? (
        <span className="nums text-fg-muted">{entry.ipAddress}</span>
      ) : (
        <span className="text-fg-subtle">—</span>
      ),
  },
];

export default function AuditLogPage() {
  // Live filtering (no Search button): `draft` tracks the form inputs
  // directly. Actor ID, Entity/module and Action are free-text and debounced
  // 300ms before hitting the query, same convention as customers/page.tsx;
  // Actor type, Outcome and the From/To date pickers apply immediately.
  const [draft, setDraft] = useState<AuditLogFilters>(DEFAULT_AUDIT_LOG_FILTERS);

  const [debouncedActorId, setDebouncedActorId] = useState(draft.actorId);
  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedActorId(draft.actorId.trim()), 300);
    return () => window.clearTimeout(handle);
  }, [draft.actorId]);

  const [debouncedEntityName, setDebouncedEntityName] = useState(draft.entityName);
  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedEntityName(draft.entityName.trim()), 300);
    return () => window.clearTimeout(handle);
  }, [draft.entityName]);

  const [debouncedAction, setDebouncedAction] = useState(draft.action);
  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedAction(draft.action.trim()), 300);
    return () => window.clearTimeout(handle);
  }, [draft.action]);

  // Any filter change resets to page 1 - staying on a now out-of-range page
  // would just show an empty result (same pattern as customers/page.tsx).
  useResetOnChange(
    [debouncedActorId, debouncedEntityName, debouncedAction, draft.actorType, draft.outcome, draft.fromDate, draft.toDate],
    () => setDraft((current) => (current.page === 1 ? current : { ...current, page: 1 })),
  );

  const effectiveFilters: AuditLogFilters = {
    ...draft,
    actorId: debouncedActorId,
    entityName: debouncedEntityName,
    action: debouncedAction,
  };

  const query = useQuery({
    queryKey: ["admin-audit-log", effectiveFilters] as const,
    queryFn: () =>
      apiFetch<PagedAuditLogResponse>(`${API_V1}/audit-log?${buildAuditLogQuery(effectiveFilters)}`, {
        authenticated: true,
      }),
    // Paging used to blank the table and drop the pager back to its loading
    // state on every Next, so the control the admin just clicked disappeared
    // under the cursor. Keeping the previous page dims it instead.
    placeholderData: keepPreviousData,
  });

  const totalCount = query.data?.totalCount ?? 0;
  const pageSize = query.data?.pageSize ?? draft.pageSize;
  const activeCount = activeFilterCount(draft);

  return (
    <div className="w-full max-w-7xl">
      <PageHeading
        title="Audit Log"
        subtitle="Every critical business and admin action recorded to the audit trail (SRS 21), filterable by actor, date range, entity/action, and outcome."
      />

      <div className="flex flex-col gap-6">
        <FilterBar
          columns={4}
          activeCount={activeCount}
          busy={query.isFetching}
          onClear={() => {
            setDraft(DEFAULT_AUDIT_LOG_FILTERS);
            setDebouncedActorId("");
            setDebouncedEntityName("");
            setDebouncedAction("");
          }}
        >
          <Select
            label="Actor type"
            options={ACTOR_TYPE_OPTIONS}
            value={draft.actorType}
            onChange={(event) =>
              setDraft({ ...draft, actorType: event.target.value as AuditLogFilters["actorType"] })
            }
          />
          <Field
            label="Actor ID"
            name="actorId"
            autoComplete="on"
            placeholder="Admin user GUID"
            value={draft.actorId}
            onChange={(event) => setDraft({ ...draft, actorId: event.target.value })}
          />
          <Field
            label="Entity / module"
            name="entityName"
            autoComplete="on"
            placeholder="e.g. AdminUser, AdminPermissionCheck"
            value={draft.entityName}
            onChange={(event) => setDraft({ ...draft, entityName: event.target.value })}
          />
          <Field
            label="Action contains"
            name="action"
            autoComplete="on"
            placeholder="e.g. Login, PermissionDenied"
            value={draft.action}
            onChange={(event) => setDraft({ ...draft, action: event.target.value })}
          />
          <Select
            label="Outcome"
            options={OUTCOME_OPTIONS}
            value={draft.outcome}
            onChange={(event) =>
              setDraft({ ...draft, outcome: event.target.value as AuditLogFilters["outcome"] })
            }
          />
          <Field
            label="From"
            type="date"
            // The range is a local calendar window (see buildAuditLogQuery in
            // lib/audit.ts), so the two inputs bound each other rather than
            // allowing from > to.
            max={draft.toDate || undefined}
            value={draft.fromDate}
            onChange={(event) => setDraft({ ...draft, fromDate: event.target.value })}
          />
          <Field
            label="To"
            type="date"
            min={draft.fromDate || undefined}
            value={draft.toDate}
            onChange={(event) => setDraft({ ...draft, toDate: event.target.value })}
          />
        </FilterBar>

        <DataTable
          title="Entries"
          description="Newest first. Timestamps render in your local time zone; the trail itself is stored in UTC."
          columns={COLUMNS}
          rows={query.data?.items}
          rowKey={(entry) => entry.id}
          isLoading={query.isPending}
          isFetching={query.isFetching}
          error={query.error}
          onRetry={() => void query.refetch()}
          caption="Audit trail entries matching the current filters"
          emptyTitle="No audit entries match these filters"
          emptyDescription={
            activeCount > 0
              ? "Widen the date range, or clear the filters to see the whole trail."
              : "Nothing has been recorded to the audit trail yet."
          }
          skeletonRows={10}
          minWidth="1040px"
          footer={
            <Pagination
              page={draft.page}
              pageSize={pageSize}
              totalCount={totalCount}
              busy={query.isFetching}
              itemLabel="entry"
              itemLabelPlural="entries"
              onPageChange={(page) => setDraft((current) => ({ ...current, page }))}
            />
          }
        />
      </div>
    </div>
  );
}
