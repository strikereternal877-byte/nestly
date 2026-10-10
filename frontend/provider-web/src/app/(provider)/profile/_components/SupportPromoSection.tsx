"use client";

import Link from "next/link";
import { Button, Card } from "@/components/ui";

/**
 * Entry point into Help & Support from Profile (Provider Management UX
 * pass), mirroring `RatingsPromoSection`/`ReferralPromoSection`'s own
 * placement decision: reaching support is occasional, not a daily action, so
 * it does not compete for a slot in ProviderSidebar/ProviderTabBar's primary
 * nav. The header bell's notification list also deep-links here for a
 * support reply, so this promo card is not the only way in.
 */
export function SupportPromoSection() {
  return (
    <Card
      title="Help & Support"
      description="Answers to common questions, and a direct line to the Glavyx support team."
    >
      <Link href="/support">
        <Button type="button" variant="secondary">
          Get help
        </Button>
      </Link>
    </Card>
  );
}
