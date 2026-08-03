"use client";

import { Card } from "@/components/ui";

/**
 * Shared "not yet available" state for the earnings screens - the backend
 * behind `/api/v1/earnings` is currently a stub returning HTTP 501 pending
 * sibling task #148 (docs/PROVIDER.md's Financial domain,
 * `provider_earning_ledger` / `provider_payout`).
 */
export function NotYetAvailable({ title }: { title: string }) {
  return (
    <Card title={title}>
      <p className="text-sm text-neutral-600 dark:text-neutral-400">
        Earnings tracking is still being built on the platform side. Check back once payouts go live.
      </p>
    </Card>
  );
}
