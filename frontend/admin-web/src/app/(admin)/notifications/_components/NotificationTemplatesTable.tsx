"use client";

import { useState } from "react";
import type { ReactNode } from "react";
import { ActiveBadge, ConfirmDialog, DataTable, formatDateTime } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { Badge, Button, Modal } from "@/components/ui";
import { describeError } from "@/lib/api";
import { previewNotificationTemplate } from "@/lib/notification-template-api";
import {
  NOTIFICATION_CHANNEL_LABELS,
  NOTIFICATION_EVENT_TYPE_LABELS,
  type NotificationTemplateResponse,
} from "@/lib/notification-template-types";
import { NotificationTemplatePreviewBody } from "./NotificationTemplatePreviewPanel";

/**
 * Lists every notification template row (SRS 12.17.1-2, tasks 126a-d):
 * event/channel/key, subject, active status, and when it was last touched.
 *
 * "Preview" opens the shared preview panel in a `Modal` bound to that saved
 * template's own subject/body via the `{id}/preview` endpoint. It used to
 * expand an extra `<tr colSpan={7}>` inside the table, which is not something
 * `DataTable` can express and which pushed the rest of the list off screen; the
 * template editor's own live-draft preview is a separate instance of the same
 * panel, wired in `page.tsx`.
 *
 * Deactivation goes through `ConfirmDialog` — it stops the template being used
 * for its event on that channel, which silently suppresses a whole class of
 * customer notification, and it was previously a single unconfirmed click with
 * no error surface at all.
 */
export function NotificationTemplatesTable({
  templates,
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
  templates: NotificationTemplateResponse[] | undefined;
  isLoading: boolean;
  isFetching?: boolean;
  error?: unknown;
  onRetry?: () => void;
  canWrite: boolean;
  onEdit: (template: NotificationTemplateResponse) => void;
  onToggleActive: (template: NotificationTemplateResponse) => void;
  togglingId?: string;
  toggleError?: unknown;
  emptyAction?: ReactNode;
}) {
  const [previewing, setPreviewing] = useState<NotificationTemplateResponse | null>(null);
  const [pendingDeactivate, setPendingDeactivate] = useState<NotificationTemplateResponse | null>(null);
  const [confirmed, setConfirmed] = useState(false);

  const isDeactivating = pendingDeactivate !== null && togglingId === pendingDeactivate.id;

  // Close the confirmation only once the deactivation it started has actually
  // finished, and only if it succeeded — otherwise a failure would dismiss the
  // dialog and leave the row looking active with no explanation on screen.
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

  const columns: DataTableColumn<NotificationTemplateResponse>[] = [
    {
      key: "event",
      header: "Event",
      cell: (template) => NOTIFICATION_EVENT_TYPE_LABELS[template.eventType] ?? "Unknown",
      // The list is unpaged — the table holds every template — so sorting here
      // sorts the whole data set rather than one visible page.
      sortValue: (template) => NOTIFICATION_EVENT_TYPE_LABELS[template.eventType],
    },
    {
      key: "channel",
      header: "Channel",
      cell: (template) => <Badge>{NOTIFICATION_CHANNEL_LABELS[template.channel] ?? "Unknown"}</Badge>,
      sortValue: (template) => NOTIFICATION_CHANNEL_LABELS[template.channel],
    },
    {
      key: "key",
      header: "Template key",
      cell: (template) => <span className="font-medium text-fg">{template.templateKey}</span>,
      sortValue: (template) => template.templateKey,
    },
    {
      key: "subject",
      header: "Subject",
      className: "max-w-xs truncate",
      cell: (template) =>
        template.subject ?? <span className="text-fg-subtle">— (SMS has no subject)</span>,
    },
    {
      key: "status",
      header: "Status",
      cell: (template) => <ActiveBadge active={template.isActive} />,
      sortValue: (template) => template.isActive,
    },
    {
      key: "updated",
      header: "Last updated",
      cell: (template) => <span className="nums whitespace-nowrap">{formatDateTime(template.updatedAtUtc)}</span>,
      sortValue: (template) => template.updatedAtUtc,
    },
  ];

  return (
    <>
      <DataTable
        title="Templates"
        description="Every (event, channel) template currently defined (SRS 12.17.1)."
        columns={columns}
        rows={templates}
        rowKey={(template) => template.id}
        isLoading={isLoading}
        isFetching={isFetching}
        error={error}
        onRetry={onRetry}
        caption="Notification templates matching the current filters"
        emptyTitle="No templates match the current filters"
        emptyDescription="Clear the filters, or create a template for an (event, channel) pair that has none."
        emptyAction={emptyAction}
        skeletonRows={6}
        minWidth="1080px"
        rowActions={(template) => (
          <>
            <Button type="button" size="sm" variant="ghost" onClick={() => setPreviewing(template)}>
              Preview
            </Button>
            {canWrite ? (
              <>
                <Button type="button" size="sm" variant="ghost" onClick={() => onEdit(template)}>
                  Edit
                </Button>
                <Button
                  type="button"
                  size="sm"
                  variant={template.isActive ? "secondary" : "subtle"}
                  disabled={togglingId === template.id}
                  loading={togglingId === template.id && !template.isActive}
                  onClick={() =>
                    template.isActive ? setPendingDeactivate(template) : onToggleActive(template)
                  }
                >
                  {template.isActive ? "Deactivate" : "Activate"}
                </Button>
              </>
            ) : null}
          </>
        )}
      />

      <Modal
        open={previewing !== null}
        onClose={() => setPreviewing(null)}
        size="lg"
        title={previewing ? `Preview: ${previewing.templateKey}` : "Preview"}
        description="Rendered against sample values — nothing is sent or persisted (SRS 12.17.2)."
      >
        {previewing ? (
          <NotificationTemplatePreviewBody
            channel={previewing.channel}
            subject={previewing.subject}
            body={previewing.body}
            render={(sampleVariables) => previewNotificationTemplate(previewing.id, { sampleVariables })}
          />
        ) : null}
      </Modal>

      <ConfirmDialog
        open={pendingDeactivate !== null}
        title="Deactivate this template?"
        description="Its event stops producing a message on this channel until a template is active again."
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
            <span className="font-medium text-fg">{pendingDeactivate.templateKey}</span> —{" "}
            {NOTIFICATION_EVENT_TYPE_LABELS[pendingDeactivate.eventType]} on{" "}
            {NOTIFICATION_CHANNEL_LABELS[pendingDeactivate.channel]}.
          </p>
        ) : null}
      </ConfirmDialog>
    </>
  );
}
