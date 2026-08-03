"use client";

import { PageHeading } from "@/components/ui";
import { LedgerSection } from "./_components/LedgerSection";
import { PayoutsSection } from "./_components/PayoutsSection";
import { SummarySection } from "./_components/SummarySection";

/** Earnings screen (docs/PROVIDER.md's Financial domain): summary, ledger and payouts. */
export default function EarningsPage() {
  return (
    <div className="mx-auto flex w-full max-w-4xl flex-col gap-6">
      <PageHeading title="Earnings" subtitle="What you've earned, and your payout history." />
      <SummarySection />
      <LedgerSection />
      <PayoutsSection />
    </div>
  );
}
