"use client";

import { PageHeading } from "@/components/ui";
import { JobEarningsSection } from "./_components/JobEarningsSection";
import { LedgerSection } from "./_components/LedgerSection";
import { PayoutsSection } from "./_components/PayoutsSection";
import { SummarySection } from "./_components/SummarySection";

/**
 * Earnings (docs/PROVIDER.md's Financial domain), in the order a provider asks
 * the questions: what am I owed right now, what did each job actually pay
 * once commission came out and has it been sent yet, where did every credit/
 * debit come from, and when was it paid out.
 *
 * Each section owns its own query and its own three states, because the four
 * endpoints fail independently — a ledger that is briefly unreachable must not
 * blank out a balance that loaded fine.
 */
export default function EarningsPage() {
  return (
    <div className="flex w-full max-w-4xl animate-rise flex-col gap-6">
      <PageHeading title="Earnings" subtitle="What you've earned, per job, and your payout history." />
      <SummarySection />
      <JobEarningsSection />
      <LedgerSection />
      <PayoutsSection />
    </div>
  );
}
