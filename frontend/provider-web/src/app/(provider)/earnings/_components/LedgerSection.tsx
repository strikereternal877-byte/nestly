"use client";

import { useQuery } from "@tanstack/react-query";
import { Alert, Card } from "@/components/ui";
import { describeError, isNotImplemented } from "@/lib/api";
import { listEarningsLedger } from "@/lib/earnings-api";
import { earningSourceLabel } from "@/lib/earnings-types";
import { NotYetAvailable } from "./NotYetAvailable";

/** Append-only earning ledger table (credit per completed job, debit for penalties). */
export function LedgerSection() {
  const query = useQuery({ queryKey: ["provider-earnings-ledger"], queryFn: listEarningsLedger });

  if (query.isPending) {
    return (
      <Card title="Ledger">
        <p className="text-sm text-neutral-500">Loading ledger…</p>
      </Card>
    );
  }

  if (query.isError && isNotImplemented(query.error)) {
    return <NotYetAvailable title="Ledger" />;
  }

  if (query.isError) {
    return (
      <Card title="Ledger">
        <Alert>{describeError(query.error)}</Alert>
      </Card>
    );
  }

  if (query.data.length === 0) {
    return (
      <Card title="Ledger">
        <p className="text-sm text-neutral-600 dark:text-neutral-400">No ledger entries yet.</p>
      </Card>
    );
  }

  return (
    <Card title="Ledger">
      <div className="overflow-x-auto rounded-xl border border-black/10 dark:border-white/15">
        <table className="w-full min-w-[480px] text-left text-sm">
          <thead className="border-b border-black/10 bg-black/5 dark:border-white/15 dark:bg-white/5">
            <tr>
              <th className="px-3 py-2 font-medium">Source</th>
              <th className="px-3 py-2 font-medium">Description</th>
              <th className="px-3 py-2 font-medium">Amount</th>
              <th className="px-3 py-2 font-medium">Date</th>
            </tr>
          </thead>
          <tbody>
            {query.data.map((entry) => (
              <tr key={entry.id} className="border-b border-black/5 last:border-0 dark:border-white/10">
                <td className="px-3 py-2">{earningSourceLabel(entry.sourceType)}</td>
                <td className="px-3 py-2">{entry.description ?? "—"}</td>
                <td className="px-3 py-2">₹{entry.amount.toFixed(2)}</td>
                <td className="px-3 py-2">{entry.createdAtUtc ? new Date(entry.createdAtUtc).toLocaleDateString() : "—"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Card>
  );
}
