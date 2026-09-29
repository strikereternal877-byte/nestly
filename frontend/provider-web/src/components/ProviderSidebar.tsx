"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { usePathname } from "next/navigation";
import type { ReactNode } from "react";
import { cx } from "@/components/ui";
import { useFeatureFlags } from "@/lib/feature-flags";
import { listJobs } from "@/lib/jobs-api";
import { listInProgressJobs, listPendingOffers } from "@/lib/jobs-active";

/**
 * Navigation for the provider portal. Unlike admin-web's AdminSidebar, there
 * is no role/permission model to filter by - every signed-in provider sees
 * the same fixed set of sections (docs/PROVIDER.md's provider-facing API
 * surface: profile, availability, jobs, earnings).
 *
 * Two presentations of one list. Providers work from a phone in the field, so
 * below `md` this renders as a thumb-reachable bottom tab bar rather than a
 * drawer they would have to open to reach the job they are standing in front
 * of; from `md` up it is a conventional side rail.
 */
const NAV_ITEMS: readonly {
  key: string;
  href: string;
  label: string;
  icon: ReactNode;
  flagKey?: keyof import("@/lib/feature-flags").ProviderFeatureFlags;
  /** Side rail only - the phone tab bar is already full; on a phone the same screen is reached from Today. */
  railOnly?: boolean;
}[] = [
  { key: "today", href: "/today", label: "Today", icon: <TodayIcon /> },
  { key: "offers", href: "/offers", label: "Offers", icon: <OfferIcon />, flagKey: "offersScreenEnabled" },
  { key: "active", href: "/active", label: "Active", icon: <ActiveJobIcon /> },
  { key: "jobs", href: "/jobs", label: "Jobs", icon: <BriefcaseIcon /> },
  { key: "monthly", href: "/monthly", label: "Monthly clients", icon: <HomeRepeatIcon />, railOnly: true },
  { key: "availability", href: "/availability", label: "Availability", icon: <CalendarIcon /> },
  { key: "earnings", href: "/earnings", label: "Earnings", icon: <WalletIcon /> },
  { key: "profile", href: "/profile", label: "Profile", icon: <UserIcon /> },
];

/** Tailwind only picks up class names it can see as literal strings, so the grid-column count per visible-item count is spelled out rather than interpolated. */
const GRID_COLS_CLASS: Record<number, string> = {
  5: "grid-cols-5",
  6: "grid-cols-6",
  7: "grid-cols-7",
};

/** `NAV_ITEMS` filtered by each entry's optional `flagKey` (SRS 12.19 "Feature flags"), shared by the side rail and the bottom tab bar. */
function useVisibleNavItems() {
  const flags = useFeatureFlags();
  return NAV_ITEMS.filter((item) => !item.flagKey || flags[item.flagKey]);
}

function useActiveMatcher() {
  const pathname = usePathname();
  return (href: string) => pathname === href || pathname.startsWith(`${href}/`);
}

/**
 * Count of currently pending offers, for the nav badge next to "Offers"
 * (task from docs/OPEN-FIXES-FEATURES.csv, "Job offers with countdown" -
 * with no dedicated surface a provider could miss an offer entirely, so the
 * count needs to be visible from anywhere in the app, not only on `/offers`
 * itself). Reads the same `GET /jobs` query `/today`/`/offers`/`/jobs`
 * already fetch (identical key+queryFn), so this never issues a second
 * network call while one of those screens is mounted - TanStack Query's
 * cache is shared app-wide - and on any other screen it costs exactly the
 * one background fetch `/jobs` itself already pays on every visit.
 */
function usePendingOfferCount(): number {
  const query = useQuery({
    queryKey: ["provider-jobs", "", ""],
    queryFn: () => listJobs({}),
    // A 501 (job assignment not yet deployed) or any other fetch failure
    // just means "no count to show" - this is a nav decoration, not a
    // screen with its own error state to render.
    retry: false,
  });
  if (!query.data) return 0;
  return listPendingOffers(query.data).length;
}

/** Same reasoning as {@link usePendingOfferCount}, for the "Active" tab - shares the same cached `/jobs` fetch. */
function useActiveJobCount(): number {
  const query = useQuery({
    queryKey: ["provider-jobs", "", ""],
    queryFn: () => listJobs({}),
    retry: false,
  });
  if (!query.data) return 0;
  return listInProgressJobs(query.data).length;
}

