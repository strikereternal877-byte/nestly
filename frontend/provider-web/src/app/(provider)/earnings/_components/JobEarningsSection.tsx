"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useState } from "react";
import { ErrorState, NotYetAvailable } from "@/components/states";
import {
  Button,
  Card,
  EmptyState,
  Skeleton,
  StatTile,
  TBody,
  TD,
  TH,
  THead,
  TR,
  Table,
  Tabs,
} from "@/components/ui";
import { isNotImplemented } from "@/lib/api";
import { isoDateOffsetFromToday } from "@/lib/date";
import { listJobEarnings } from "@/lib/earnings-api";
import { formatInr, formatIsoDate, formatSignedInr } from "@/lib/format";
import { JobPayoutStatusBadge } from "./JobPayoutStatusBadge";
import type { JobEarning } from "@/lib/earnings-types";

type Period = "30" | "90" | "all";

const PERIOD_TABS = [
  { value: "30", label: "Last 30 days" },
  { value: "90", label: "Last 90 days" },
  { value: "all", label: "All time" },
] as const satisfies readonly { value: Period; label: string }[];

/** Inclusive local `YYYY-MM-DD` bounds for a period, or `undefined` for "all time" (sent as no filter, not an empty string). */
function dateRangeFor(period: Period): { fromDate?: string; toDate?: string } {
  if (period === "all") return {};
  // Inclusive of today, so "last 30 days" is today plus the 29 before it -
  // same convention as LedgerSection's client-side period filter.
  return { fromDate: isoDateOffsetFromToday(-(Number(period) - 1)), toDate: isoDateOffsetFromToday(0) };
}

/**
 * The job-level earnings ledger (docs/OPEN-FIXES-FEATURES.csv "Earnings
 * detail and payouts"): one row per completed job with its gross-to-net
 * breakdown - the exact figures `jobs/[id]`'s payout panel already shows
 * (booking total, platform commission, payout) - plus where that job sits in
 * the payout timeline, so a provider can tell not just what a job paid but
 * whether they have actually been sent the money yet.
 *
 * Unlike `LedgerSection`'s client-side period tabs, the date range here is a
 * real server-side filter (`GET /earnings/jobs?fromDate=&toDate=`) - the
 * summary tiles above the table total the *whole filtered period*, not just
 * the page fetched, so they stay honest even though this screen does not
 * (yet) build a page-by-page control (pageSize is requested high enough to
 * cover a typical period in one page, matching `PayoutsSection`'s own scope).
 */
