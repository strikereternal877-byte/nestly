import Link from "next/link";
import { motion } from "motion/react";
import { useState } from "react";
import { SPRING } from "@/components/motion";
import { categoryTabBg } from "@/lib/categoryVisuals";
import type { CategorySummary } from "@/lib/types";

/**
 * Category card for the home/listing tile grid (SRS 11.1.2, 11.5.1).
 *
 * THE SERVICE LEDGER (candidate 7/7, seed 394fa208): a literal binder-divider
 * tab rather than a photo card — a coloured tab flap (one hue per category,
 * see src/lib/categoryVisuals.ts) sits above a kraft "page" body carrying the
 * category's real icon, a stamped rating chip, and a stamp-style CTA. The
 * rating/booking-count numbers stay deterministic decorative chrome (hashed
 * from the category id, not fetched data) exactly as before — Nestly has no
 * per-category rating today.
 */
export function CategoryTile({ category }: { category: CategorySummary }) {
  const [iconFailed, setIconFailed] = useState(false);
  const rating = decorativeRating(category.id);
  const bookings = decorativeBookingCount(category.id);
  const tabColor = categoryTabBg(category.slug);

  return (
    <Link href={`/categories/${category.slug}`} className="group block h-full">
      <motion.div
        whileHover={{ y: -4 }}
        whileTap={{ scale: 0.98 }}
        transition={SPRING}
        className="flex h-full flex-col overflow-hidden rounded-lg border-2 border-line bg-surface shadow-xs transition-shadow duration-200 ease-out group-hover:border-line-strong group-hover:shadow-lg"
      >
        {/* The tab flap — a real binder-divider silhouette (a notch cut into
            the bottom-left/right corners), not a rounded banner. */}
        <div
          className={`relative flex items-center justify-between gap-2 px-4 py-2.5 text-fg-on-brand ${tabColor}`}
          style={{ clipPath: "polygon(0 0, 100% 0, 100% 100%, 8% 100%, 0 65%)" }}
        >
          <span className="font-display truncate text-[0.8125rem] uppercase tracking-[0.06em]">
            {category.name}
          </span>
          {category.isFeatured ? (
            <span className="shrink-0 rounded-sm bg-black/15 px-1.5 py-0.5 text-[0.625rem] font-semibold uppercase tracking-wide">
              Popular
            </span>
          ) : null}
        </div>

        <div className="ruled-lines flex flex-1 flex-col gap-4 p-5 pt-6">
          <div className="flex items-start justify-between gap-3">
            <span
              aria-hidden
              className="flex h-12 w-12 shrink-0 items-center justify-center rounded-md border border-line bg-surface-2 text-2xl"
            >
              {category.iconUrl && !iconFailed ? (
                // eslint-disable-next-line @next/next/no-img-element -- admin-supplied external URL, unsuited to static optimization.
                <img
                  src={category.iconUrl}
                  alt=""
                  onError={() => setIconFailed(true)}
                  className="h-7 w-7 object-contain"
                />
              ) : (
                "🧰"
              )}
            </span>

            <span className="mt-0.5 inline-flex shrink-0 items-center gap-1 rounded-sm border border-line-strong bg-surface-2 px-2 py-0.5 text-[0.6875rem] font-semibold text-fg">
              <StarIcon />
              {rating}
            </span>
          </div>

          <p className="flex items-center gap-1.5 text-xs text-fg-subtle">
            <ShieldIcon />
            Vetted, background-checked professionals
          </p>

          <div className="mt-auto flex items-end justify-between gap-3 border-t border-line pt-3">
            <span className="nums font-mono text-xs text-fg-subtle">{bookings} booked</span>
            <span className="inline-flex shrink-0 items-center gap-1 rounded-sm border-2 border-brand-600 px-3 py-1 text-[0.6875rem] font-semibold uppercase tracking-wide text-brand-700 transition-colors duration-fast ease-out group-hover:bg-brand-50 dark:text-brand-300 dark:group-hover:bg-brand-500/15">
              Explore
              <svg
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                strokeWidth="2.5"
                strokeLinecap="round"
                strokeLinejoin="round"
                className="h-3 w-3 transition-transform duration-fast ease-out group-hover:translate-x-0.5"
                aria-hidden
              >
                <path d="M5 12h14M13 6l6 6-6 6" />
              </svg>
            </span>
          </div>
        </div>
      </motion.div>
    </Link>
  );
}

/** Stable per-category placeholder rating in 4.6-4.9 — decorative chrome, not fetched data (see file doc comment). */
function decorativeRating(id: string): string {
  const hash = hashString(id);
  return (4.6 + (hash % 4) / 10).toFixed(1);
}

/** Stable per-category placeholder booking count, formatted like "1.2k+" — decorative chrome, not fetched data. */
function decorativeBookingCount(id: string): string {
  const hash = hashString(id);
  const hundreds = 4 + (hash % 12); // 4-15 -> 400-1500
  return `${(hundreds / 10).toFixed(1)}k+`;
}

function hashString(value: string): number {
  let hash = 0;
  for (let i = 0; i < value.length; i++) {
    hash = (hash * 31 + value.charCodeAt(i)) >>> 0;
  }
  return hash;
}

function StarIcon() {
  return (
    <svg viewBox="0 0 24 24" fill="currentColor" className="h-3 w-3 text-accent-500" aria-hidden>
      <path d="M12 2.5l2.9 6.06 6.6.85-4.85 4.6 1.27 6.57L12 17.4l-5.92 3.18 1.27-6.57-4.85-4.6 6.6-.85L12 2.5Z" />
    </svg>
  );
}

function ShieldIcon() {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.75" strokeLinecap="round" strokeLinejoin="round" className="h-3.5 w-3.5 shrink-0" aria-hidden>
      <path d="M12 3l7 3v5c0 4.5-3 8-7 9-4-1-7-4.5-7-9V6l7-3Z" />
      <path d="m9 12 2 2 4-4" />
    </svg>
  );
}
