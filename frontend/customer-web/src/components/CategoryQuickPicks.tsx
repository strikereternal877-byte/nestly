"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { motion } from "motion/react";
import { useState } from "react";
import { cx } from "@/components/ui";
import { Reveal, revealItem } from "@/components/motion";
import { useSelectedCity } from "@/hooks/useSelectedCity";
import { API_V1, apiFetch } from "@/lib/api";
import type { CategorySummary } from "@/lib/types";

/**
 * "Home Services, Right at Your Doorstep": every registered category
 * serviceable in the customer's selected city/locality, as a compact strip of
 * icon+name chips. Same data source as the categories listing
 * (`GET /categories?cityId=`), just rendered as small icons instead of full
 * cards - this is a quick-jump rail, not a second category grid.
 *
 * Two variants, both rendered by `page.tsx` (one hidden per breakpoint via
 * CSS, not JS, so there is only ever one network request):
 *
 * - `"overlay"` (`sm` and up): a frosted glass panel over the hero's
 *   bottom-left corner. Full names, unpredictable-length real catalog data,
 *   made this genuinely tall on some cities - confirmed by measuring real
 *   coordinates (not guessing) that on a full-height desktop hero it clears
 *   the centered headline/search bar comfortably from the bottom anchor.
 * - `"inline"` (below `sm`): a plain in-page card directly under the hero,
 *   not an overlay at all. The same real-coordinate check found that a short
 *   mobile hero simply does not have enough clearance between the search bar
 *   and the hero's bottom edge for ANY reasonably-sized overlay widget there
 *   - rendering it in normal document flow instead is what actually
 *   guarantees zero overlap on mobile, rather than continuing to shrink
 *   numbers to fit a budget that measured out at ~46px.
 *
 * Silent on every "nothing to show" case (no city yet, no categories, load
 * error) in both variants: a skeleton/empty-state block would either compete
 * with the hero (overlay) or leave an odd empty card right under it (inline).
 * The full grid with proper empty/error states still lives in the
 * "New & Trending" section and the /categories page.
 */
export function CategoryQuickPicks({ variant }: { variant: "overlay" | "inline" }) {
  const { city, locality } = useSelectedCity();

  if (!city) {
    return null;
  }

  return <QuickPicksRow cityId={city.id} pincodeId={locality?.pincodeId} variant={variant} />;
}

function QuickPicksRow({
  cityId,
  pincodeId,
  variant,
}: {
  cityId: string;
  pincodeId?: string;
  variant: "overlay" | "inline";
}) {
  const query = useQuery({
    queryKey: ["categories", cityId, pincodeId],
    queryFn: () =>
      apiFetch<CategorySummary[]>(
        `${API_V1}/categories?cityId=${cityId}${pincodeId ? `&pincodeId=${pincodeId}` : ""}`,
      ),
    // Home page (CategoryTiles) may already have this cached under the same
    // key - a shared cache entry means this rail never double-fetches.
    staleTime: 60 * 1000,
  });

  if (!query.data || query.data.length === 0) {
    return null;
  }

  const isOverlay = variant === "overlay";

  return (
    <div
      className={cx(
        "w-[300px] max-w-full rounded-2xl px-3.5 py-3",
        // Overlay: a frosted glass panel over the photo - the same
        // translucent-card-over-imagery language enterprise product sites use
        // for hero widgets. Inline: a plain on-brand card matching every
        // other section's own surface, since there is no photo behind it to
        // read against.
        isOverlay
          ? "border border-white/15 bg-white/10 shadow-lg backdrop-blur-md"
          : "w-full border border-line bg-brand-50 shadow-xs dark:bg-brand-500/[0.06]",
      )}
    >
      <p
        className={cx(
          "mb-2.5 text-[0.6875rem] font-semibold uppercase tracking-wide",
          isOverlay ? "text-white/90" : "text-fg-muted",
        )}
      >
        Home Services, Right at Your Doorstep
      </p>
      <Reveal className="flex flex-wrap gap-x-3 gap-y-2">
        {query.data.slice(0, 8).map((category) => (
          <motion.div key={category.id} variants={revealItem}>
            <QuickPickIcon category={category} isOverlay={isOverlay} />
          </motion.div>
        ))}
      </Reveal>
    </div>
  );
}

/**
 * Icon beside its name, both sitting directly on the surface behind them - no
 * white chip behind the icon (an admin-uploaded icon is expected to be its
 * own transparent-background art; the fallback letter needs no backing shape
 * either, since the surface itself already supplies the contrast). Icon-
 * beside-label rather than icon-above-label is what makes a full,
 * untruncated name possible at this size: the text has the item's whole
 * remaining width to use instead of being squeezed under a 30px box.
 */
function QuickPickIcon({ category, isOverlay }: { category: CategorySummary; isOverlay: boolean }) {
  const [imageFailed, setImageFailed] = useState(false);
  const showImage = !!category.iconUrl && !imageFailed;

  return (
    <Link href={`/categories/${category.slug}`} className="group inline-flex items-center gap-1.5">
      <motion.span
        whileHover={{ scale: 1.08 }}
        whileTap={{ scale: 0.95 }}
        className="flex h-[30px] w-[30px] shrink-0 items-center justify-center"
      >
        {showImage ? (
          // eslint-disable-next-line @next/next/no-img-element -- admin-supplied external URL, unsuited to static optimization.
          <img
            src={category.iconUrl!}
            alt=""
            loading="lazy"
            decoding="async"
            onError={() => setImageFailed(true)}
            className={cx("h-full w-full object-contain", isOverlay && "drop-shadow-sm")}
          />
        ) : (
          // A category with no admin-uploaded icon (or one whose URL 404s)
          // previously fell back to a bare capital letter - indistinguishable
          // from a broken-image glyph and easy to mistake for a rendering bug
          // (docs/OPEN-FIXES-FEATURES.csv "Category grid, Second category
          // tile renders a bare letter instead of an icon"). A generic
          // category glyph reads as "icon not yet uploaded" instead, and
          // never depends on the category name existing/being non-empty.
          <FallbackCategoryIcon
            className={cx("h-[18px] w-[18px] drop-shadow-sm", isOverlay ? "text-white" : "text-brand-600")}
          />
        )}
      </motion.span>
      <span
        className={cx(
          "whitespace-nowrap text-xs font-medium transition-colors duration-fast ease-out",
          isOverlay ? "text-white drop-shadow-sm group-hover:text-white/80" : "text-fg group-hover:text-brand-600",
        )}
      >
        {category.name}
      </span>
    </Link>
  );
}

/** Generic "category" glyph used when a category has no icon art yet, or its `iconUrl` fails to load. */
function FallbackCategoryIcon({ className }: { className?: string }) {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.75"
      strokeLinecap="round"
      strokeLinejoin="round"
      className={className}
      aria-hidden
    >
      <rect x="3.5" y="3.5" width="7" height="7" rx="1.5" />
      <rect x="13.5" y="3.5" width="7" height="7" rx="1.5" />
      <rect x="3.5" y="13.5" width="7" height="7" rx="1.5" />
      <rect x="13.5" y="13.5" width="7" height="7" rx="1.5" />
    </svg>
  );
}
