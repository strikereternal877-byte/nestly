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

      {/* Full-bleed quiet-stone band — an editorial grid section, not a
          tinted brand band: restraint over decoration. Breaks out of the
          page's max-w wrapper on purpose, unlike every other section here. */}
      <section aria-labelledby="categories-heading" className="bg-surface-2 py-14 sm:py-20">
        <div className="mx-auto flex w-full max-w-7xl flex-col items-center gap-10 px-4 sm:px-6">
          <div className="flex max-w-xl flex-col items-center gap-3 text-center">
            <h2 id="categories-heading" className="font-display text-display-sm text-fg">
              Popular categories
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
            className="inline-flex items-center gap-2 rounded-full border border-line bg-surface px-6 py-3 text-sm font-semibold text-fg shadow-xs transition-colors duration-fast ease-out hover:border-brand-600/40 hover:text-brand-600"
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
          <h2 id="why-heading" className="font-display text-xl text-fg">
            Why book with Nestly
          </h2>
          <TrustMarkers />
        </section>
      </div>
    </main>
  );
}
