"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { getVisibleNavModules } from "@/lib/permissions";
import type { NavModuleKey } from "@/lib/permissions";
import { cx } from "@/components/ui";
import type { AdminSessionClaims } from "@/lib/types";

/**
 * Sidebar nav, filtered by the current admin's role/permissions (task 98c).
 * See lib/permissions.ts for the filtering rule and why some of these links
 * currently 404 - the pages are separate, not-yet-implemented tasks.
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
  { label: "Operations", keys: ["bookings", "slots", "support", "reviews"] },
  { label: "Catalog", keys: ["catalog", "pricing", "serviceability"] },
  { label: "People", keys: ["customers", "provider", "admin-users"] },
  { label: "Growth", keys: ["coupons", "referral", "nestly-coins", "subscription"] },
  { label: "Content", keys: ["cms", "notifications"] },
  { label: "System", keys: ["settings", "audit"] },
];

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
      className="flex w-64 shrink-0 flex-col gap-6 overflow-y-auto border-r border-line bg-surface p-4"
    >
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
                    "relative rounded-lg px-3 py-2 text-sm transition-colors duration-fast ease-out",
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
                  {module.label}
                </Link>
              );
            })}
          </div>
        </div>
      ))}
    </nav>
  );
}
