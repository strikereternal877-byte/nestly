"use client";

import { NavTabs } from "@/components/nav-tabs";

/**
 * Sub-nav for the Payments module - "All transactions" (the existing SRS
 * 12.13.1 list/detail view) and "Reconciliation" (docs/OPEN-FIXES-FEATURES.csv
 * "Payment reconciliation"), both gated behind the same "payments.read"
 * permission. Same pattern as `BookingsTabs`/`SubscriptionTabs`: a single
 * sidebar entry ("Payments", `permissions.ts`) with this strip underneath it
 * rather than a second sidebar item for the reconciliation queue.
 */
export function PaymentsTabs() {
  return (
    <NavTabs
      label="Payment sections"
      tabs={[
        { href: "/payments", label: "All transactions" },
        { href: "/payments/reconciliation", label: "Reconciliation" },
      ]}
    />
  );
}
