"use client";

import { useState } from "react";
import type { ReactNode } from "react";
import { Button } from "@/components/ui";
import { ConfirmDialog, DataTable } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { describeError } from "@/lib/api";
import { CmsContentStatus, type CmsPageResponse } from "@/lib/cms-types";
import { formatPlacement, formatSchedule } from "./cmsDisplay";
import { CmsStatusBadge } from "./CmsStatusBadge";

/**
 * Static page list (SRS 12.16.1). Columns are deliberately not sortable: the
 * list is paged server-side and the endpoint takes no sort parameter, so a
 * header sort would reorder only the rows on screen.
 */
export function CmsPagesTable({
  pages,
  isLoading,
  isFetching,
  error,
  onRetry,
  canWrite,
  onEdit,
  onTogglePublished,
  togglingId,
  toggleError,
  emptyAction,
  footer,
}: {
  pages: CmsPageResponse[] | undefined;
  isLoading: boolean;
  isFetching?: boolean;
  error?: unknown;
  onRetry?: () => void;
  canWrite: boolean;
  onEdit: (page: CmsPageResponse) => void;
  onTogglePublished: (page: CmsPageResponse) => void;
  togglingId?: string;
  toggleError?: unknown;
  emptyAction?: ReactNode;
  footer?: ReactNode;
}) {
  // Unpublishing takes a live page off the customer site, so it is confirmed.
  // Publishing is not — it is the recoverable direction.
  const [pendingUnpublish, setPendingUnpublish] = useState<CmsPageResponse | null>(null);
  const [confirmed, setConfirmed] = useState(false);
  const isUnpublishing = pendingUnpublish !== null && togglingId === pendingUnpublish.id;

  // Dismiss only once the request it started has finished, and only when it
  // succeeded — closing on click would hide both the in-flight state and the
  // failure reason. "Adjusting state when a prop changes"
  // (react.dev/learn/you-might-not-need-an-effect), not an effect:
  // shouldSettle is true for exactly one render (a confirmed toggle that has
  // just stopped being pending), and comparing against its own last-seen
  // value during render is what fires the close-out exactly once per settle
  // rather than on every render where both conditions still hold.
  const shouldSettle = confirmed && !isUnpublishing;
  const [wasSettling, setWasSettling] = useState(false);
  if (shouldSettle !== wasSettling) {
    setWasSettling(shouldSettle);
    if (shouldSettle) {
      setConfirmed(false);
      if (!toggleError) setPendingUnpublish(null);
    }
  }

  const columns: DataTableColumn<CmsPageResponse>[] = [
    {
      key: "title",
      header: "Title",
      cell: (page) => <span className="font-medium text-fg">{page.title}</span>,
    },
    { key: "slug", header: "Slug", cell: (page) => <span className="text-fg-muted">/{page.slug}</span> },
    { key: "placement", header: "Placement", cell: (page) => formatPlacement(page.placement) },
    {
      key: "schedule",
      header: "Schedule",
      cell: (page) => <span className="nums whitespace-nowrap">{formatSchedule(page)}</span>,
    },
    { key: "status", header: "Status", cell: (page) => <CmsStatusBadge status={page.status} /> },
  ];

  return (
    <>
      <DataTable
        title="Pages"
        description="Search and manage every static page (SRS 12.16.1)."
        columns={columns}
        rows={pages}
        rowKey={(page) => page.id}
        isLoading={isLoading}
        isFetching={isFetching}
        error={error}
        onRetry={onRetry}
        caption="Static CMS pages"
        emptyTitle="No pages match the current filters"
        emptyDescription="Clear the filters, or create a page above."
        emptyAction={emptyAction}
        skeletonRows={6}
        minWidth="960px"
        footer={footer}
        rowActions={
          canWrite
            ? (page) => {
                const published = page.status === CmsContentStatus.Published;
                return (
                  <>
                    <Button type="button" size="sm" variant="ghost" onClick={() => onEdit(page)}>
                      Edit
                    </Button>
                    <Button
                      type="button"
                      size="sm"
                      variant={published ? "secondary" : "subtle"}
                      disabled={togglingId === page.id}
                      loading={togglingId === page.id && !published}
                      onClick={() => (published ? setPendingUnpublish(page) : onTogglePublished(page))}
                    >
                      {published ? "Unpublish" : "Publish"}
                    </Button>
                  </>
                );
              }
            : undefined
        }
      />

      <ConfirmDialog
        open={pendingUnpublish !== null}
        title="Unpublish this page?"
        description="It stops being reachable on the customer site immediately. The content is kept as a draft."
        confirmLabel="Unpublish"
        cancelLabel="Keep published"
        loading={isUnpublishing}
        error={toggleError ? describeError(toggleError) : null}
        onCancel={() => {
          setConfirmed(false);
          setPendingUnpublish(null);
        }}
        onConfirm={() => {
          if (!pendingUnpublish) return;
          setConfirmed(true);
          onTogglePublished(pendingUnpublish);
        }}
      >
        {pendingUnpublish ? (
          <p className="text-sm text-fg-muted">
            <span className="font-medium text-fg">{pendingUnpublish.title}</span> at /{pendingUnpublish.slug}.
          </p>
        ) : null}
      </ConfirmDialog>
    </>
  );
}
