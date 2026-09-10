"use client";

import { NavTabs } from "@/components/nav-tabs";

/**
 * Sub-nav across the serviceability module: SRS 12.9.1 geography master,
 * 12.9.2 mapping, and the coverage gap map (docs/OPEN-FIXES-FEATURES.csv
 * "Admin Web, Proposed new page, Coverage gap map").
 */
export function ServiceabilityTabs() {
  return (
    <NavTabs
      label="Serviceability sections"
      tabs={[
        { href: "/serviceability", label: "Geography master" },
        { href: "/serviceability/mappings", label: "Serviceability mapping" },
        { href: "/serviceability/coverage-gaps", label: "Coverage gap map" },
      ]}
    />
  );
}
