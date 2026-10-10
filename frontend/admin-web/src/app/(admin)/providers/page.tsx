"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useMemo, useState } from "react";
import { Alert, Button, Card, Field, KpiCard, PageHeading, Skeleton } from "@/components/ui";
import type { ChartTone } from "@/components/ui";
import { describeError } from "@/lib/api";
import { getProviderOnboardingOverview } from "@/lib/providers-api";
import { ProviderOnboardingStatus, ProviderStatus } from "@/lib/providers-types";
import { todayIsoDate } from "@/lib/date";
import { ProvidersTabs } from "./_components/ProvidersTabs";

/**
 * Provider Onboarding Overview dashboard (new admin-web page, per the admin's
 * request: "the overview ... counts for onboarding, document verification,
 * verified, pending, live/active, with each count clicking through to the
 * filtered provider list").
 *
 * This is a cumulative funnel, not a single day's cohort
 * (docs/OPEN-FIXES-FEATURES.csv "Provider Onboarding Overview": counting only
 * one day's registrations made the tiles look far emptier than the real,
 * ongoing funnel): every tile reads every provider whose `createdAt` falls on
 * or before the selected "as of" date (default today - same `date` query
 * param, default-to-today convention as the Fulfilment Control Room's own
 * date picker), then asks where that whole set currently stands across two
 * independent dimensions of `Provider` (`ProviderOnboardingStatus` and
 * `ProviderStatus` - see Provider.cs's own doc comments). "Live" and "Active"
 * can both be true of the same provider at once - they are not mutually
 * exclusive buckets, matching `AdminProviderOnboardingOverviewResponse`'s
 * doc comment on the backend.
 *
 * Backend: one endpoint, `GET /admin/providers/onboarding-overview?date=`
 * (`getProviderOnboardingOverview`) - a single query as-of that date, not six.
 *
 * This is the Providers module's landing page (`/providers`) - the directory
 * moved to `/providers/directory` so the module opens on this dashboard
 * first, matching Customers' own Analytics-first landing (both are the only
 * two modules with a real KPI dashboard among their tabs; every other module
 * still lands on its base list, since it has no such dashboard to prefer).
 *
 * Each tile is a `KpiCard` (the same stat-tile component `/dashboard`
 * renders its own KPI row with - reused rather than rebuilt) wrapped in a
 * `Link` to `/providers/directory`, pre-filtered to on-or-before this date at
 * that stage via the search page's own `status`/`onboardingStatus`/`createdTo`
 * query params (see providers/directory/page.tsx's own doc comment on reading
 * them). Processing a provider from there (approve/reject KYC, activate,
 * suspend) reuses the directory's existing detail-page actions - this
 * dashboard adds no new write endpoints, only a funnel view onto data those
 * actions already mutate.
 */
