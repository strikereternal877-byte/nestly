"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import type { ReactNode } from "react";
import { cx } from "@/components/ui";

/**
 * Navigation for the partner portal. Unlike admin-web's AdminSidebar, there
 * is no role/permission model to filter by - every signed-in partner sees
 * the same fixed set of sections (docs/PARTNER.md's partner-facing API
 * surface: profile, availability, jobs, earnings).
 *
 * Two presentations of one list. Partners work from a phone in the field, so
 * below `md` this renders as a thumb-reachable bottom tab bar rather than a
 * drawer they would have to open to reach the job they are standing in front
 * of; from `md` up it is a conventional side rail.
 */
const NAV_ITEMS = [
  { key: "jobs", href: "/jobs", label: "Jobs", icon: <BriefcaseIcon /> },
  { key: "availability", href: "/availability", label: "Availability", icon: <CalendarIcon /> },
  { key: "earnings", href: "/earnings", label: "Earnings", icon: <WalletIcon /> },
  { key: "profile", href: "/profile", label: "Profile", icon: <UserIcon /> },
] as const;

function useActiveMatcher() {
  const pathname = usePathname();
  return (href: string) => pathname === href || pathname.startsWith(`${href}/`);
}

/** Side rail, `md` and up. */
export function PartnerSidebar() {
  const isActive = useActiveMatcher();

  return (
    <nav
      aria-label="Partner sections"
      className="hidden w-60 shrink-0 flex-col gap-0.5 border-r border-line bg-surface p-4 md:flex"
    >
      {NAV_ITEMS.map((item) => {
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
export function PartnerTabBar() {
  const isActive = useActiveMatcher();

  return (
    <nav
      aria-label="Partner sections"
      className="fixed inset-x-0 bottom-0 z-40 grid grid-cols-4 border-t border-line bg-surface/95 pb-[env(safe-area-inset-bottom)] backdrop-blur-md md:hidden"
    >
      {NAV_ITEMS.map((item) => {
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
            {item.icon}
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
