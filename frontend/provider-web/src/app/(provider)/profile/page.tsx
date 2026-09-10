"use client";

import { PageHeading } from "@/components/ui";
import { GoLiveChecklistSection } from "./_components/GoLiveChecklistSection";
import { KycSection } from "./_components/KycSection";
import { PhotoSection } from "./_components/PhotoSection";
import { ProfileDetailsSection } from "./_components/ProfileDetailsSection";
import { ReferralPromoSection } from "./_components/ReferralPromoSection";
import { ServiceAreasSection } from "./_components/ServiceAreasSection";
import { SkillsSection } from "./_components/SkillsSection";

/**
 * Provider profile/onboarding (docs/PROVIDER.md's Identity and Capability &
 * Coverage domains), ordered the way onboarding actually runs: who you are,
 * how you look to a customer, prove it, where you work, what you do.
 *
 * Each section owns its own query, mutation and three states, so one failing
 * lookup never blanks out the rest of the screen. Each anchor id below pairs
 * with `scroll-mt-24` so a "fix this" link (the go-live checklist here, or
 * the layout's persistent banner) lands below the sticky header instead of
 * tucked underneath it.
 */
export default function ProfilePage() {
  return (
    <div className="flex w-full max-w-4xl animate-rise flex-col gap-6">
      <PageHeading
        title="Profile"
        subtitle="Your identity, verification status, coverage and skills."
      />
      <GoLiveChecklistSection />
      <ProfileDetailsSection />
      <PhotoSection />
      <div id="kyc" className="scroll-mt-24">
        <KycSection />
      </div>
      <div id="service-areas" className="scroll-mt-24">
        <ServiceAreasSection />
      </div>
      <div id="skills" className="scroll-mt-24">
        <SkillsSection />
      </div>
      <ReferralPromoSection />
    </div>
  );
}
