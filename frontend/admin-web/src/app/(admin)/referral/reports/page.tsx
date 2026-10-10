"use client";

import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { motion } from "motion/react";
import { useState } from "react";
import { FilterBar, countActiveFilters, formatCurrency } from "@/components/data-table";
import { Reveal, revealItem } from "@/components/motion";
import { SectionError } from "@/components/screen-states";
import { Card, Field, PageHeading, Skeleton, StatTile } from "@/components/ui";
import { endOfLocalDayUtc, startOfLocalDayUtc } from "@/lib/day-range";
import { ReferralTabs } from "../_components/ReferralTabs";
import { getReferralCostReport, getReferralFunnelReport } from "../_lib/referral-api";

interface DateRangeFilters {
  fromDate: string;
  toDate: string;
}

const EMPTY_FILTERS: DateRangeFilters = { fromDate: "", toDate: "" };

/**
 * Referral funnel and total program cost (task 171).
 *
 * Both endpoints take UTC instants. The previous version passed the raw
 * `yyyy-mm-dd` from the date control straight through as `fromUtc`, which
 * declares the admin's local day boundary to be a UTC one — in IST that shifts
 * the reported window by 5h30m, so "from 4 August" quietly included the
 * evening of the 3rd. `lib/day-range.ts` converts each boundary from local
 * time components instead.
 */
export default function ReferralReportsPage() {
  const [filters, setFilters] = useState<DateRangeFilters>(EMPTY_FILTERS);

  // Live filtering (no Apply button): both fields are date pickers, so a
  // change applies immediately - but "to" before "from" is still nonsense,
  // so an invalid range is flagged and left disabled rather than fired at
  // the report endpoints, same validation the old Apply button ran.
  // Derived directly during render rather than mirrored into its own state -
  // there is nothing here an effect was ever needed for.
  const isRangeInvalid = Boolean(filters.fromDate && filters.toDate && filters.toDate < filters.fromDate);
  const rangeError = isRangeInvalid ? "The end date cannot be before the start date." : null;

  const range = {
    fromUtc: filters.fromDate ? startOfLocalDayUtc(filters.fromDate) ?? undefined : undefined,
    toUtc: filters.toDate ? endOfLocalDayUtc(filters.toDate) ?? undefined : undefined,
  };

  const funnelQuery = useQuery({
    queryKey: ["referral-reports", "funnel", filters] as const,
    queryFn: () => getReferralFunnelReport(range),
    enabled: !isRangeInvalid,
    placeholderData: keepPreviousData,
  });

  const costQuery = useQuery({
    queryKey: ["referral-reports", "cost", filters] as const,
    queryFn: () => getReferralCostReport(range),
    enabled: !isRangeInvalid,
    placeholderData: keepPreviousData,
  });

  const clearFilters = () => {
    setFilters(EMPTY_FILTERS);
  };

  const isBusy = funnelQuery.isFetching || costQuery.isFetching;

  return (
    <div className="w-full max-w-5xl">
      <PageHeading
        title="Referral reports"
        subtitle="How far referrals get through the funnel, and what the programme has paid out."
      />

      <ReferralTabs />

      <div className="flex flex-col gap-6">
        <FilterBar
          columns={2}
          onClear={clearFilters}
          activeCount={countActiveFilters(filters)}
          busy={isBusy}
        >
          <Field
            label="From"
            type="date"
            max={filters.toDate || undefined}
            value={filters.fromDate}
            error={rangeError ?? undefined}
            onChange={(event) => setFilters((current) => ({ ...current, fromDate: event.target.value }))}
          />
          <Field
            label="To"
            type="date"
            min={filters.fromDate || undefined}
            value={filters.toDate}
            onChange={(event) => setFilters((current) => ({ ...current, toDate: event.target.value }))}
          />
        </FilterBar>

        <Card
          title="Funnel"
          description="Of the referrals registered in this range, how many have since progressed further."
        >
          {funnelQuery.isPending ? (
            <StatTileSkeleton count={4} />
          ) : funnelQuery.error ? (
            <SectionError error={funnelQuery.error} onRetry={() => funnelQuery.refetch()} />
          ) : (
            <Reveal className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
              <motion.div variants={revealItem}>
                <StatTile tone="brand" label="Invited" value={count(funnelQuery.data.invitedCount)} hint="Same as registered — invite clicks are not tracked." />
              </motion.div>
              <motion.div variants={revealItem}>
                <StatTile tone="info" label="Registered" value={count(funnelQuery.data.registeredCount)} />
              </motion.div>
              <motion.div variants={revealItem}>
                <StatTile
                  tone="warning"
                  label="Qualified"
                  value={count(funnelQuery.data.qualifiedCount)}
                  hint={share(funnelQuery.data.qualifiedCount, funnelQuery.data.registeredCount)}
                />
              </motion.div>
              <motion.div variants={revealItem}>
                <StatTile
                  tone="success"
                  label="Rewarded"
                  value={count(funnelQuery.data.rewardedCount)}
                  hint={share(funnelQuery.data.rewardedCount, funnelQuery.data.registeredCount)}
                />
              </motion.div>
            </Reveal>
          )}
        </Card>

        <Card title="Programme cost" description="Every reward actually disbursed within this range.">
          {costQuery.isPending ? (
            <StatTileSkeleton count={3} />
          ) : costQuery.error ? (
            <SectionError error={costQuery.error} onRetry={() => costQuery.refetch()} />
          ) : (
            <Reveal className="grid grid-cols-1 gap-4 sm:grid-cols-3">
              <motion.div variants={revealItem}>
                <StatTile tone="accent" label="Wallet credit" value={formatCurrency(costQuery.data.totalWalletCreditCost)} />
              </motion.div>
              <motion.div variants={revealItem}>
                <StatTile tone="brand" label="Coupons" value={formatCurrency(costQuery.data.totalCouponCost)} />
              </motion.div>
              <motion.div variants={revealItem}>
                <StatTile
                  tone="danger"
                  label="Total cost"
                  value={formatCurrency(costQuery.data.totalCost)}
                  hint={`${count(costQuery.data.rewardedReferralCount)} referral rewards · ${count(
                    costQuery.data.milestoneBonusCount,
                  )} milestone bonuses`}
                />
              </motion.div>
            </Reveal>
          )}
        </Card>
      </div>
    </div>
  );
}

function count(value: number): string {
  return value.toLocaleString("en-IN");
}

/** Conversion against the registered cohort — omitted rather than shown as 0% of nothing. */
function share(value: number, total: number): string | undefined {
  if (total <= 0) return undefined;
  return `${Math.round((value / total) * 100)}% of registered`;
}

/** Matches the real tile's box so applying a range does not jump the layout. */
function StatTileSkeleton({ count: tiles }: { count: number }) {
  return (
    <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
      {Array.from({ length: tiles }, (_, index) => (
        <div key={index} className="rounded-2xl bg-surface p-5 shadow-sm">
          <Skeleton className="h-4 w-24" />
          <Skeleton className="mt-3 h-8 w-20" />
        </div>
      ))}
    </div>
  );
}
