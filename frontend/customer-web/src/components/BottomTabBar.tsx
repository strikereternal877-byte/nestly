"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import type { ReactNode } from "react";
import { cx } from "@/components/ui";
import { useFeatureFlags } from "@/lib/feature-flags";

/**
 * Thumb-zone bottom navigation for phone widths (task #342).
 *
 * customer-web's only nav before this was `SiteHeader`'s hamburger drawer —
 * fine for the dozen account-management destinations it holds, but it puts
 * every one of them, including the five a customer reaches constantly, one
 * extra tap behind a slide-over. This supplements rather than replaces that
 * drawer: the five primary destinations live here, thumb-reachable on every
 * screen; the drawer keeps the secondary ones (addresses, refer & earn,
 * support, AMC, subscription, recurring bookings — see `SiteHeader`'s
 * `ACCOUNT_LINKS`). `SiteHeader` itself stays mounted and unchanged above
 * `md`, where this bar never renders.
 *
 * Same fixed/`md:hidden`/safe-area shape as provider-web's `ProviderTabBar`
 * for cross-app consistency, with an active-state top indicator added (a
 * bottom-anchored echo of `Tabs`'/`SiteHeader`'s own underline idiom) since
 * five items packed this close together need more than a colour shift to
 * read as "selected" at a glance.
 */
const TAB_ITEMS = [
  { key: "home", href: "/", label: "Home", icon: <HomeIcon /> },
  { key: "search", href: "/search", label: "Search", icon: <SearchIcon /> },
  { key: "bookings", href: "/bookings", label: "Bookings", icon: <BookingsIcon /> },
  { key: "wallet", href: "/wallet", label: "Wallet", icon: <WalletIcon />, flagKey: "walletEnabled" },
  { key: "profile", href: "/profile", label: "Profile", icon: <ProfileIcon /> },
] as const;

/** Tailwind only picks up class names it can see as literal strings, so the grid-column count per visible-item count is spelled out rather than interpolated. */
const GRID_COLS_CLASS: Record<number, string> = {
  4: "grid-cols-4",
  5: "grid-cols-5",
};

/**
 * Routes where a fixed `StickyActionBar` (or, for the auth screens, a
 * centered card with its own submit button) already claims the bottom of the
 * viewport — the tab bar would either fight it for the same thumb-reach strip
 * mid-task or add navigation noise to a single-purpose form/checkout screen
 * that isn't one of the five destinations here anyway. `/bookings` itself (the
 * list — a primary destination) is deliberately excluded from the
 * `/bookings/` prefix check below.
 */
function hideOnRoute(pathname: string): boolean {
  if (pathname === "/login" || pathname === "/register" || pathname === "/forgot-password") {
    return true;
  }
  if (pathname.startsWith("/booking/")) return true; // summary, payment/[id], success/[id]
  if (pathname.startsWith("/bookings/") && pathname !== "/bookings") return true; // detail/track/reschedule/cancel/review
  if (pathname.startsWith("/addresses/")) return true; // new, [id]/edit
  if (pathname.startsWith("/recurring-bookings/new")) return true;
  if (pathname.startsWith("/amc/")) return true; // new, [id], [id]/redeem
  if (pathname.startsWith("/services/")) return true; // the "Book now" bar, or a meal-priced service's price bar
  return false;
}

export function BottomTabBar() {
  const pathname = usePathname();
  const flags = useFeatureFlags();
  if (hideOnRoute(pathname)) return null;

  const items = TAB_ITEMS.filter((item) => !("flagKey" in item) || flags[item.flagKey]);

  const isActive = (href: string) =>
    href === "/" ? pathname === "/" : pathname === href || pathname.startsWith(`${href}/`);

  return (
    // Distinct name from SiteHeader's own `<nav aria-label="Primary">` -
    // two landmarks sharing one name are indistinguishable to a screen
    // reader's landmark list.
    <nav
      aria-label="Bottom navigation"
      className={cx(
        "fixed inset-x-0 bottom-0 z-40 grid border-t border-line bg-surface/95 backdrop-blur-md md:hidden",
        GRID_COLS_CLASS[items.length] ?? "grid-cols-5",
        "supports-[padding:max(0px)]:pb-[max(0px,env(safe-area-inset-bottom))]",
      )}
    >
      {items.map((item) => {
        const active = isActive(item.href);
        return (
          <Link
            key={item.key}
            href={item.href}
            aria-current={active ? "page" : undefined}
            className={cx(
              "relative flex flex-col items-center gap-1 py-2.5 text-[0.6875rem] font-medium transition-colors duration-fast ease-out",
              active ? "text-brand-600 dark:text-brand-400" : "text-fg-subtle hover:text-fg",
            )}
          >
            {active ? (
              <span
                aria-hidden
                className="absolute inset-x-5 top-0 h-0.5 rounded-full bg-brand-600 dark:bg-brand-400"
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

function HomeIcon(): ReactNode {
  return (
    <svg {...ICON_PROPS}>
      <path d="M4 11.5 12 5l8 6.5V19a1 1 0 0 1-1 1h-4v-5h-6v5H5a1 1 0 0 1-1-1v-7.5Z" />
    </svg>
  );
}

function SearchIcon(): ReactNode {
  return (
    <svg {...ICON_PROPS}>
      <circle cx="11" cy="11" r="7" />
      <path d="m20 20-3.5-3.5" />
    </svg>
  );
}

function BookingsIcon(): ReactNode {
  return (
    <svg {...ICON_PROPS}>
      <rect x="3" y="5" width="18" height="16" rx="2" />
      <path d="M8 3v4M16 3v4M3 10h18M8 14h.01M12 14h.01M16 14h.01M8 17h.01M12 17h.01" />
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

function ProfileIcon(): ReactNode {
  return (
    <svg {...ICON_PROPS}>
      <circle cx="12" cy="8" r="3.5" />
      <path d="M5 20a7 7 0 0 1 14 0" />
    </svg>
  );
}
