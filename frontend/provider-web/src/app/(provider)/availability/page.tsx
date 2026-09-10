"use client";

import Link from "next/link";
import { Button, PageHeading } from "@/components/ui";
import { BlackoutDatesSection } from "./_components/BlackoutDatesSection";
import { WindowsSection } from "./_components/WindowsSection";

/** Availability screen (docs/PROVIDER.md's `provider_availability` table): weekly windows plus blackout dates. */
export default function AvailabilityPage() {
  return (
    <div className="flex w-full max-w-4xl animate-rise flex-col gap-6">
      <PageHeading
        title="Availability"
        subtitle="When you're open for jobs, and the dates you're not."
        actions={
          // Entry point into /calendar (docs/OPEN-FIXES-FEATURES.csv
          // "Calendar and week view") - see that page's own header comment
          // for why it links from here rather than sitting in the primary
          // nav.
          <Link href="/calendar">
            <Button type="button" variant="secondary" size="sm">
              View week calendar
            </Button>
          </Link>
        }
      />
      <WindowsSection />
      <BlackoutDatesSection />
    </div>
  );
}
