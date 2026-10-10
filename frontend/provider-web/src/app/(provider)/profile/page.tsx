"use client";

import { useEffect, useState } from "react";
import { PageHeading, Tabs } from "@/components/ui";
import { BankAccountSection } from "./_components/BankAccountSection";
import { GoLiveChecklistSection } from "./_components/GoLiveChecklistSection";
import { KycSection } from "./_components/KycSection";
import { PhotoSection } from "./_components/PhotoSection";
import { ProfileDetailsSection } from "./_components/ProfileDetailsSection";
import { RatingsPromoSection } from "./_components/RatingsPromoSection";
import { ReferralPromoSection } from "./_components/ReferralPromoSection";
import { ServiceAreasSection } from "./_components/ServiceAreasSection";
import { SkillsSection } from "./_components/SkillsSection";
import { SupportPromoSection } from "./_components/SupportPromoSection";

type ProfileTab = "details" | "verification" | "coverage";

/**
 * The go-live checklist and the persistent `GoLiveBanner` (shown on every
 * authenticated screen, `/profile` included) both deep-link into a specific
 * section via `/profile#kyc`/`#service-areas`/`#skills` (lib/go-live.ts) -
 * now that those sections live behind tabs instead of one long scroll, this
 * maps each hash to the tab that hosts it, so a "fix this" click still lands
 * on visible content instead of an anchor inside a hidden tab.
 */
const HASH_TO_TAB: Record<string, ProfileTab> = {
  kyc: "verification",
  "service-areas": "coverage",
  skills: "coverage",
};

/**
 * Provider profile/onboarding (docs/PROVIDER.md's Identity and Capability &
 * Coverage domains). Grouped into tabs by purpose - Details (who you are,
 * how you look), Verification (KYC), Coverage & skills (where and what you
 * do) - rather than eight cards stacked in one long scroll, so a provider
 * only sees what's relevant to the thing they came to fix.
 *
 * The go-live checklist stays above the tabs, always visible: it's
 * actionable guidance ("here's what's missing"), not a content section
 * itself, and it - along with the promo cards below the tabs - are read by
 * every provider regardless of which tab they're on.
 *
 * Each section still owns its own query, mutation and three states, so one
 * failing lookup never blanks out the rest of the screen - unchanged from
 * before this redesign, just now organized under tabs instead of stacked.
 */
export default function ProfilePage() {
  const [tab, setTab] = useState<ProfileTab>("details");

  useEffect(() => {
    const jumpToHash = () => {
      const hash = window.location.hash.slice(1);
      const targetTab = HASH_TO_TAB[hash];
      if (!targetTab) return;
      setTab(targetTab);
      // Wait a paint for the target tab's content to mount before scrolling.
      requestAnimationFrame(() => {
        document.getElementById(hash)?.scrollIntoView({ block: "start" });
      });
    };

    jumpToHash();
    // Covers a "fix this" link clicked while already on /profile (the
    // banner renders here too) - that's a same-page hash change, which
    // never re-runs a mount-only effect.
    window.addEventListener("hashchange", jumpToHash);
    return () => window.removeEventListener("hashchange", jumpToHash);
  }, []);

  return (
    <div className="flex w-full max-w-4xl animate-rise flex-col gap-6">
      <PageHeading title="Profile" subtitle="Your identity, verification status, coverage and skills." />

      <GoLiveChecklistSection />

      <Tabs
        label="Profile sections"
        value={tab}
        onChange={setTab}
        tabs={[
          { value: "details", label: "Details" },
          { value: "verification", label: "Verification" },
          { value: "coverage", label: "Coverage & skills" },
        ]}
      />

      {tab === "details" ? (
        <div className="flex flex-col gap-6">
          <ProfileDetailsSection />
          <PhotoSection />
        </div>
      ) : null}

      {tab === "verification" ? (
        <div className="flex flex-col gap-6">
          <div id="kyc" className="scroll-mt-24">
            <KycSection />
          </div>
          {/* Placed right after KYC - related verification concerns, and the
              BankAccountProof KYC document above is this section's own
              supporting evidence (docs/PROVIDER.md OPEN DECISIONS #3). */}
          <div id="bank-account" className="scroll-mt-24">
            <BankAccountSection />
          </div>
        </div>
      ) : null}

      {tab === "coverage" ? (
        <div className="flex flex-col gap-6">
          <div id="service-areas" className="scroll-mt-24">
            <ServiceAreasSection />
          </div>
          <div id="skills" className="scroll-mt-24">
            <SkillsSection />
          </div>
        </div>
      ) : null}

      <RatingsPromoSection />
      <ReferralPromoSection />
      <SupportPromoSection />
    </div>
  );
}