export default function ProviderOnboardingOverviewPage() {
  const [date, setDate] = useState(() => todayIsoDate());

  const query = useQuery({
    queryKey: ["admin-provider-onboarding-overview", date] as const,
    queryFn: () => getProviderOnboardingOverview(date),
  });

  // Only an upper bound - this dashboard is cumulative as-of `date`, so a
  // tile's link must match every provider up to and including it, not just
  // providers created on that exact day. providers/directory/page.tsx
  // converts to the UTC instant its search API takes (day-range.ts's
  // endOfLocalDayUtc), the same way this dashboard's own query does.
  const asOfParams = useMemo(() => {
    const params = new URLSearchParams();
    params.set("createdTo", date);
    return params;
  }, [date]);

  function tileHref(extra?: Record<string, string>): string {
    const params = new URLSearchParams(asOfParams);
    for (const [key, value] of Object.entries(extra ?? {})) params.set(key, value);
    return `/providers/directory?${params.toString()}`;
  }

  const tiles: {
    key: string;
    label: string;
    tone: ChartTone;
    value: number | undefined;
    href: string;
    hint: string;
  }[] = [
    {
      key: "cohort",
      label: "Total onboarding",
      tone: "brand",
      value: query.data?.totalOnboardingCount,
      href: tileHref(),
      hint: "Every provider registered on or before this date, at any stage.",
    },
    {
      key: "docs",
      label: "Document verification",
      tone: "warning",
      value: query.data?.documentVerificationCount,
      href: "/providers/verification",
      hint: "KYC submitted, awaiting an admin verdict - opens the verification queue to act on them.",
    },
    {
      key: "verified",
      label: "Verified",
      tone: "info",
      value: query.data?.verifiedCount,
      href: tileHref({ onboardingStatus: String(ProviderOnboardingStatus.KycVerified) }),
      hint: "KYC approved.",
    },
    {
      key: "pending",
      label: "Pending",
      tone: "danger",
      value: query.data?.pendingCount,
      href: tileHref({ status: String(ProviderStatus.PendingVerification) }),
      hint: "Not yet activated (ProviderStatus).",
    },
    {
      key: "live",
      label: "Live",
      tone: "success",
      value: query.data?.liveCount,
      href: tileHref({ onboardingStatus: String(ProviderOnboardingStatus.Completed) }),
      hint: "Onboarding flow fully complete.",
    },
    {
      key: "active",
      label: "Active",
      tone: "success",
      value: query.data?.activeCount,
      href: tileHref({ status: String(ProviderStatus.Active) }),
      hint: "Live and assignable (ProviderStatus).",
    },
  ];

  return (
    <div className="w-full max-w-6xl">
      <PageHeading
        title="Provider Onboarding Overview"
        subtitle="Every registered provider as of the selected date, by onboarding stage - click a count to see and process those providers."
      />
      <ProvidersTabs />

      <div className="mb-6 mt-6 flex flex-wrap items-end justify-between gap-4">
        <Field
          label="As of date"
          type="date"
          value={date}
          onChange={(event) => setDate(event.target.value)}
          className="w-48"
        />
        <Button variant="secondary" onClick={() => query.refetch()} loading={query.isFetching}>
          Refresh
        </Button>
      </div>

      {query.isError ? (
        <Alert tone="error" action={<Button size="sm" onClick={() => query.refetch()}>Retry</Button>}>
          {describeError(query.error)}
        </Alert>
      ) : null}

      {query.isPending ? (
        <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-6">
          {Array.from({ length: 6 }, (_, index) => (
            <div key={index} className="rounded-2xl bg-surface p-6 shadow-sm">
              <Skeleton className="h-4 w-24" />
              <Skeleton className="mt-3 h-8 w-16" />
            </div>
          ))}
        </div>
      ) : query.data ? (
        <>
          <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-6">
            {tiles.map((tile) => (
              <Link key={tile.key} href={tile.href} className="block" title={tile.hint}>
                <KpiCard
                  icon={<TileIcon tileKey={tile.key} />}
                  tone={tile.tone}
                  label={tile.label}
                  value={(tile.value ?? 0).toLocaleString("en-IN")}
                />
              </Link>
            ))}
          </div>

          <Card className="mt-6" title="How to read this">
            <div className="grid gap-5 sm:grid-cols-2">
              <div>
                <p className="text-xs font-semibold uppercase tracking-wide text-fg-subtle">Onboarding progress</p>
                <p className="mt-1.5 text-sm text-fg-muted">
                  Has this provider finished signing up? <span className="font-medium text-fg">Document verification</span>{" "}
                  → <span className="font-medium text-fg">Verified</span> →{" "}
                  <span className="font-medium text-fg">Live</span> is the order it happens in.{" "}
                  <span className="font-medium text-fg">Total onboarding</span> is everyone, at any stage.
                </p>
              </div>
              <div>
                <p className="text-xs font-semibold uppercase tracking-wide text-fg-subtle">Account status</p>
                <p className="mt-1.5 text-sm text-fg-muted">
                  Can this provider get jobs right now? <span className="font-medium text-fg">Pending</span> = not yet
                  activated. <span className="font-medium text-fg">Active</span> = activated and assignable.
                </p>
              </div>
            </div>
            <p className="mt-4 border-t border-line pt-4 text-sm text-fg-muted">
              These are two separate questions about the same provider, so counts overlap — a provider who is both{" "}
              <span className="font-medium text-fg">Live</span> and <span className="font-medium text-fg">Active</span>{" "}
              is counted in both tiles, not split between them.
            </p>
          </Card>
        </>
      ) : null}
    </div>
  );
}

function TileIcon({ tileKey }: { tileKey: string }) {
  const common = {
    viewBox: "0 0 24 24",
    fill: "none",
    stroke: "currentColor",
    strokeWidth: "1.75",
    strokeLinecap: "round" as const,
    strokeLinejoin: "round" as const,
    className: "h-5 w-5",
    "aria-hidden": true,
  };

  switch (tileKey) {
    case "docs":
      return (
        <svg {...common}>
          <rect x="5" y="3.5" width="14" height="17" rx="1.5" />
          <path d="M8.5 8.5h7M8.5 12h7M8.5 15.5h4" />
        </svg>
      );
    case "verified":
      return (
        <svg {...common}>
          <path d="M12 3.5 5 6.5v5.5c0 4.6 3 7.6 7 8.5 4-0.9 7-3.9 7-8.5V6.5L12 3.5Z" />
          <path d="m9 12 2 2 4-4.5" />
        </svg>
      );
    case "pending":
      return (
        <svg {...common}>
          <circle cx="12" cy="12" r="8.5" />
          <path d="M12 7.5V12l3 2" />
        </svg>
      );
    case "live":
      return (
        <svg {...common}>
          <circle cx="12" cy="12" r="3" />
          <path d="M12 3.5v3M12 17.5v3M3.5 12h3M17.5 12h3" />
        </svg>
      );
    case "active":
      return (
        <svg {...common}>
          <path d="M12 3.5c4.7 0 8.5 3.8 8.5 8.5s-3.8 8.5-8.5 8.5-8.5-3.8-8.5-8.5" />
          <path d="m4.5 6.5 3 3" />
        </svg>
      );
    default:
      return (
        <svg {...common}>
          <path d="M4 20V10.5l8-6.5 8 6.5V20" />
          <path d="M9.5 20v-6h5v6" />
        </svg>
      );
  }
}
