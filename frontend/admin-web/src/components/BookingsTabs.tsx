"use client";

import { NavTabs } from "@/components/nav-tabs";

/**
 * Sub-nav for the Bookings module. Recurring plans, assignment conflicts and
 * AMC contracts/renewal report all live here rather than as their own
 * sidebar entries because they are gated by the same "bookings.read"
 * permission and describe the same domain - see RecurringPlansController's,
 * BookingConflictsController's and AmcContractsController's doc comments. A
 * sidebar entry would imply a module of its own, which is exactly the RBAC
 * split task 299 (and docs/AMC.md's RBAC ADDITIONS) concluded against.
 *
 * Promoted from `bookings/_components` to the top level (Phase 20, AMC):
 * the AMC contract/report pages live under `/amc`, not nested inside
 * `/bookings`, so a route-local `_components` folder could no longer be the
 * single owner of this strip - every one of catalog/cms/referral's own
 * per-module tabs stays local because none of them needed to be shared
 * across two different top-level route directories the way this one now is.
 */
export function BookingsTabs() {
  return (
    <NavTabs
      label="Booking sections"
      tabs={[
        { href: "/bookings", label: "All bookings" },
        { href: "/bookings/unassigned-at-risk", label: "Unassigned & at-risk" },
        { href: "/bookings/recurring-plans", label: "Recurring plans" },
        { href: "/bookings/conflicts", label: "Conflicts" },
        { href: "/amc/contracts", label: "AMC contracts" },
        { href: "/amc/renewal-report", label: "AMC renewal report" },
      ]}
    />
  );
}
