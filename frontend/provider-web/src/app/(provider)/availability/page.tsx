"use client";

import { PageHeading } from "@/components/ui";
import { BlackoutDatesSection } from "./_components/BlackoutDatesSection";
import { WindowsSection } from "./_components/WindowsSection";

/** Availability screen (docs/PROVIDER.md's `provider_availability` table): weekly windows plus blackout dates. */
export default function AvailabilityPage() {
  return (
    <div className="mx-auto flex w-full max-w-4xl flex-col gap-6">
      <PageHeading title="Availability" subtitle="When you're open for jobs, and the dates you're not." />
      <WindowsSection />
      <BlackoutDatesSection />
    </div>
  );
}
