"use client";

import Link from "next/link";
import { motion } from "motion/react";
import { SearchBar } from "@/components/SearchBar";
import { categoryTabBg } from "@/lib/categoryVisuals";

/**
 * Hero / primary CTA (SRS 11.1.2). Content is static for now: there is no
 * admin-configurable banner backend yet (no Banner/Promotion entity or API
 * anywhere in the catalog module) - SRS 11.1.3's "banner visibility, order,
 * and content shall be admin-configurable" is not implemented server-side,
 * so this deliberately does not fabricate one. Swap this component's content
 * for a data-driven one once that API exists.
 *
 * THE SERVICE LEDGER (candidate 7/7, seed 394fa208), FIRST VIEWPOINT: a
 * full-bleed kraft ledger card floats on a dark ink ground, category tabs
 * run across its top edge like real binder dividers, and one open "page"
 * shows a real booking row mid-motion — a stamp landing on load. The eight
 * tab labels are the product's real categories (public/images/categories),
 * not fabricated ones; they're decorative navigation here (each links to its
 * category), the same "make trust felt, not stated" role the previous
 * avatar cluster played.
 */
const HERO_CATEGORY_TABS = [
  { slug: "home-cleaning", label: "Cleaning" },
  { slug: "plumbing", label: "Plumbing" },
  { slug: "electrical", label: "Electrical" },
  { slug: "carpentry", label: "Carpentry" },
  { slug: "painting", label: "Painting" },
  { slug: "ac-repair-service", label: "AC repair" },
  { slug: "salon-for-women", label: "Salon" },
  { slug: "pest-control", label: "Pest control" },
] as const;

export function HeroBanner() {
  return (
    <section className="hero-mesh relative isolate overflow-hidden rounded-3xl px-3 pb-3 pt-8 shadow-xl sm:px-8 sm:pb-8 sm:pt-12">
      <div
        aria-hidden
        className="texture-grain pointer-events-none absolute inset-0 -z-10 opacity-[0.06] mix-blend-overlay"
      />

      <div className="mx-auto max-w-lg text-center sm:max-w-2xl">
        <p className="mb-3 inline-flex animate-rise items-center gap-2 rounded-full bg-white/10 px-3 py-1 text-xs font-medium text-white backdrop-blur-sm">
          <span className="h-1.5 w-1.5 rounded-full bg-brand-400" aria-hidden />
          Vetted professionals, upfront pricing
        </p>
        <h1
          style={{ animationDelay: "70ms" }}
          className="font-display animate-rise text-balance text-2xl leading-tight text-white sm:text-4xl"
        >
          Your home&rsquo;s service record, already open.
        </h1>
        <p
          style={{ animationDelay: "140ms" }}
          className="mx-auto mt-3 max-w-xl animate-rise text-[0.9375rem] leading-relaxed text-white/75 text-pretty"
        >
          Pick a tab, see the price before you book, and let recurring visits
          stamp the record automatically.
        </p>
      </div>

      {/* The floating kraft ledger card. */}
      <motion.div
        initial={{ opacity: 0, y: 18 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ duration: 0.5, ease: [0.16, 1, 0.3, 1], delay: 0.18 }}
        className="relative mt-8 overflow-hidden rounded-xl border-2 border-line-strong bg-surface shadow-2xl"
      >
        {/* Binder-divider tabs across the top edge. */}
        <nav aria-label="Popular categories" className="flex flex-wrap gap-1.5 border-b border-line bg-surface-2 px-3 pt-3 sm:px-5">
          {HERO_CATEGORY_TABS.map((tab) => (
            <Link
              key={tab.slug}
              href={`/categories/${tab.slug}`}
              className={`font-display shrink-0 rounded-t-md px-3 py-2 text-[0.6875rem] uppercase tracking-[0.05em] text-fg-on-brand transition-transform duration-fast ease-out hover:-translate-y-0.5 ${categoryTabBg(tab.slug)}`}
            >
              {tab.label}
            </Link>
          ))}
        </nav>

        <div className="grid gap-6 p-5 sm:grid-cols-[1fr_18rem] sm:p-8">
          <div className="flex min-w-0 flex-col gap-5">
            {/* `default`, not `hero`: the search box now sits on the kraft
                card's own light surface rather than directly on the dark ink
                ground, so it needs the standard border/ring treatment, not
                the white-on-dark one built for the old gradient hero. */}
            <SearchBar variant="default" />

            {/* A single real ledger row, mid-motion: the stamp lands on
                mount. Decorative sample data, clearly labelled as such via
                the "Sample entry" caption rather than presented as a real
                booking. */}
            <LedgerRowSample />
          </div>

          <div className="hidden flex-col justify-center gap-3 border-l border-line pl-6 sm:flex">
            <p className="font-display text-xs uppercase tracking-[0.08em] text-fg-subtle">
              Recurring visits
            </p>
            <p className="text-sm leading-relaxed text-fg-muted">
              Set a schedule once — every visit books, prices, and stamps the
              record on its own.
            </p>
            <Link
              href="/recurring-bookings"
              className="mt-1 inline-flex items-center gap-1.5 text-sm font-semibold text-brand-700 hover:underline dark:text-brand-300"
            >
              Manage recurring plans
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.25" strokeLinecap="round" strokeLinejoin="round" className="h-3.5 w-3.5" aria-hidden>
                <path d="M5 12h14M13 6l6 6-6 6" />
              </svg>
            </Link>
          </div>
        </div>
      </motion.div>
    </section>
  );
}

function LedgerRowSample() {
  return (
    <div className="rounded-lg border border-line bg-surface-2 p-4">
      <p className="mb-3 text-[0.6875rem] font-medium uppercase tracking-wide text-fg-subtle">
        Sample entry
      </p>
      <div className="flex items-center justify-between gap-4">
        <div className="min-w-0">
          <p className="truncate font-medium text-fg">Deep home cleaning</p>
          <p className="nums font-mono text-xs text-fg-subtle">Sat, 14 Feb · 10:00–12:00</p>
        </div>
        <div className="flex shrink-0 items-center gap-3">
          <span className="nums font-mono text-sm font-semibold text-fg">₹1,499</span>
          <motion.span
            initial={{ opacity: 0, scale: 2.2, rotate: -14 }}
            animate={{ opacity: 1, scale: 1, rotate: -8 }}
            transition={{ type: "spring", stiffness: 500, damping: 18, delay: 0.6 }}
            className="inline-flex items-center gap-1 rounded-sm border-2 border-brand-600 px-2 py-1 text-[0.625rem] font-bold uppercase tracking-wide text-brand-700 dark:text-brand-300"
          >
            Confirmed
          </motion.span>
        </div>
      </div>
    </div>
  );
}
