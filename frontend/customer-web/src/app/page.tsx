import { CategoryQuickPicks } from "@/components/CategoryQuickPicks";
import { CuratedHomeSections } from "@/components/CuratedHomeSections";
import { GetTheAppSection } from "@/components/GetTheAppSection";
import { HeroBanner } from "@/components/HeroBanner";
import { LocationPrompt } from "@/components/LocationPrompt";
import { TrustMarkers } from "@/components/TrustMarkers";

export default function Home() {
  return (
    <main className="flex w-full flex-col gap-14 pb-14 sm:pb-20">
      <LocationPrompt />
      {/* Full-bleed, edge-to-edge: sits flush under the sticky header with no
          side padding or card chrome, unlike every other section here (which
          is why it's outside the max-w-7xl wrapper below, matching the
          categories band's existing full-bleed pattern). The quick-picks rail
          is a sibling overlay, not a HeroBanner prop - it never touches the
          banner's own CMS-driven slide/carousel logic. */}
      <div className="relative">
        <HeroBanner />
        {/* `sm` and up only: anchored to the hero's BOTTOM-left corner, not
            the top - the hero's own headline/subtitle/search bar are
            centered and occupy the top-to-middle band of the hero, confirmed
            by measuring real coordinates to collide with this widget when it
            sat up there (a 300px-wide panel reaches far enough right to
            underlap even a centered headline's left edge). The bottom-left
            corner is genuinely clear of all of that on a full-height desktop
            hero. */}
        <div className="pointer-events-none absolute inset-x-0 bottom-0 z-20 hidden justify-start px-5 pb-6 sm:flex sm:px-8 sm:pb-8">
          <div className="pointer-events-auto w-[300px] max-w-[calc(100vw-2.5rem)]">
            <CategoryQuickPicks variant="overlay" />
          </div>
        </div>
      </div>

      {/* Below `sm` only: NOT an overlay. Measuring real coordinates found
          only ~46px of clearance between the search bar and a short mobile
          hero's bottom edge - too little for any reasonably-sized floating
          widget there. Rendering it in normal document flow right after the
          hero guarantees zero overlap by construction instead of continuing
          to shrink pixels to fit a budget that doesn't exist on this
          breakpoint. */}
      <div className="px-4 sm:hidden">
        <CategoryQuickPicks variant="inline" />
      </div>

      <div className="mx-auto w-full max-w-7xl px-4 sm:px-6">
        <CuratedHomeSections />
      </div>

      <div className="mx-auto w-full max-w-7xl px-4 sm:px-6">
        <section aria-labelledby="why-heading" className="flex flex-col gap-6">
          <h2 id="why-heading" className="text-xl font-semibold tracking-tight text-fg">
            Why book with Glavyx
          </h2>
          <TrustMarkers />
        </section>
      </div>

      <div className="mx-auto w-full max-w-7xl px-4 sm:px-6">
        <GetTheAppSection />
      </div>
    </main>
  );
}
