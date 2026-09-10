"use client";

import { PageHeading } from "@/components/ui";
import { canWriteModule } from "@/lib/permissions";
import { useAdminClaims } from "@/lib/use-admin-claims";
import { CoverageGapsSection } from "../_components/CoverageGapsSection";
import { MappedWithoutProviderCoverageSection } from "../_components/MappedWithoutProviderCoverageSection";
import { ServiceabilityTabs } from "../_components/ServiceabilityTabs";
import { UnmappedActiveServicesSection } from "../_components/UnmappedActiveServicesSection";

/**
 * Coverage gap map (docs/OPEN-FIXES-FEATURES.csv "Admin Web, Proposed new
 * page, Coverage gap map"): a pincode-by-service view flagging every way a
 * service can end up unbookable somewhere that a plain mapping list doesn't
 * surface on its own -
 *
 *   1. Has providers but no serviceability mapping — {@link UnmappedActiveServicesSection}
 *   2. Has mapping but no active provider — {@link MappedWithoutProviderCoverageSection}
 *   3. No coverage at all — an active service with neither, which shows up in
 *      quadrant 1 regardless of provider coverage (see that endpoint's doc
 *      comment: it does not depend on provider state).
 *
 * Also surfaces the reverse of quadrant 1 — provider coverage with no mapping
 * yet ({@link CoverageGapsSection}) — completing the cross-reference between
 * active services, serviceability mappings and live provider coverage per
 * pincode the CSV row's fix describes. Same permission gating as the other
 * two serviceability screens: reachable only once the "serviceability"
 * module is visible, and every "create mapping" action additionally checks
 * `canWriteModule`.
 */
export default function CoverageGapMapPage() {
  const claims = useAdminClaims();
  const canWrite = canWriteModule(claims, "serviceability");

  return (
    <div className="w-full max-w-6xl">
      <PageHeading
        title="Coverage Gap Map"
        subtitle="Cross-references active services, serviceability mappings and live provider coverage per pincode."
      />

      <ServiceabilityTabs />

      <div className="flex flex-col gap-6">
        <UnmappedActiveServicesSection canWrite={canWrite} />
        <CoverageGapsSection canWrite={canWrite} />
        <MappedWithoutProviderCoverageSection />
      </div>
    </div>
  );
}
