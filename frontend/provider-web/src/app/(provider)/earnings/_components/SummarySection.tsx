"use client";

import { useQuery } from "@tanstack/react-query";
import { StatTile } from "@/components/ui";
import { Alert } from "@/components/ui";
import { describeError, isNotImplemented } from "@/lib/api";
import { getEarningsSummary } from "@/lib/earnings-api";
import { NotYetAvailable } from "./NotYetAvailable";

/** Earnings summary tile row - total earned and pending payout, from the summary endpoint's stable fields. */
export function SummarySection() {
  const query = useQuery({ queryKey: ["provider-earnings-summary"], queryFn: getEarningsSummary });

  if (query.isPending) {
    return <p className="text-sm text-neutral-500">Loading earnings summary…</p>;
  }

  if (query.isError && isNotImplemented(query.error)) {
    return <NotYetAvailable title="Summary" />;
  }

  if (query.isError) {
    return <Alert>{describeError(query.error)}</Alert>;
  }

  return (
    <div className="grid grid-cols-1 gap-4">
      <StatTile label="Current balance" value={`₹${query.data.currentBalance.toFixed(2)}`} />
    </div>
  );
}
