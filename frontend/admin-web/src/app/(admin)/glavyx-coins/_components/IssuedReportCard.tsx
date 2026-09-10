"use client";

import { useQuery } from "@tanstack/react-query";
import { motion } from "motion/react";
import { useState } from "react";
import { Reveal, revealItem } from "@/components/motion";
import { Alert, Button, Card, Field, Skeleton, StatTile } from "@/components/ui";
import { FormActions, formatCurrency } from "@/components/data-table";
import { SectionError } from "@/components/screen-states";
import { isoDateOffsetFromToday, todayIsoDate } from "@/lib/date";
import { NestlyCoinsAudience, getCoinsIssuedReport } from "../_lib/coins-api";

/** Matches a `StatTile`'s height so the row does not jump when the numbers land. */
function StatTileSkeleton() {
  return (
    <div className="rounded-2xl bg-surface p-5 shadow-sm">
      <Skeleton className="h-4 w-32" />
      <Skeleton className="mt-3 h-9 w-24" />
    </div>
  );
}

/**
 * Program cost over a date range: coins issued, coins clawed back, and the
 * net liability still outstanding.
 *
 * The range is a *local calendar* window — `_lib/coins-api` pins both ends to
 * the admin's day boundaries rather than sending the bare `yyyy-mm-dd`.
 */
export function IssuedReportCard({ audience }: { audience: NestlyCoinsAudience }) {
  // Computed once per mount so the draft and the applied range cannot
  // disagree about "today" on a page left open across midnight.
  const [defaults] = useState(() => ({ from: isoDateOffsetFromToday(-30), to: todayIsoDate() }));
  const [fromDate, setFromDate] = useState(defaults.from);
  const [toDate, setToDate] = useState(defaults.to);
  const [range, setRange] = useState(defaults);
  const [rangeError, setRangeError] = useState<string | null>(null);

  const reportQuery = useQuery({
    queryKey: ["nestly-coins-report", audience, range.from, range.to] as const,
    queryFn: () => getCoinsIssuedReport(audience, range.from, range.to),
  });

  return (
    <Card title="Coins issued vs. clawed back" description="Program cost over a date range.">
      <div className="flex flex-col gap-5">
        <form
          className="flex flex-col gap-4"
          onSubmit={(event) => {
            event.preventDefault();
            if (!fromDate || !toDate) {
              setRangeError("Pick both a start and an end date.");
              return;
            }
            if (fromDate > toDate) {
              setRangeError("The start date must not be after the end date.");
              return;
            }
            setRangeError(null);
            setRange({ from: fromDate, to: toDate });
          }}
        >
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
            <Field
              label="From"
              type="date"
              max={toDate || undefined}
              value={fromDate}
              onChange={(event) => setFromDate(event.target.value)}
            />
            <Field
              label="To"
              type="date"
              min={fromDate || undefined}
              value={toDate}
              onChange={(event) => setToDate(event.target.value)}
            />
          </div>
          {rangeError ? <Alert>{rangeError}</Alert> : null}
          <FormActions align="start">
            {/* Not busy on the first load — the skeleton below already says
                so, and two spinners for one request reads as two requests. */}
            <Button type="submit" loading={reportQuery.isFetching && !reportQuery.isPending}>
              Load
            </Button>
          </FormActions>
        </form>

        {reportQuery.isPending ? (
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
            <StatTileSkeleton />
            <StatTileSkeleton />
            <StatTileSkeleton />
          </div>
        ) : reportQuery.isError ? (
          <SectionError error={reportQuery.error} onRetry={() => void reportQuery.refetch()} />
        ) : (
          <Reveal className="grid grid-cols-1 gap-4 sm:grid-cols-3">
            <motion.div variants={revealItem}>
              <StatTile
                tone="brand"
                label="Issued"
                value={formatCurrency(reportQuery.data.totalIssued)}
                title={formatCurrency(reportQuery.data.totalIssued)}
              />
            </motion.div>
            <motion.div variants={revealItem}>
              <StatTile
                tone="danger"
                label="Clawed back"
                value={formatCurrency(reportQuery.data.totalClawedBack)}
                title={formatCurrency(reportQuery.data.totalClawedBack)}
              />
            </motion.div>
            <motion.div variants={revealItem}>
              <StatTile
                tone="success"
                label="Net outstanding"
                value={formatCurrency(reportQuery.data.netOutstanding)}
                title={formatCurrency(reportQuery.data.netOutstanding)}
                hint="Issued minus clawed back — the liability still on the books."
              />
            </motion.div>
          </Reveal>
        )}
      </div>
    </Card>
  );
}
