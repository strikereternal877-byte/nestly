"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import type { ReactNode } from "react";
import { getVisibleNavModules } from "@/lib/permissions";
import type { NavModuleKey } from "@/lib/permissions";
import { cx } from "@/components/ui";
import type { AdminSessionClaims } from "@/lib/types";

/**
 * Sidebar nav, filtered by the current admin's role/permissions (task 98c).
 * See lib/permissions.ts for the filtering rule.
 *
 * Every NAV_MODULES href now resolves to a real page - verified in task 228,
 * which is when the note that used to sit here (several links 404, pages not
 * yet implemented) stopped being true. Adding a NavModule without its route is
 * still the way to reintroduce a dead link; it now lands on the branded
 * not-found.tsx rather than Next's bare default, but that is a fallback and
 * not a licence to ship one.
 *
 * Twenty modules in one flat list is not navigable, so they are grouped here
 * rather than in lib/permissions.ts: grouping is presentation, and keeping it
 * out of the permission model means the authorization rule stays the single
 * thing that file is responsible for. A module missing from GROUPS still
 * renders, under "More" — so adding a NavModule can never make it disappear
 * from the nav.
 */
const GROUPS: readonly { label: string; keys: readonly NavModuleKey[] }[] = [
  { label: "Overview", keys: ["dashboard", "reports"] },
  { label: "Operations", keys: ["fulfilment", "bookings", "payments", "slots", "support", "chat", "reviews"] },
  { label: "Catalog", keys: ["catalog", "pricing", "serviceability"] },
  { label: "People", keys: ["customers", "provider", "provider-referral", "admin-users"] },
  { label: "Growth", keys: ["coupons", "referral", "nestly-coins", "subscription"] },
  { label: "Content", keys: ["cms", "landing", "notifications"] },
  { label: "System", keys: ["settings", "audit"] },
];

const ICON_PROPS = {
  viewBox: "0 0 24 24",
  fill: "none",
  stroke: "currentColor",
  strokeWidth: "1.75",
  strokeLinecap: "round",
  strokeLinejoin: "round",
  className: "h-[18px] w-[18px] shrink-0",
  "aria-hidden": true,
} as const;