export function JobEarningsSection() {
  const [period, setPeriod] = useState<Period>("30");
  const range = dateRangeFor(period);
  const query = useQuery({
    queryKey: ["provider-job-earnings", range.fromDate ?? null, range.toDate ?? null],
    queryFn: () => listJobEarnings({ ...range, pageSize: 100 }),
  });

  if (query.isPending) {
    return (
      <Card title="Completed jobs">
        <JobEarningsSkeleton />
      </Card>
    );
  }

  if (query.isError && isNotImplemented(query.error)) {
    return (
      <Card title="Completed jobs">
        <NotYetAvailable
          title="Your job earnings aren't available yet"
          description="Earnings tracking is still being built on the platform side. Every completed job's payout breakdown will be listed here once it goes live."
          action={
            <Link href="/jobs">
              <Button variant="secondary">See your jobs</Button>
            </Link>
          }
        />
      </Card>
    );
  }

  if (query.isError) {
    return (
      <Card title="Completed jobs">
        <ErrorState
          title="Couldn't load your job earnings"
          error={query.error}
          onRetry={() => query.refetch()}
          isRetrying={query.isRefetching}
        />
      </Card>
    );
  }

  const { items, jobCount, totalNetAmount } = query.data;

  return (
    <Card
      title="Completed jobs"
      description="What each completed job actually paid you, and where it stands in your payout timeline."
    >
      <Tabs
        tabs={PERIOD_TABS}
        value={period}
        onChange={setPeriod}
        label="Job earnings period"
        className="mb-4"
      />

      {items.length === 0 ? (
        <EmptyState
          title="No completed jobs in this period"
          description="A job you complete is credited to your ledger with its gross/commission/net breakdown here."
          action={
            <Link href="/jobs">
              <Button variant="secondary">See your jobs</Button>
            </Link>
          }
        />
      ) : (
        <>
          <div className="mb-5 grid grid-cols-2 gap-3">
            <StatTile label="Net earned" value={formatInr(totalNetAmount)} />
            <StatTile label="Jobs completed" value={String(jobCount)} />
          </div>

          {/* Phone: one stacked card per job. */}
          <ul className="flex flex-col divide-y divide-line sm:hidden">
            {items.map((job) => (
              <li key={job.bookingId} className="flex flex-col gap-2 py-3">
                <div className="flex items-start justify-between gap-4">
                  <div className="min-w-0">
                    <p className="truncate text-sm font-medium text-fg">{job.serviceName}</p>
                    <p className="nums mt-0.5 text-xs text-fg-subtle">
                      {job.bookingReference} · {formatIsoDate(job.completionDate)}
                    </p>
                  </div>
                  <p className="nums shrink-0 text-sm font-semibold text-fg">
                    {formatInr(job.netAmountToProvider)}
                  </p>
                </div>
                <div className="flex items-center justify-between gap-4">
                  <PayoutBreakdownInline job={job} />
                  <JobPayoutStatusBadge status={job.payoutStatus} />
                </div>
              </li>
            ))}
          </ul>

          {/* Tablet and up: the same data as a table, where the columns fit. */}
          <div className="hidden sm:block">
            <Table>
              <THead>
                <TR>
                  <TH>Date</TH>
                  <TH>Booking</TH>
                  <TH numeric>Gross</TH>
                  <TH numeric>Commission</TH>
                  <TH numeric>Net payout</TH>
                  <TH>Payout status</TH>
                </TR>
              </THead>
              <TBody>
                {items.map((job) => (
                  <TR key={job.bookingId}>
                    <TD className="nums whitespace-nowrap text-fg-muted">
                      {formatIsoDate(job.completionDate)}
                    </TD>
                    <TD>
                      <span className="font-medium text-fg">{job.serviceName}</span>
                      <span className="nums mt-0.5 block text-fg-muted">{job.bookingReference}</span>
                    </TD>
                    <TD numeric className="nums text-fg-muted">
                      {formatInr(job.grossAmount)}
                    </TD>
                    <TD numeric className="nums text-fg-muted">
                      {formatSignedInr(job.commissionAmount, /* isDebit */ true)}
                    </TD>
                    <TD numeric className="nums font-semibold text-fg">
                      {formatInr(job.netAmountToProvider)}
                    </TD>
                    <TD>
                      <JobPayoutStatusBadge status={job.payoutStatus} />
                    </TD>
                  </TR>
                ))}
              </TBody>
            </Table>
          </div>
        </>
      )}
    </Card>
  );
}

/** Gross → commission, compact enough for the phone card's second line. */
function PayoutBreakdownInline({ job }: { job: JobEarning }) {
  return (
    <p className="nums text-xs text-fg-subtle">
      {formatInr(job.grossAmount)} − {formatInr(job.commissionAmount)} commission
    </p>
  );
}

function JobEarningsSkeleton() {
  return (
    <div className="flex flex-col gap-3" aria-hidden>
      <Skeleton className="h-9 w-full rounded-lg" />
      <div className="grid grid-cols-2 gap-3">
        <Skeleton className="h-20 rounded-2xl" />
        <Skeleton className="h-20 rounded-2xl" />
      </div>
      {Array.from({ length: 4 }, (_, index) => (
        <div key={index} className="flex items-start justify-between gap-4 py-1">
          <div className="min-w-0 flex-1">
            <Skeleton className="h-4 w-32" />
            <Skeleton className="mt-2 h-3 w-44" />
          </div>
          <Skeleton className="h-4 w-20" />
        </div>
      ))}
    </div>
  );
}
