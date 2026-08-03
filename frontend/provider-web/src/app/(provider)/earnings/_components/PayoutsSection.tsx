"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { Alert, Card } from "@/components/ui";
import { describeError, isNotImplemented } from "@/lib/api";
import { listPayouts } from "@/lib/earnings-api";
import { payoutStatusLabel } from "@/lib/earnings-types";
import { NotYetAvailable } from "./NotYetAvailable";

/** Payout batches list (docs/PROVIDER.md's `provider_payout` table - manual bank transfer in v1). */
export function PayoutsSection() {
  const query = useQuery({ queryKey: ["provider-payouts"], queryFn: listPayouts });

  if (query.isPending) {
    return (
      <Card title="Payouts">
        <p className="text-sm text-neutral-500">Loading payouts…</p>
      </Card>
    );
  }

  if (query.isError && isNotImplemented(query.error)) {
    return <NotYetAvailable title="Payouts" />;
  }

  if (query.isError) {
    return (
      <Card title="Payouts">
        <Alert>{describeError(query.error)}</Alert>
      </Card>
    );
  }

  if (query.data.length === 0) {
    return (
      <Card title="Payouts">
        <p className="text-sm text-neutral-600 dark:text-neutral-400">No payouts yet.</p>
      </Card>
    );
  }

  return (
    <Card title="Payouts">
      <div className="overflow-x-auto rounded-xl border border-black/10 dark:border-white/15">
        <table className="w-full min-w-[480px] text-left text-sm">
          <thead className="border-b border-black/10 bg-black/5 dark:border-white/15 dark:bg-white/5">
            <tr>
              <th className="px-3 py-2 font-medium">Period</th>
              <th className="px-3 py-2 font-medium">Amount</th>
              <th className="px-3 py-2 font-medium">Status</th>
              <th className="px-3 py-2 font-medium"><span className="sr-only">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            {query.data.map((payout) => (
              <tr key={payout.id} className="border-b border-black/5 last:border-0 dark:border-white/10">
                <td className="px-3 py-2">
                  {payout.periodStart && payout.periodEnd ? `${payout.periodStart} – ${payout.periodEnd}` : "—"}
                </td>
                <td className="px-3 py-2">{payout.totalAmount !== undefined ? `₹${payout.totalAmount.toFixed(2)}` : "—"}</td>
                <td className="px-3 py-2">{payoutStatusLabel(payout.status)}</td>
                <td className="px-3 py-2">
                  <Link href={`/earnings/payouts/${payout.id}`} className="underline underline-offset-2">
                    View
                  </Link>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Card>
  );
}
