"use client";

import { useEffect, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { cx } from "@/components/ui";
import { listNotifications, markAllNotificationsRead, markNotificationRead } from "@/lib/notifications-api";
import { ProviderNotificationType, type ProviderNotification } from "@/lib/notifications-types";
import { formatRelativeDate } from "@/lib/format";

/**
 * Header notification bell + popup, matched to the MatDash reference
 * (matdash-nextjs-minisidebar.vercel.app dashboard header's bell menu):
 * 360px panel, "N new" pill next to the title, 44px tinted icon chips per
 * row, title/description/time layout, "See all" footer.
 *
 * Provider Management UX pass: this used to be a UI shell only - there was
 * no backend concept of a notification feed belonging to the signed-in
 * provider (only customer-facing `NotificationTemplate`s existed). Now backed
 * by `/api/v1/notifications` (`ProviderNotificationService`), populated by
 * `ProviderNotificationPublisher` at the moments a provider needs to know
 * about *now* - a new job offer, a KYC rejection, a suspension, a payout
 * going through.
 */
const TONE_CHIP = {
  brand: "bg-brand-50 text-brand-600 dark:bg-brand-500/15 dark:text-brand-300",
  success: "bg-success-soft text-success",
  danger: "bg-danger-soft text-danger",
  warning: "bg-warning-soft text-warning",
} as const;

function toneFor(type: ProviderNotificationType): keyof typeof TONE_CHIP {
  switch (type) {
    case ProviderNotificationType.PayoutProcessed:
      return "success";
    case ProviderNotificationType.KycRejected:
    case ProviderNotificationType.Suspended:
    case ProviderNotificationType.JobUnassigned:
    case ProviderNotificationType.JobCancelled:
      return "danger";
    case ProviderNotificationType.JobRescheduled:
      return "warning";
    case ProviderNotificationType.JobOffered:
    default:
      return "brand";
  }
}

function BellIcon({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 24 24" fill="none" className={className} aria-hidden>
      <path
        d="M18.75 9.71V9c0-3.87-3.02-7-6.75-7S5.25 5.13 5.25 9v.71c0 .85-.24 1.67-.69 2.38l-1.11 1.72c-1.01 1.58-.24 3.72 1.53 4.21a26.6 26.6 0 0 0 14.04 0c1.76-.49 2.53-2.63 1.52-4.21l-1.11-1.72a4.3 4.3 0 0 1-.68-2.38Z"
        stroke="currentColor"
        strokeWidth="1.5"
      />
      <path
        d="M7.5 19c.66 1.75 2.42 3 4.5 3s3.84-1.25 4.5-3M12 6v4"
        stroke="currentColor"
        strokeWidth="1.5"
        strokeLinecap="round"
        opacity=".5"
      />
    </svg>
  );
}

export function NotificationBell() {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const router = useRouter();
  const queryClient = useQueryClient();

  // Polled rather than pushed - same tradeoff the "Offers"/"Active" nav
  // badges already make (see ProviderSidebar's usePendingOfferCount doc
  // comment): no SignalR channel exists for this feed, and a real push
  // already reached the device via ProviderNotificationPublisher regardless
  // of whether this panel is open.
  const query = useQuery({
    queryKey: ["provider-notifications", "recent"],
    queryFn: () => listNotifications(1, 8),
    refetchInterval: 60_000,
    retry: false,
  });

  const markReadMutation = useMutation({
    mutationFn: markNotificationRead,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["provider-notifications"] }),
  });

  const markAllReadMutation = useMutation({
    mutationFn: markAllNotificationsRead,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["provider-notifications"] }),
  });

  useEffect(() => {
    if (!open) return;

    const onPointerDown = (event: MouseEvent) => {
      if (!ref.current?.contains(event.target as Node)) setOpen(false);
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") setOpen(false);
    };

    document.addEventListener("mousedown", onPointerDown);
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("mousedown", onPointerDown);
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [open]);

  const notifications = query.data?.items ?? [];
  const unreadCount = query.data?.unreadCount ?? 0;

  const handleOpenNotification = (notification: ProviderNotification) => {
    if (!notification.isRead) {
      markReadMutation.mutate(notification.id);
    }
    setOpen(false);
    if (notification.deepLinkPath) {
      router.push(notification.deepLinkPath);
    }
  };

  return (
    <div ref={ref} className="relative">
      <button
        type="button"
        onClick={() => setOpen((current) => !current)}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label="Notifications"
        className="relative inline-flex h-10 w-10 items-center justify-center rounded-full text-fg-muted transition-colors duration-fast ease-out hover:bg-surface-3 hover:text-fg"
      >
        <BellIcon className="h-5 w-5" />
        {unreadCount > 0 ? (
          <span className="absolute right-1 top-1 flex h-4 min-w-4 items-center justify-center rounded-full bg-danger px-1 text-[10px] font-semibold text-white">
            {unreadCount > 9 ? "9+" : unreadCount}
          </span>
        ) : null}
      </button>

      {open ? (
        <div
          role="menu"
          aria-label="Notifications"
          className="absolute right-0 top-full z-50 mt-2 w-[360px] max-w-[calc(100vw-2rem)] animate-pop overflow-hidden rounded-sm bg-surface py-6 shadow-sm"
        >
          <div className="flex items-center justify-between px-6">
            <h3 className="text-lg font-semibold text-fg">Notifications</h3>
            <div className="flex items-center gap-2">
              {unreadCount > 0 ? (
                <span className="rounded-full bg-brand-600 px-2.5 py-0.5 text-xs font-semibold text-fg-on-brand">
                  {unreadCount} new
                </span>
              ) : null}
              {unreadCount > 0 ? (
                <button
                  type="button"
                  onClick={() => markAllReadMutation.mutate()}
                  className="text-xs font-medium text-brand-600 hover:underline dark:text-brand-400"
                >
                  Mark all read
                </button>
              ) : null}
            </div>
          </div>

          <div className="mt-3 max-h-80 overflow-y-auto">
            {notifications.length === 0 ? (
              <p className="px-6 py-10 text-center text-sm text-fg-muted">No notifications yet</p>
            ) : (
              notifications.map((item) => (
                <button
                  key={item.id}
                  type="button"
                  role="menuitem"
                  onClick={() => handleOpenNotification(item)}
                  className={cx(
                    "flex w-full items-center gap-4 px-6 py-3 text-left transition-colors duration-fast ease-out hover:bg-surface-2",
                    !item.isRead && "bg-brand-50/40 dark:bg-brand-500/5",
                  )}
                >
                  <span
                    className={cx(
                      "flex h-11 w-11 shrink-0 items-center justify-center rounded-full",
                      TONE_CHIP[toneFor(item.type)],
                    )}
                  >
                    <BellIcon className="h-5 w-5" />
                  </span>
                  <span className="flex w-full items-start justify-between gap-2">
                    <span className="min-w-0">
                      <span className="block text-[0.9375rem] font-semibold text-fg">{item.title}</span>
                      <span className="line-clamp-1 block text-sm text-fg-muted">{item.body}</span>
                    </span>
                    <span className="shrink-0 pt-0.5 text-xs text-fg-subtle">{formatRelativeDate(item.createdAtUtc)}</span>
                  </span>
                </button>
              ))
            )}
          </div>

          <div className="mt-2 border-t border-line px-6 pt-3">
            <a
              href="/notifications"
              className="block text-center text-sm font-medium text-brand-600 hover:underline dark:text-brand-400"
              onClick={() => setOpen(false)}
            >
              See all
            </a>
          </div>
        </div>
      ) : null}
    </div>
  );
}
