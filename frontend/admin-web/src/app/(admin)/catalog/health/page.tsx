"use client";

import { PageHeading } from "@/components/ui";
import { CatalogTabs } from "../_components/CatalogTabs";
import { CatalogHealthSection } from "./_components/CatalogHealthSection";

/**
 * Catalog health (docs/OPEN-FIXES-FEATURES.csv "Admin Web, Proposed new
 * page, Catalog health"): a pre-publish completeness audit over active
 * services, surfacing incomplete catalog entries a customer would otherwise
 * find first. Warning/audit only, matching the coverage gap map's
 * non-destructive approach — see {@link CatalogHealthSection}'s doc comment.
 */
export default function CatalogHealthPage() {
  return (
    <div className="flex w-full max-w-6xl flex-col gap-6">
      <div>
        <PageHeading
          title="Catalog Health"
          subtitle="Active services missing a price, image, serviceability mapping, or booking history."
        />
        <CatalogTabs />
      </div>

      <CatalogHealthSection />
    </div>
  );
}
