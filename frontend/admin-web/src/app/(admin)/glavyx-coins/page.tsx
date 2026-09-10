"use client";

import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { PageHeading, Skeleton, SkeletonText, Tabs } from "@/components/ui";
import { SectionError } from "@/components/screen-states";
import { canWriteModule } from "@/lib/permissions";
import { useAdminClaims } from "@/lib/use-admin-claims";
import { IssuedReportCard } from "./_components/IssuedReportCard";
import { ProgramConfigCard } from "./_components/ProgramConfigCard";
import { AUDIENCE_TABS, NestlyCoinsAudience, getCoinsConfig } from "./_lib/coins-api";

/**
 * Nestly Coins program administration (task 202): the per-audience earn rate,
 * eligibility rules, cap, expiry and clawback window, plus the issued/clawed
 * back report.
 *
 * The `(admin)` layout already wraps every route in this group in
 * `RequireAdminAuth`; this page used to mount a second copy of that guard
 * inside it, which rendered the "Loading…" fallback twice on a cold load.
 */
/** Shaped like `ProgramConfigCard`: a header, a two-column field grid, two toggles. */
function ConfigCardSkeleton() {
  return (
    <div className="rounded-2xl bg-surface p-6 shadow-sm">
      <Skeleton className="h-4 w-36" />
      <Skeleton className="mt-2 h-3.5 w-80 max-w-full" />
      <div className="mt-6 grid grid-cols-1 gap-4 sm:grid-cols-2">
        {Array.from({ length: 5 }, (_, index) => (
          <div key={index} className="flex flex-col gap-2">
            <Skeleton className="h-3.5 w-32" />
            <Skeleton className="h-10 w-full" />
          </div>
        ))}
      </div>
      <div className="mt-6">
        <SkeletonText lines={2} />
      </div>
    </div>
  );
}

export default function NestlyCoinsPage() {
  const claims = useAdminClaims();
  const canWrite = canWriteModule(claims, "nestly-coins");

  const [audience, setAudience] = useState<string>(String(NestlyCoinsAudience.Customer));
  const audienceValue = Number(audience) as NestlyCoinsAudience;

  const configQuery = useQuery({
    queryKey: ["nestly-coins-config", audienceValue] as const,
    queryFn: () => getCoinsConfig(audienceValue),
  });

  return (
    <div className="flex w-full max-w-4xl flex-col gap-6">
      <div>
        <PageHeading
          title="Glavyx Coins"
          subtitle="Per-audience earn rate, reorder rule, monthly cap, expiry and clawback window (NESTLY-COINS.md)."
        />
        <Tabs
          tabs={AUDIENCE_TABS}
          value={audience}
          onChange={setAudience}
          label="Audience"
          className="mb-6"
        />
      </div>

      {configQuery.isPending ? (
        <ConfigCardSkeleton />
      ) : configQuery.isError ? (
        <SectionError error={configQuery.error} onRetry={() => void configQuery.refetch()}>
          The program config for this audience could not be loaded.
        </SectionError>
      ) : (
        <ProgramConfigCard
          // Remount on audience change so the form state is rebuilt from the
          // new audience's values rather than merged into the previous ones.
          key={audienceValue}
          audience={audienceValue}
          config={configQuery.data}
          canWrite={canWrite}
        />
      )}

      {/* Deliberately not keyed on the audience: the query key already carries
          it, and remounting would throw away the date range the admin chose. */}
      <IssuedReportCard audience={audienceValue} />
    </div>
  );
}
