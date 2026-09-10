"use client";

import { NavTabs } from "@/components/nav-tabs";

/**
 * Sub-nav between the provider directory and the performance ranking list
 * (docs/OPEN-FIXES-FEATURES.csv "Admin Web, Proposed new page, Provider
 * performance") - mirrors CatalogTabs/ServiceabilityTabs' own pattern of a
 * `_components/*Tabs.tsx` per admin module.
 */
export function ProvidersTabs() {
  return (
    <NavTabs
      label="Provider sections"
      tabs={[
        // No matchPrefixes on "Directory": "/providers/[providerId]" (the
        // detail page) does not render this strip at all, and a blanket
        // "/providers/" prefix would also match "/providers/performance"
        // below, highlighting both tabs at once.
        { href: "/providers", label: "Directory" },
        { href: "/providers/performance", label: "Performance" },
      ]}
    />
  );
}
