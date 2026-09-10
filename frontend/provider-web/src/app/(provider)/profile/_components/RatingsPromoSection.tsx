import Link from "next/link";
import { Button, Card } from "@/components/ui";

/**
 * Entry point into Ratings & feedback (docs/OPEN-FIXES-FEATURES.csv "Ratings
 * and feedback") from Profile, mirroring `ReferralPromoSection`'s own
 * placement decision: a provider checks their rating occasionally, not on
 * every shift, so it does not compete for a slot in
 * ProviderSidebar/ProviderTabBar's primary nav - already sized for the five
 * screens a provider works from daily (Today/Offers/Jobs/Availability/
 * Earnings) before Profile itself.
 */
export function RatingsPromoSection() {
  return (
    <Card
      title="Ratings & feedback"
      description="See your running average rating and what customers have said about your work."
    >
      <Link href="/ratings">
        <Button type="button" variant="secondary">
          View your ratings
        </Button>
      </Link>
    </Card>
  );
}