/** Count pill for a nav item. `tone="danger"` is reserved for a countdown a provider can lose (an unanswered offer); "Active" uses `tone="brand"` since a job in progress is a status, not something urgently at risk. */
function NavCountBadge({ count, tone, label }: { count: number; tone: "danger" | "brand"; label: string }) {
  if (count <= 0) return null;
  return (
    <span
      className={cx(
        "ml-auto flex h-4 min-w-4 items-center justify-center rounded-full px-1 text-[10px] font-semibold text-white",
        tone === "danger" ? "bg-danger" : "bg-brand-600",
      )}
      aria-label={label}
    >
      {count > 9 ? "9+" : count}
    </span>
  );
}

/** Same count/tone as {@link NavCountBadge}, styled as a corner dot for the icon-only tab bar instead of an inline pill. */
function TabBarDot({ count, tone }: { count: number; tone: "danger" | "brand" }) {
  if (count <= 0) return null;
  return (
    <span
      aria-hidden
      className={cx(
        "absolute -right-1 -top-1 flex h-3.5 min-w-3.5 items-center justify-center rounded-full px-0.5 text-[9px] font-semibold text-white",
        tone === "danger" ? "bg-danger" : "bg-brand-600",
      )}
    >
      {count > 9 ? "9+" : count}
    </span>
  );
}

/**
 * True for `/jobs/{id}` - not `/jobs` itself. That screen's primary actions
 * live in a `StickyActionBar` (task #345), which occupies the same
 * thumb-reach real estate at the bottom of the viewport as this tab bar; a
 * native app hides its bottom tabs on a task-focused detail screen for the
 * same reason. Exported so `AuthenticatedLayout` can give that one route its
 * own (taller) bottom padding instead of the tab bar's.
 */
export function isJobDetailPath(pathname: string | null): boolean {
  return /^\/jobs\/[^/]+\/?$/.test(pathname ?? "");
}

/** Brand mark + wordmark shown once, at the top of the sidebar rail. */
function SidebarBrand() {
  return (
    <div className="flex items-center gap-2 px-3 pb-5 pt-1">
      <span
        aria-hidden
        className="flex h-9 w-9 items-center justify-center rounded-xl bg-brand-gradient text-fg-on-brand shadow-brand"
      >
        <svg viewBox="0 0 24 24" fill="none" className="h-5 w-5">
          <circle
            cx="12"
            cy="12"
            r="9"
            stroke="currentColor"
            strokeWidth="4.5"
            strokeLinecap="round"
            strokeDasharray="44 13"
            transform="rotate(40 12 12)"
          />
          <line x1="13.5" y1="12" x2="21" y2="12" stroke="currentColor" strokeWidth="4.5" strokeLinecap="round" />
        </svg>
      </span>
      <span className="text-[0.9375rem] font-semibold tracking-tight text-fg">
        Glavyx <span className="text-fg-muted">Provider</span>
      </span>
    </div>
  );
}

/** Side rail, `md` and up. */
export function ProviderSidebar() {
  const isActive = useActiveMatcher();
  const pendingOfferCount = usePendingOfferCount();
  const activeJobCount = useActiveJobCount();
  const navItems = useVisibleNavItems();

  return (
    <nav
      aria-label="Provider sections"
      // bg-surface (white): the MatDash reference's `aside.menu-sidebar` is
      // explicitly `bg-white`. The tinted region there is the scrollable
      // content canvas behind the cards (`<main>`, not this rail).
      className="sticky top-0 hidden h-screen w-60 shrink-0 flex-col gap-0.5 overflow-y-auto border-r border-line bg-surface p-4 md:flex"
    >
      <SidebarBrand />
      {navItems.map((item) => {
        const active = isActive(item.href);
        return (
          <Link
            key={item.key}
            href={item.href}
            aria-current={active ? "page" : undefined}
            className={cx(
              "relative flex items-center gap-3 rounded-lg px-3 py-2.5 text-sm transition-colors duration-fast ease-out",
              active
                ? "bg-brand-50 font-medium text-brand-700 dark:bg-brand-500/15 dark:text-brand-300"
                : "text-fg-muted hover:bg-surface-2 hover:text-fg",
            )}
          >
            {active ? (
              <span
                aria-hidden
                className="absolute inset-y-2 left-0 w-0.5 rounded-full bg-brand-600 dark:bg-brand-400"
              />
            ) : null}
            {item.icon}
            {item.label}
            {item.key === "offers" ? (
              <NavCountBadge
                count={pendingOfferCount}
                tone="danger"
                label={`${pendingOfferCount} offer${pendingOfferCount === 1 ? "" : "s"} waiting`}
              />
            ) : null}
            {item.key === "active" ? (
              <NavCountBadge
                count={activeJobCount}
                tone="brand"
                label={`${activeJobCount} job${activeJobCount === 1 ? "" : "s"} in progress`}
              />
            ) : null}
          </Link>
        );
      })}
    </nav>
  );
}

