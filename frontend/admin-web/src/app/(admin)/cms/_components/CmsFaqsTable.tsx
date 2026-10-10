"use client";

import { useState } from "react";
import type { ReactNode } from "react";
import { Button } from "@/components/ui";
import { ConfirmDialog, DataTable } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { describeError } from "@/lib/api";
import { CmsContentStatus, type CmsFaqResponse } from "@/lib/cms-types";
import { formatPlacement, formatSchedule } from "./cmsDisplay";
import { CmsStatusBadge } from "./CmsStatusBadge";

/**
 * Site-level FAQ list (SRS 12.16.1). Server-paged, so no sortable columns —
 * see `CmsPagesTable`.
 */
export function CmsFaqsTable({
  faqs,
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
  faqs: CmsFaqResponse[] | undefined;
  isLoading: boolean;
  isFetching?: boolean;
  error?: unknown;
  onRetry?: () => void;
  canWrite: boolean;
  onEdit: (faq: CmsFaqResponse) => void;
  onTogglePublished: (faq: CmsFaqResponse) => void;
  togglingId?: string;
  toggleError?: unknown;
  emptyAction?: ReactNode;
  footer?: ReactNode;
}) {
  const [pendingUnpublish, setPendingUnpublish] = useState<CmsFaqResponse | null>(null);
  const [confirmed, setConfirmed] = useState(false);
  const isUnpublishing = pendingUnpublish !== null && togglingId === pendingUnpublish.id;

  // "Adjusting state when a prop changes" (react.dev/learn/you-might-not-
  // need-an-effect), not an effect: shouldSettle is true for exactly one
  // render (a confirmed toggle that has just stopped being pending), and
  // comparing against its own last-seen value during render is what fires
  // the close-out exactly once per settle rather than on every render where
  // both conditions still hold.
  const shouldSettle = confirmed && !isUnpublishing;
  const [wasSettling, setWasSettling] = useState(false);
  if (shouldSettle !== wasSettling) {
    setWasSettling(shouldSettle);
    if (shouldSettle) {
      setConfirmed(false);
      if (!toggleError) setPendingUnpublish(null);
    }
  }

  const columns: DataTableColumn<CmsFaqResponse>[] = [
    {
      key: "question",
      header: "Question",
      className: "max-w-md",
      cell: (faq) => <span className="font-medium text-fg">{faq.question}</span>,
    },
    { key: "placement", header: "Placement", cell: (faq) => formatPlacement(faq.placement) },
    { key: "sortOrder", header: "Sort", numeric: true, cell: (faq) => faq.sortOrder },
    {
      key: "schedule",
      header: "Schedule",
      cell: (faq) => <span className="nums whitespace-nowrap">{formatSchedule(faq)}</span>,
    },
    { key: "status", header: "Status", cell: (faq) => <CmsStatusBadge status={faq.status} /> },
  ];

  return (
    <>
      <DataTable
        title="FAQs"
        description="Search and manage every site-level FAQ (SRS 12.16.1)."
        columns={columns}
        rows={faqs}
        rowKey={(faq) => faq.id}
        isLoading={isLoading}
        isFetching={isFetching}
        error={error}
        onRetry={onRetry}
        caption="Site-level FAQs"
        emptyTitle="No FAQs match the current filters"
        emptyDescription="Clear the filters, or create an FAQ above."
        emptyAction={emptyAction}
        skeletonRows={6}
        minWidth="960px"
        footer={footer}
        rowActions={
          canWrite
            ? (faq) => {
                const published = faq.status === CmsContentStatus.Published;
                return (
                  <>
                    <Button type="button" size="sm" variant="ghost" onClick={() => onEdit(faq)}>
                      Edit
                    </Button>
                    <Button
                      type="button"
                      size="sm"
                      variant={published ? "secondary" : "subtle"}
                      disabled={togglingId === faq.id}
                      loading={togglingId === faq.id && !published}
                      onClick={() => (published ? setPendingUnpublish(faq) : onTogglePublished(faq))}
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
        title="Unpublish this FAQ?"
        description="It disappears from the customer-facing help content immediately."
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
          <p className="text-sm text-fg-muted">{pendingUnpublish.question}</p>
        ) : null}
      </ConfirmDialog>
    </>
  );
}
