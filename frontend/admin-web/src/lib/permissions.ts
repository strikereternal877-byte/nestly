import type { AdminSessionClaims } from "./types";

/**
 * The admin panel's module list and permission-based nav filtering (SRS 12,
 * task 98c).
 *
 * Every module in SRS section 12 gets a nav entry here now, even though most
 * of their pages do not exist yet - each is a separate, not-yet-implemented
 * task. Linking to the expected route today means those tasks only need to
 * add `src/app/(admin)/<module>/page.tsx`; nothing here has to change.
 *
 * Permission model: `requiredPermission` is the exact grantable code from
 * `AdminPermissionCatalog.BuildCode` (backend/shared/Domain/AdminPermissionCatalog.cs)
 * - "{module}.read", one per `AdminModules` constant. Read is the correct
 * tier to gate nav visibility on: a role holding "{module}.write" always
 * holds "{module}.read" too (AdminPermissionCatalog's grant-building rule),
 * so this never hides a module from a role that can also edit it; write-gated
 * actions inside a module's own page check `.write` via `canWriteModule`
 * below instead.
 *
 * Task 96a has since landed and `AdminTokenService` now issues one
 * `AdminClaimTypes.Permission` ("permission") claim per granted code, so
 * `AdminSessionClaims.permissions` is populated with these exact strings for
 * any real login - this file previously used a placeholder "module:view"
 * shape from before that landed, which never matched a real token's claims
 * and silently hid every nav entry (this one included) for a real session.
 * `ROLE_MODULE_FALLBACK` below remains as the fallback for the
 * `permissions` array being empty (e.g. a token issued before this claim
 * existed, or a decoding failure) - keyed on the role claim SRS 12.1
 * guarantees exists. That fallback is a UX convenience, not a security
 * boundary: every one of these routes still requires its own valid
 * [Authorize] call server-side, exactly like RequireAdminAuth's client-side
 * guard is not itself the security boundary either.
 */

export type NavModuleKey =
  | "dashboard"
  | "customers"
  | "catalog"
  | "pricing"
  | "serviceability"
  | "slots"
  | "bookings"
  | "coupons"
  | "support"
  | "reviews"
  | "cms"
  | "notifications"
  | "reports"
  | "audit"
  | "settings"
  | "admin-users"
  | "provider"
  | "referral"
  | "nestly-coins"
  | "subscription";

export interface NavModule {
  key: NavModuleKey;
  label: string;
  href: string;
  /** SRS section this module corresponds to, for traceability. */
  srsRef: string;
  requiredPermission: string;
}

export const NAV_MODULES: readonly NavModule[] = [
  { key: "dashboard", label: "Dashboard", href: "/dashboard", srsRef: "SRS 12.3", requiredPermission: "dashboard.read" },
  { key: "customers", label: "Customers", href: "/customers", srsRef: "SRS 12.4", requiredPermission: "customers.read" },
  { key: "catalog", label: "Catalog", href: "/catalog", srsRef: "SRS 12.5-12.7", requiredPermission: "catalog.read" },
  { key: "pricing", label: "Pricing", href: "/pricing", srsRef: "SRS 12.8", requiredPermission: "pricing.read" },
  { key: "serviceability", label: "Serviceability", href: "/serviceability", srsRef: "SRS 12.9", requiredPermission: "serviceability.read" },
  { key: "slots", label: "Slots & Availability", href: "/slots", srsRef: "SRS 12.10", requiredPermission: "slots.read" },
  { key: "bookings", label: "Bookings", href: "/bookings", srsRef: "SRS 12.11, 12.13", requiredPermission: "bookings.read" },
  { key: "coupons", label: "Coupons & Campaigns", href: "/coupons", srsRef: "SRS 12.12", requiredPermission: "coupons.read" },
  { key: "support", label: "Support Tickets", href: "/support", srsRef: "SRS 12.14", requiredPermission: "support.read" },
  { key: "reviews", label: "Review Moderation", href: "/reviews", srsRef: "SRS 12.15", requiredPermission: "reviews.read" },
  { key: "cms", label: "CMS & Content", href: "/cms", srsRef: "SRS 12.16", requiredPermission: "cms.read" },
  { key: "notifications", label: "Notification Templates", href: "/notifications", srsRef: "SRS 12.17", requiredPermission: "notifications.read" },
  { key: "reports", label: "Reports & Exports", href: "/reports", srsRef: "SRS 12.18", requiredPermission: "reports.read" },
  { key: "audit", label: "Audit Log", href: "/audit", srsRef: "SRS 12.1.2, 12.2.3, 21", requiredPermission: "audit.read" },
  { key: "settings", label: "System Settings", href: "/settings", srsRef: "SRS 12.19", requiredPermission: "settings.read" },
  { key: "admin-users", label: "Admin Users", href: "/admin-users", srsRef: "SRS 12.2", requiredPermission: "settings.read" },
  { key: "provider", label: "Providers", href: "/providers", srsRef: "PROVIDER.md", requiredPermission: "provider.read" },
  { key: "referral", label: "Referral Program", href: "/referral", srsRef: "REFERRAL.md", requiredPermission: "referral.read" },
  { key: "nestly-coins", label: "Nestly Coins", href: "/nestly-coins", srsRef: "NESTLY-COINS.md", requiredPermission: "nestly-coins.read" },
  { key: "subscription", label: "Subscription Plans", href: "/subscription-plans", srsRef: "PRODUCT-ENHANCEMENTS.md #1", requiredPermission: "subscription.read" },
];

