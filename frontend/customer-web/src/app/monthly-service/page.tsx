"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { BannerBreadcrumb, ScreenSkeleton, inr } from "@/components/patterns";
import { Reveal, RevealItem } from "@/components/motion";
import { PageBanner } from "@/components/PageBanner";
import { RequireAuth } from "@/components/RequireAuth";
import { Alert, Button, Card, EmptyState, LinkButton } from "@/components/ui";
import { describeError } from "@/lib/api";
import {
  MonthlyServiceContractStatus,
  describeDays,
  describeVisit,
  formatClock,
  listMyMonthlyServices,
} from "@/lib/monthly-service";
import type { MonthlyServiceContract } from "@/lib/monthly-service";
import { ContractStatusBadge, MY_MONTHLY_SERVICES_KEY, Stat } from "./_components/shared";

/**
 * "My monthly services" (docs/MONTHLY-SERVICE.md): one card per engagement,
 * each showing who comes, when, and how this month is going so far.
 */
export default function MonthlyServicesPage() {
  return (
    <RequireAuth>
      <MonthlyServicesScreen />
    </RequireAuth>
  );
}

function MonthlyServicesScreen() {
  const query = useQuery({ queryKey: MY_MONTHLY_SERVICES_KEY, queryFn: listMyMonthlyServices });

  if (query.isPending) {
    return (
      <main className="flex w-full flex-col" aria-hidden>
        <div className="listing-banner h-[13.5rem] w-full sm:h-[15.5rem]" />
        <ScreenSkeleton cards={2} className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14" />
      </main>
    );
  }

  return (
    <main className="flex w-full flex-col">
      <PageBanner
        title="Monthly services"
        description="The same trusted professional on your chosen days. You pay at month end, only for the days they came."
        breadcrumb={<BannerBreadcrumb items={[{ label: "Home", href: "/" }, { label: "Monthly services" }]} />}
      />

      <div className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14">
        <div className="mb-6">
          <LinkButton href="/monthly-service/new" size="sm">
            Start a monthly service
          </LinkButton>
        </div>

        {query.isError ? (
          <Alert
            tone="error"
            title="Couldn't load your monthly services"
            action={
              <Button size="sm" variant="secondary" onClick={() => query.refetch()}>
                Retry
              </Button>
            }
          >
            {describeError(query.error)}
          </Alert>
        ) : query.data.length === 0 ? (
          <EmptyState
            title="No monthly services yet"
            description="Get house help or a maid on fixed days every week — same person, pay monthly for the visits that happened."
            action={<LinkButton href="/monthly-service/new">See plans</LinkButton>}
          />
        ) : (
          <Reveal as="ul" className="flex list-none flex-col gap-4">
            {query.data.map((contract) => (
              <RevealItem key={contract.id}>
                <ContractCard contract={contract} />
              </RevealItem>
            ))}
          </Reveal>
        )}
      </div>
    </main>
  );
}

function ContractCard({ contract }: { contract: MonthlyServiceContract }) {
  const running = contract.status !== MonthlyServiceContractStatus.Cancelled;
  return (
    <Card>
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="text-sm font-semibold text-fg">{contract.planName}</p>
          <p className="mt-0.5 text-xs text-fg-muted">
            {describeDays(contract.days)} · {formatClock(contract.visitStartTime)} · {describeVisit(contract)}
          </p>
        </div>
        <ContractStatusBadge contract={contract} />
      </div>

      {running ? (
        <div className="mt-4 grid grid-cols-2 gap-4 border-t border-line pt-4 sm:grid-cols-4">
          <Stat label="Professional" value={contract.provider?.displayName ?? "Being assigned"} />
          <Stat label="Visits this month" value={String(contract.currentMonth.present + contract.currentMonth.customerUnavailable)} />
          <Stat label="Charged so far" value={inr(contract.currentMonth.billableAmount)} hint={`${inr(contract.ratePerVisit)} per visit`} />
          <Stat label="Unpaid bills" value={inr(contract.unpaidAmount)} />
        </div>
      ) : null}

      {contract.today?.dayCode ? (
        <p className="mt-4 rounded-xl bg-brand-50 px-4 py-3 text-sm text-brand-700 dark:bg-brand-500/15 dark:text-brand-300">
          Today&apos;s visit code: <span className="nums font-semibold tracking-[0.3em]">{contract.today.dayCode}</span>
          <span className="block text-xs opacity-80">Share it with your professional when they arrive.</span>
        </p>
      ) : null}

      <div className="mt-4 flex flex-wrap items-center gap-2 border-t border-line pt-4">
        <Link
          href={`/monthly-service/${contract.id}`}
          className="text-sm font-medium text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
        >
          Attendance &amp; bills
        </Link>
      </div>
    </Card>
  );
}
