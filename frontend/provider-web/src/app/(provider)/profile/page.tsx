"use client";

import { PageHeading } from "@/components/ui";
import { KycSection } from "./_components/KycSection";
import { ProfileDetailsSection } from "./_components/ProfileDetailsSection";
import { ServiceAreasSection } from "./_components/ServiceAreasSection";
import { SkillsSection } from "./_components/SkillsSection";

/**
 * Provider profile/onboarding screen (docs/PROVIDER.md's Identity and
 * Capability & Coverage domains): profile details, KYC status/submission,
 * service areas and skills.
 */
export default function ProfilePage() {
  return (
    <div className="mx-auto flex w-full max-w-4xl flex-col gap-6">
      <PageHeading title="Profile" subtitle="Your identity, verification status, coverage and skills." />
      <ProfileDetailsSection />
      <KycSection />
      <ServiceAreasSection />
      <SkillsSection />
    </div>
  );
}