/** Whether the current admin can perform mutating ("write") actions within a module, per AdminPermissionCatalog. */
export function canWriteModule(claims: AdminSessionClaims | null, moduleKey: NavModuleKey): boolean {
  if (!claims) return false;
  if (claims.permissions.length > 0) {
    return claims.permissions.includes(`${moduleKey}.write`);
  }

  // Same fallback reasoning as isModuleVisibleByRole: without a populated
  // permissions claim, fall back to the provisional role matrix - any role
  // that can see the module at all is treated as able to act on it, since
  // the fallback matrix does not distinguish read/write.
  return isModuleVisibleByRole(claims.role, moduleKey);
}

/**
 * Provisional role -> module matrix, standing in for the real permission
 * matrix (task 96a) until that lands. Derived from the role list and
 * responsibilities implied in SRS 12.2.2; every role gets the dashboard.
 * "Super Admin" is treated as unrestricted, matching its description as the
 * role that manages every other admin user and role (SRS 12.2.1).
 */
const ROLE_MODULE_FALLBACK: Record<string, NavModuleKey[] | "*"> = {
  "Super Admin": "*",
  "Operations Admin": ["dashboard", "customers", "bookings", "serviceability", "slots", "support", "provider"],
  "Booking Admin": ["dashboard", "bookings", "slots", "serviceability"],
  "Support Admin": ["dashboard", "support", "customers", "reviews"],
  "Catalog Admin": ["dashboard", "catalog", "pricing"],
  "Pricing Admin": ["dashboard", "pricing", "coupons"],
  "Marketing Admin": ["dashboard", "coupons", "cms", "notifications", "reviews", "referral", "nestly-coins", "subscription"],
  "Finance Admin": ["dashboard", "bookings", "reports", "provider", "nestly-coins", "subscription"],
  "Read-only Analyst": ["dashboard", "reports"],
};

function isModuleVisibleByRole(role: string | null, key: NavModuleKey): boolean {
  if (!role) return key === "dashboard";
  const allowed = ROLE_MODULE_FALLBACK[role];
  if (allowed === "*") return true;
  if (!allowed) return key === "dashboard"; // unrecognised role: safest usable default
  return allowed.includes(key);
}

/**
 * Returns the nav modules the given admin session should see.
 *
 * `claims` is null while the session is still loading/unauthenticated - no
 * modules are visible then (RequireAdminAuth keeps that state off-screen
 * anyway, but this stays defensive rather than assuming a caller order).
 */
export function getVisibleNavModules(claims: AdminSessionClaims | null): NavModule[] {
  if (!claims) return [];

  if (claims.permissions.length > 0) {
    const granted = new Set(claims.permissions);
    return NAV_MODULES.filter((module) => granted.has(module.requiredPermission));
  }

  return NAV_MODULES.filter((module) => isModuleVisibleByRole(claims.role, module.key));
}
