import Link from "next/link";
import { CategoryTiles } from "@/components/CategoryTiles";
import { HeroBanner } from "@/components/HeroBanner";
import { TrustMarkers } from "@/components/TrustMarkers";

export default function Home() {
  return (
    <main className="flex w-full flex-col gap-14 pb-14 sm:pb-20">
      <div className="mx-auto w-full max-w-7xl px-4 pt-8 sm:px-6 sm:pt-12">
        <HeroBanner />
      </div>

      {/* Full-bleed banded section: a stamp-tinted band, centered
          heading/subtitle, the ledger-tab category grid, and a centered
          pill CTA below it — breaks out of the page's max-w wrapper on
          purpose, unlike every other section here. */}
      <section aria-labelledby="categories-heading" className="bg-brand-50 py-14 dark:bg-brand-500/[0.06] sm:py-20">
        <div className="mx-auto flex w-full max-w-7xl flex-col items-center gap-10 px-4 sm:px-6">
          <div className="flex max-w-xl flex-col items-center gap-3 text-center">
            <h2 id="categories-heading" className="font-display text-2xl text-fg sm:text-display-sm">
              Every tab, a category
            </h2>
            <p className="text-[0.9375rem] leading-relaxed text-fg-muted text-pretty">
              Everything your home needs, from one place — vetted professionals across every service.
            </p>
          </div>

          <div className="w-full">
            <CategoryTiles />
          </div>

          <Link
            href="/categories"
            className="inline-flex items-center gap-2 rounded-full border-2 border-line-strong bg-surface px-6 py-3 text-sm font-semibold text-fg shadow-xs transition-colors duration-fast ease-out hover:border-brand-600/50 hover:text-brand-600"
          >
            Browse all categories
            <svg
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2.25"
              strokeLinecap="round"
              strokeLinejoin="round"
              className="h-3.5 w-3.5"
              aria-hidden
            >
              <path d="M5 12h14M13 6l6 6-6 6" />
            </svg>
          </Link>
        </div>
      </section>

      <div className="mx-auto w-full max-w-7xl px-4 sm:px-6">
        <section aria-labelledby="why-heading" className="flex flex-col gap-6">
          <h2 id="why-heading" className="font-display text-lg text-fg">
            Why book with Nestly
          </h2>
          <TrustMarkers />
        </section>
      </div>
    </main>
  );
}