/** One glyph per module — decorative only, purely visual navigation aid. */
const MODULE_ICONS: Record<NavModuleKey, ReactNode> = {
  dashboard: (
    <svg {...ICON_PROPS}>
      <rect x="3.5" y="3.5" width="7.5" height="7.5" rx="1.5" />
      <rect x="13" y="3.5" width="7.5" height="4.5" rx="1.5" />
      <rect x="13" y="10.5" width="7.5" height="10" rx="1.5" />
      <rect x="3.5" y="13.5" width="7.5" height="7" rx="1.5" />
    </svg>
  ),
  reports: (
    <svg {...ICON_PROPS}>
      <path d="M4 20V10M12 20V4M20 20v-6" />
    </svg>
  ),
  fulfilment: (
    <svg {...ICON_PROPS}>
      <rect x="3" y="4.5" width="4.5" height="15" rx="1.2" />
      <rect x="9.75" y="4.5" width="4.5" height="15" rx="1.2" />
      <rect x="16.5" y="4.5" width="4.5" height="9" rx="1.2" />
    </svg>
  ),
  bookings: (
    <svg {...ICON_PROPS}>
      <rect x="4.5" y="4" width="15" height="17" rx="2" />
      <path d="M9 3.5h6M8 10h8M8 14h8M8 18h4.5" />
    </svg>
  ),
  payments: (
    <svg {...ICON_PROPS}>
      <rect x="2.5" y="5.5" width="19" height="13" rx="2.25" />
      <path d="M2.5 10h19M6 15h4" />
    </svg>
  ),
  slots: (
    <svg {...ICON_PROPS}>
      <rect x="3.5" y="4.5" width="17" height="16" rx="2" />
      <path d="M3.5 9.5h17M8 2.5v4M16 2.5v4" />
      <path d="M12 13v3.2l2 1.2" />
    </svg>
  ),
  support: (
    <svg {...ICON_PROPS}>
      <circle cx="12" cy="12" r="8.5" />
      <circle cx="12" cy="12" r="3.5" />
      <path d="m6.2 6.2 3.4 3.4M18 6l-3.5 3.6M18 18l-3.5-3.4M6.2 17.8l3.4-3.4" />
    </svg>
  ),
  chat: (
    <svg {...ICON_PROPS}>
      <path d="M4 5.5h16v11H9.5L5 20.5V16.5H4z" />
    </svg>
  ),
  reviews: (
    <svg {...ICON_PROPS} fill="currentColor" stroke="none">
      <path d="M12 3.5 14.6 9l6 .9-4.3 4.2 1 6-5.3-2.8-5.3 2.8 1-6L3.4 9.9l6-.9Z" />
    </svg>
  ),
  catalog: (
    <svg {...ICON_PROPS}>
      <path d="M12 3.5 3.5 8 12 12.5 20.5 8Z" />
      <path d="m3.5 12 8.5 4.5L20.5 12M3.5 16l8.5 4.5L20.5 16" />
    </svg>
  ),
  pricing: (
    <svg {...ICON_PROPS}>
      <path d="M12.5 3.5H20v7.5L11 20 3.5 12.5Z" />
      <circle cx="16" cy="7.5" r="1.4" fill="currentColor" stroke="none" />
    </svg>
  ),
  serviceability: (
    <svg {...ICON_PROPS}>
      <path d="M12 21s7-6.1 7-11.5A7 7 0 0 0 5 9.5C5 14.9 12 21 12 21Z" />
      <circle cx="12" cy="9.5" r="2.5" />
    </svg>
  ),
  customers: (
    <svg {...ICON_PROPS}>
      <circle cx="9" cy="8" r="3.2" />
      <path d="M2.8 19.5a6.3 6.3 0 0 1 12.4 0" />
      <path d="M16 5.3a3.2 3.2 0 0 1 0 6.2M18.6 19.5a6.3 6.3 0 0 0-3.4-5.6" />
    </svg>
  ),
  provider: (
    <svg {...ICON_PROPS}>
      <rect x="3" y="7.5" width="18" height="12" rx="2" />
      <path d="M8.5 7.5v-2a2 2 0 0 1 2-2h3a2 2 0 0 1 2 2v2M3 12.5h18" />
    </svg>
  ),
  "provider-referral": (
    <svg {...ICON_PROPS}>
      <circle cx="6" cy="12" r="2.6" />
      <circle cx="17.5" cy="6" r="2.6" />
      <circle cx="17.5" cy="18" r="2.6" />
      <path d="m8.3 10.7 6.9-3.4M8.3 13.3l6.9 3.4" />
    </svg>
  ),
  "admin-users": (
    <svg {...ICON_PROPS}>
      <circle cx="9.5" cy="8" r="3.2" />
      <path d="M3.5 19.5a6.3 6.3 0 0 1 12 0" />
      <path d="M17 4.3a3 3 0 1 1 0 5.9M19.5 9.5a3.4 3.4 0 0 1 1.9 3" />
    </svg>
  ),
  coupons: (
    <svg {...ICON_PROPS}>
      <path d="M3.5 9V7a2 2 0 0 1 2-2h13a2 2 0 0 1 2 2v2a2 2 0 0 0 0 6v2a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2v-2a2 2 0 0 0 0-6Z" />
      <path d="M9.5 5v14" strokeDasharray="1.8 2.2" />
    </svg>
  ),
  referral: (
    <svg {...ICON_PROPS}>
      <rect x="3.5" y="9.5" width="17" height="11" rx="1.5" />
      <path d="M12 9.5V21M3.5 14h17" />
      <path d="M12 9.5c-2.5 0-4-1.4-4-3a2.2 2.2 0 0 1 4 0 2.2 2.2 0 0 1 4 0c0 1.6-1.5 3-4 3Z" />
    </svg>
  ),
  "nestly-coins": (
    <svg {...ICON_PROPS}>
      <circle cx="12" cy="12" r="8.5" />
      <path d="M12 7.5v9M14.7 9.7c0-1-.9-1.7-2.2-1.7-1.4 0-2.4.7-2.4 1.7s.9 1.4 2.4 1.7c1.5.3 2.4.8 2.4 1.9 0 1-1 1.7-2.4 1.7-1.3 0-2.2-.7-2.2-1.7" />
    </svg>
  ),
  subscription: (
    <svg {...ICON_PROPS}>
      <path d="M4 12a8 8 0 0 1 13.6-5.7L20 8.5M20 12a8 8 0 0 1-13.6 5.7L4 15.5" />
      <path d="M20 4v4.5h-4.5M4 20v-4.5H8.5" />
    </svg>
  ),
  cms: (
    <svg {...ICON_PROPS}>
      <path d="M6.5 2.5h8l4 4v14.5h-12z" />
      <path d="M14 2.5V7h4.5" />
      <path d="M9 12.5h6M9 16h6" />
    </svg>
  ),
  landing: (
    <svg {...ICON_PROPS}>
      <rect x="3" y="4" width="18" height="16" rx="2" />
      <path d="M3 9h18" />
      <path d="M7 13h4v4H7zM14 13h3M14 16h3" />
    </svg>
  ),
  notifications: (
    <svg {...ICON_PROPS}>
      <path d="M6 10a6 6 0 0 1 12 0c0 4 1.5 5.5 1.5 5.5h-15S6 14 6 10Z" />
      <path d="M9.7 19.5a2.4 2.4 0 0 0 4.6 0" />
    </svg>
  ),
  settings: (
    <svg {...ICON_PROPS}>
      <circle cx="12" cy="12" r="3.2" />
      <path d="M12 3.5v2.2M12 18.3v2.2M20.5 12h-2.2M5.7 12H3.5M17.8 6.2l-1.5 1.5M7.7 16.3l-1.5 1.5M17.8 17.8l-1.5-1.5M7.7 7.7 6.2 6.2" />
    </svg>
  ),
  audit: (
    <svg {...ICON_PROPS}>
      <path d="M12 3 4.5 6v6c0 5 3.2 7.8 7.5 9 4.3-1.2 7.5-4 7.5-9V6Z" />
      <path d="m9 12 2 2 4-4.2" />
    </svg>
  ),
};

