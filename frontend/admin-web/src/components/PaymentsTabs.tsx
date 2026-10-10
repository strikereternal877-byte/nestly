"use client";

import { NavTabs } from "@/components/nav-tabs";

/**
 * Sub-nav for the Payments module - "All transactions" (the existing SRS
 * 12.13.1 list/detail view), "Reconciliation" (docs/OPEN-FIXES-FEATURES.csv
 * "Payment reconciliation"), "Auto-charge retries" (Payment Management UX
 * pass: previously zero admin visibility into
 * `RecurringOccurrenceAutoChargeJob` at all - see `BookingsController`'s
 * `auto-charge/*` routes), and "Payouts" (same pass: a cross-provider payout
 * queue - the same admin/providerId-optional search `PayoutsController.Search`
 * already supported, previously only ever called scoped to one provider from
 * that provider's own Earnings tab). "All transactions"/"Reconciliation" are
 * gated behind "payments.read"; "Auto-charge retries" is "bookings.read"/
 * "bookings.write" (it is booking-domain data, same reasoning
 * `RecurringPlansController` gives for its own "bookings.read" gate);
 * "Wallet top-ups" (customers adding money to their wallet through the gateway - the
 * stuck / needs-review list and "Reconcile now") is "payments.read"/"payments.write" like the other two payment
 * views; "Payouts" is the separate "payout.read"/"payout.write" module (PROVIDER.md
 * RBAC ADDITIONS). All three ride along this one strip anyway, for the same
 * reason AMC contracts live under `BookingsTabs` despite their own distinct
 * gating: an admin doing day-to-day payment operations looks for it here,
 * and each page still enforces its own permission regardless of which tab
 * strip links to it.
 */
export function PaymentsTabs() {
  return (
    <NavTabs
      label="Payment sections"
      tabs={[
        { href: "/payments", label: "All transactions" },
        { href: "/payments/reconciliation", label: "Reconciliation" },
        { href: "/payments/wallet-top-ups", label: "Wallet top-ups" },
        { href: "/payments/auto-charge", label: "Auto-charge retries" },
        { href: "/payments/payouts", label: "Payouts" },
      ]}
    />
  );
}
