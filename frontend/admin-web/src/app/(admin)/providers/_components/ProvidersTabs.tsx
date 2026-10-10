"use client";

import { NavTabs } from "@/components/nav-tabs";

/**
 * Sub-nav between the Onboarding Overview dashboard, the provider directory,
 * and the performance ranking list (docs/OPEN-FIXES-FEATURES.csv "Admin Web,
 * Proposed new page, Provider performance") - mirrors CatalogTabs/
 * ServiceabilityTabs' own pattern of a `_components/*Tabs.tsx` per admin
 * module. Onboarding Overview is listed first and owns the module's root
 * (`/providers`) since it's the dashboard an admin actually wants to land on
 * - the directory moved to `/providers/directory` to make room (same
 * Overview-first landing as CustomersTabs).
 */
export function ProvidersTabs() {
  return (
    <NavTabs
      label="Provider sections"
      tabs={[
        // No matchPrefixes on "Onboarding Overview": pathname equality alone
        // is enough (NavTabs) - "/providers/directory" and
        // "/providers/performance" are distinct strings, and
        // "/providers/[providerId]" (the detail page) does not render this
        // strip at all.
        { href: "/providers", label: "Onboarding Overview" },
        { href: "/providers/directory", label: "Directory" },
        { href: "/providers/verification", label: "Verification queue" },
        { href: "/providers/performance", label: "Performance" },
      ]}
    />
  );
}
