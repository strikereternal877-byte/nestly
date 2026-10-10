"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ErrorState } from "@/components/states";
import { Button, Card, EmptyState, PageHeading, Skeleton, cx } from "@/components/ui";
import { formatDateTime } from "@/lib/format";
import { listNotifications, markAllNotificationsRead, markNotificationRead } from "@/lib/notifications-api";
import { ProviderNotificationType, type ProviderNotification } from "@/lib/notifications-types";

const PAGE_SIZE = 20;

function labelFor(type: ProviderNotificationType): string {
  switch (type) {
    case ProviderNotificationType.JobOffered:
      return "New job";
    case ProviderNotificationType.JobRescheduled:
      return "Job moved";
    case ProviderNotificationType.JobUnassigned:
      return "Job removed";
    case ProviderNotificationType.JobCancelled:
      return "Job cancelled";
    case ProviderNotificationType.KycRejected:
      return "Document";
    case ProviderNotificationType.Suspended:
      return "Account";
    case ProviderNotificationType.PayoutProcessed:
      return "Payout";
    case ProviderNotificationType.SupportTicketReply:
      return "Support";
    case ProviderNotificationType.BankAccountApproved:
    case ProviderNotificationType.BankAccountRejected:
      return "Bank account";
    default:
      return "Notification";
  }
}

/**
 * Full notification history (Provider Management UX pass) - the header
 * bell's "See all" destination, and the only place a provider can review
 * something they dismissed or missed while the app was closed.
 */
export default function NotificationsPage() {
  const [page, setPage] = useState(1);
  const router = useRouter();
  const queryClient = useQueryClient();

  const query = useQuery({
    queryKey: ["provider-notifications", "list", page],
    queryFn: () => listNotifications(page, PAGE_SIZE),
  });

  const markReadMutation = useMutation({
    mutationFn: markNotificationRead,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["provider-notifications"] }),
  });

  const markAllReadMutation = useMutation({
    mutationFn: markAllNotificationsRead,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["provider-notifications"] }),
  });

  const handleOpen = (notification: ProviderNotification) => {
    if (!notification.isRead) {
      markReadMutation.mutate(notification.id);
    }
    if (notification.deepLinkPath) {
      router.push(notification.deepLinkPath);
    }
  };

  const unreadCount = query.data?.unreadCount ?? 0;

  return (
    <div className="flex w-full max-w-3xl animate-rise flex-col gap-6">
      <PageHeading
        title="Notifications"
        subtitle="Job offers, account updates and payouts - everything that needed your attention."
        actions={
          unreadCount > 0 ? (
            <Button variant="secondary" onClick={() => markAllReadMutation.mutate()} loading={markAllReadMutation.isPending}>
              Mark all read
            </Button>
          ) : undefined
        }
      />

      {query.isPending ? (
        <Card>
          <div className="flex flex-col gap-4" aria-hidden>
            {[0, 1, 2, 3].map((row) => (
              <Skeleton key={row} className="h-16 rounded-xl" />
            ))}
          </div>
        </Card>
      ) : query.isError ? (
        <Card>
          <ErrorState
            title="Couldn't load your notifications"
            error={query.error}
            onRetry={() => query.refetch()}
            isRetrying={query.isRefetching}
          />
        </Card>
      ) : query.data.items.length === 0 ? (
        <EmptyState
          title="No notifications yet"
          description="Job offers, KYC updates, account changes and payouts will show up here as they happen."
        />
      ) : (
        <Card flush>
          <ul className="divide-y divide-line">
            {query.data.items.map((item) => (
              <li key={item.id}>
                <button
                  type="button"
                  onClick={() => handleOpen(item)}
                  className={cx(
                    "flex w-full flex-col gap-1 px-5 py-4 text-left transition-colors duration-fast ease-out hover:bg-surface-2",
                    !item.isRead && "bg-brand-50/40 dark:bg-brand-500/5",
                  )}
                >
                  <div className="flex items-start justify-between gap-3">
                    <span className="text-xs font-medium uppercase tracking-wide text-fg-subtle">{labelFor(item.type)}</span>
                    <span className="shrink-0 text-xs text-fg-subtle">{formatDateTime(item.createdAtUtc)}</span>
                  </div>
                  <span className="text-[0.9375rem] font-semibold text-fg">{item.title}</span>
                  <span className="text-sm text-fg-muted">{item.body}</span>
                </button>
              </li>
            ))}
          </ul>
        </Card>
      )}

      {query.data && query.data.totalCount > PAGE_SIZE ? (
        <div className="flex items-center justify-between">
          <Button variant="secondary" disabled={page <= 1} onClick={() => setPage((current) => current - 1)}>
            Previous
          </Button>
          <span className="text-sm text-fg-muted">
            Page {query.data.page} of {Math.max(1, Math.ceil(query.data.totalCount / PAGE_SIZE))}
          </span>
          <Button
            variant="secondary"
            disabled={page * PAGE_SIZE >= query.data.totalCount}
            onClick={() => setPage((current) => current + 1)}
          >
            Next
          </Button>
        </div>
      ) : null}
    </div>
  );
}
