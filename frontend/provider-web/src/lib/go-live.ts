/**
 * Copy and fix-it links for the go-live checklist
 * (docs/OPEN-FIXES-FEATURES.csv "Provider Web, Proposed new page, Onboarding
 * checklist and go-live status"), shared between the persistent banner
 * (`(provider)/layout.tsx`) and the checklist section on `/profile` so the
 * two surfaces never drift out of sync on wording or destination.
 */
import type { GoLiveCheck } from "./profile-types";

export interface GoLiveCheckCopy {
  /** What the persistent banner says is missing - short and specific, never generic. */
  bannerMessage: string;
  /** Where "fix this" sends the provider. */
  href: string;
  /** What the fix-it link itself reads. */
  linkLabel: string;
}

const GO_LIVE_CHECK_COPY: Record<string, GoLiveCheckCopy> = {
  kycApproved: {
    bannerMessage: "Complete your KYC to start receiving jobs.",
    href: "/profile#kyc",
    linkLabel: "Submit KYC documents",
  },
  hasActiveSkill: {
    bannerMessage: "Add a skill to start receiving jobs.",
    href: "/profile#skills",
    linkLabel: "Add a skill",
  },
  hasActiveServiceArea: {
    bannerMessage: "Add a service area to start receiving jobs.",
    href: "/profile#service-areas",
    linkLabel: "Add a service area",
  },
  hasAvailability: {
    bannerMessage: "Set your weekly availability to start receiving jobs.",
    href: "/availability",
    linkLabel: "Set availability",
  },
};

/** Falls back to the check's own server-sent label/a profile link for any key this client doesn't recognize yet, so an added backend check degrades gracefully instead of disappearing. */
export const getGoLiveCheckCopy = (check: GoLiveCheck): GoLiveCheckCopy =>
  GO_LIVE_CHECK_COPY[check.key] ?? {
    bannerMessage: `${check.label} to start receiving jobs.`,
    href: "/profile",
    linkLabel: "Go to profile",
  };