/** Brand mark + wordmark shown once, at the top of the sidebar rail. */
function SidebarBrand() {
  return (
    <div className="flex items-center gap-2 px-3 pb-5 pt-1">
      <span
        aria-hidden
        className="flex h-9 w-9 items-center justify-center rounded-xl bg-brand-gradient text-fg-on-brand shadow-brand"
      >
        <svg viewBox="0 0 24 24" fill="none" className="h-5 w-5">
          <path
            d="M4 11.5 12 5l8 6.5V19a1 1 0 0 1-1 1h-4v-5h-6v5H5a1 1 0 0 1-1-1v-7.5Z"
            fill="currentColor"
          />
        </svg>
      </span>
      <span className="text-[0.9375rem] font-semibold tracking-tight text-fg">
        Glavyx <span className="text-fg-muted">Admin</span>
      </span>
    </div>
  );
}

export function AdminSidebar({
  claims,
  onNavigate,
}: {
  claims: AdminSessionClaims | null;
  /** Set by the mobile drawer so following a link closes it. */
  onNavigate?: () => void;
}) {
  const pathname = usePathname();
  const modules = getVisibleNavModules(claims);
  const byKey = new Map(modules.map((module) => [module.key, module]));

  const grouped = GROUPS.map((group) => ({
    label: group.label,
    modules: group.keys.map((key) => byKey.get(key)).filter((module) => module !== undefined),
  })).filter((group) => group.modules.length > 0);

  const groupedKeys = new Set(GROUPS.flatMap((group) => [...group.keys]));
  const ungrouped = modules.filter((module) => !groupedKeys.has(module.key));
  if (ungrouped.length > 0) {
    grouped.push({ label: "More", modules: ungrouped });
  }

  return (
    <nav
      aria-label="Admin sections"
      // bg-surface (white): the MatDash reference's `aside.menu-sidebar` is
      // explicitly `bg-white`. The tinted region there is the scrollable
      // content canvas behind the cards (`<main>`, not this rail).
      className="flex h-full w-64 shrink-0 flex-col overflow-y-auto border-r border-line bg-surface p-4"
    >
      <SidebarBrand />
      <div className="flex flex-1 flex-col gap-6">
        {grouped.map((group) => (
          <div key={group.label}>
            <p className="mb-1.5 px-3 text-[0.6875rem] font-semibold uppercase tracking-wider text-fg-subtle">
              {group.label}
            </p>
            <div className="flex flex-col gap-0.5">
              {group.modules.map((module) => {
                const isActive =
                  pathname === module.href || pathname.startsWith(`${module.href}/`);
                return (
                  <Link
                    key={module.key}
                    href={module.href}
                    onClick={onNavigate}
                    aria-current={isActive ? "page" : undefined}
                    className={cx(
                      "group relative flex items-center gap-3 rounded-lg px-3 py-2 text-sm transition-colors duration-fast ease-out",
                      isActive
                        ? "bg-brand-50 font-medium text-brand-700 dark:bg-brand-500/15 dark:text-brand-300"
                        : "text-fg-muted hover:bg-surface-2 hover:text-fg",
                    )}
                  >
                    {isActive ? (
                      <span
                        aria-hidden
                        className="absolute inset-y-1.5 left-0 w-0.5 rounded-full bg-brand-600 dark:bg-brand-400"
                      />
                    ) : null}
                    <span
                      className={cx(
                        "shrink-0 transition-colors duration-fast ease-out",
                        isActive
                          ? "text-brand-600 dark:text-brand-300"
                          : "text-fg-subtle group-hover:text-fg-muted",
                      )}
                    >
                      {MODULE_ICONS[module.key]}
                    </span>
                    <span className="min-w-0 truncate">{module.label}</span>
                  </Link>
                );
              })}
            </div>
          </div>
        ))}
      </div>
    </nav>
  );
}
