"use client";

import { useState } from "react";
import type { ReactNode } from "react";
import { Button } from "@/components/ui";
import { ConfirmDialog, DataTable } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { describeError } from "@/lib/api";
import { CmsContentStatus, CmsMediaType, type BannerResponse } from "@/lib/cms-types";
import { formatPlacement, formatSchedule } from "./cmsDisplay";
import { CmsStatusBadge } from "./CmsStatusBadge";

/**
 * Promotional banner list (SRS 12.16.1). Server-paged, so no sortable
 * columns — see `CmsPagesTable`.
 */
export function BannersTable({
  banners,
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
  banners: BannerResponse[] | undefined;
  isLoading: boolean;
  isFetching?: boolean;
  error?: unknown;
  onRetry?: () => void;
  canWrite: boolean;
  onEdit: (banner: BannerResponse) => void;
  onTogglePublished: (banner: BannerResponse) => void;
  togglingId?: string;
  toggleError?: unknown;
  emptyAction?: ReactNode;
  footer?: ReactNode;
}) {
  const [pendingUnpublish, setPendingUnpublish] = useState<BannerResponse | null>(null);
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

  const columns: DataTableColumn<BannerResponse>[] = [
    {
      key: "title",
      header: "Title",
      cell: (banner) => <span className="font-medium text-fg">{banner.title}</span>,
    },
    {
      key: "image",
      header: "Media",
      cell: (banner) => (
        <a
          href={banner.mediaUrl}
          target="_blank"
          rel="noreferrer"
          // The link text alone reads as "View" to a screen reader running a
          // link list, which is meaningless across a column of them.
          aria-label={`View the ${banner.mediaType === CmsMediaType.Video ? "video" : "image"} for ${banner.title} (opens in a new tab)`}
          className="rounded text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
        >
          View
        </a>
      ),
    },
    {
      key: "placement",
      header: "Placement",
      cell: (banner) =>
        banner.categoryName
          ? `${formatPlacement(banner.placement)} (${banner.categoryName})`
          : formatPlacement(banner.placement),
    },
    { key: "sortOrder", header: "Sort", numeric: true, cell: (banner) => banner.sortOrder },
    {
      key: "schedule",
      header: "Schedule",
      cell: (banner) => <span className="nums whitespace-nowrap">{formatSchedule(banner)}</span>,
    },
    { key: "status", header: "Status", cell: (banner) => <CmsStatusBadge status={banner.status} /> },
  ];

  return (
    <>
      <DataTable
        title="Banners"
        description="Search and manage every promotional banner (SRS 12.16.1)."
        columns={columns}
        rows={banners}
        rowKey={(banner) => banner.id}
        isLoading={isLoading}
        isFetching={isFetching}
        error={error}
        onRetry={onRetry}
        caption="Promotional banners"
        emptyTitle="No banners match the current filters"
        emptyDescription="Clear the filters, or create a banner above."
        emptyAction={emptyAction}
        skeletonRows={6}
        minWidth="1040px"
        footer={footer}
        rowActions={
          canWrite
            ? (banner) => {
                const published = banner.status === CmsContentStatus.Published;
                return (
                  <>
                    <Button type="button" size="sm" variant="ghost" onClick={() => onEdit(banner)}>
                      Edit
                    </Button>
                    <Button
                      type="button"
                      size="sm"
                      variant={published ? "secondary" : "subtle"}
                      disabled={togglingId === banner.id}
                      loading={togglingId === banner.id && !published}
                      onClick={() => (published ? setPendingUnpublish(banner) : onTogglePublished(banner))}
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
        title="Unpublish this banner?"
        description="It disappears from its placement on the customer site immediately."
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
            <span className="font-medium text-fg">{pendingUnpublish.title}</span> on{" "}
            {formatPlacement(pendingUnpublish.placement)}.
          </p>
        ) : null}
      </ConfirmDialog>
    </>
  );
}