/**
 * Bottom tab bar, below `md`. Sits above the safe-area inset so it clears the
 * iOS home indicator instead of hiding behind it.
 */
export function ProviderTabBar() {
  const pathname = usePathname();
  const isActive = useActiveMatcher();
  const pendingOfferCount = usePendingOfferCount();
  const activeJobCount = useActiveJobCount();
  const navItems = useVisibleNavItems().filter((item) => !item.railOnly);

  // See isJobDetailPath's comment - redundant with that screen's own sticky
  // action bar.
  if (isJobDetailPath(pathname)) return null;

  return (
    <nav
      aria-label="Provider sections"
      className={cx(
        "fixed inset-x-0 bottom-0 z-40 grid border-t border-line bg-surface/95 pb-[env(safe-area-inset-bottom)] backdrop-blur-md md:hidden",
        GRID_COLS_CLASS[navItems.length] ?? "grid-cols-6",
      )}
    >
      {navItems.map((item) => {
        const active = isActive(item.href);
        return (
          <Link
            key={item.key}
            href={item.href}
            aria-current={active ? "page" : undefined}
            className={cx(
              "flex flex-col items-center gap-1 px-1 py-2.5 text-[0.6875rem] font-medium transition-colors duration-fast ease-out",
              active ? "text-brand-600 dark:text-brand-400" : "text-fg-subtle hover:text-fg",
            )}
          >
            <span className="relative">
              {item.icon}
              {item.key === "offers" ? <TabBarDot count={pendingOfferCount} tone="danger" /> : null}
              {item.key === "active" ? <TabBarDot count={activeJobCount} tone="brand" /> : null}
            </span>
            {item.label}
          </Link>
        );
      })}
    </nav>
  );
}

const ICON_PROPS = {
  viewBox: "0 0 24 24",
  fill: "none",
  stroke: "currentColor",
  strokeWidth: "1.75",
  strokeLinecap: "round",
  strokeLinejoin: "round",
  className: "h-5 w-5 shrink-0",
  "aria-hidden": true,
} as const;

/** A house with a repeat arrow - a standing monthly engagement at one home. */
function HomeRepeatIcon(): ReactNode {
  return (
    <svg {...ICON_PROPS}>
      <path d="M4 11.5 12 5l8 6.5" />
      <path d="M6 10v9h12v-9" />
      <path d="M14.5 14.5a2.5 2.5 0 1 1-.9-1.9" />
      <path d="M14 11.5v1.6h-1.6" />
    </svg>
  );
}

function TodayIcon(): ReactNode {
  return (
    <svg {...ICON_PROPS}>
      <circle cx="12" cy="12" r="9" />
      <path d="M12 7v5l3.5 2" />
    </svg>
  );
}

/** An open envelope/inbox tray - what a fresh, unanswered offer looks like. */
function OfferIcon(): ReactNode {
  return (
    <svg {...ICON_PROPS}>
      <path d="M3 8.5 12 14l9-5.5" />
      <rect x="3" y="5.5" width="18" height="13" rx="2" />
    </svg>
  );
}

/** A location pin - "Active" is specifically about the job you're physically at/working on, unlike "Jobs"' briefcase (the full queue). */
function ActiveJobIcon(): ReactNode {
  return (
    <svg {...ICON_PROPS}>
      <path d="M12 21s7-6.5 7-11.5a7 7 0 0 0-14 0C5 14.5 12 21 12 21Z" />
      <circle cx="12" cy="9.5" r="2.5" />
    </svg>
  );
}

function BriefcaseIcon(): ReactNode {
  return (
    <svg {...ICON_PROPS}>
      <rect x="3" y="7" width="18" height="13" rx="2" />
      <path d="M9 7V5.5A1.5 1.5 0 0 1 10.5 4h3A1.5 1.5 0 0 1 15 5.5V7M3 12h18" />
    </svg>
  );
}

function CalendarIcon(): ReactNode {
  return (
    <svg {...ICON_PROPS}>
      <rect x="3" y="5" width="18" height="16" rx="2" />
      <path d="M8 3v4M16 3v4M3 10h18" />
    </svg>
  );
}

function WalletIcon(): ReactNode {
  return (
    <svg {...ICON_PROPS}>
      <path d="M3 7a2 2 0 0 1 2-2h12v4M3 7v10a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-6a2 2 0 0 0-2-2H5a2 2 0 0 1-2-2Z" />
      <circle cx="16.5" cy="14" r="1.25" />
    </svg>
  );
}

function UserIcon(): ReactNode {
  return (
    <svg {...ICON_PROPS}>
      <circle cx="12" cy="8" r="3.5" />
      <path d="M5 20a7 7 0 0 1 14 0" />
    </svg>
  );
}
