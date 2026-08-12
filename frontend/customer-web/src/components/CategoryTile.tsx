"use client";

import Link from "next/link";
import { motion } from "motion/react";
import { useState } from "react";
import { SPRING } from "@/components/motion";
import type { CategorySummary } from "@/lib/types";

/**
 * Category card for the home/listing tile grid (SRS 11.1.2, 11.5.1).
 *
 * "Quiet ground" direction: an editorial photography card, not an icon tile
 * or a stat-badge card — a large rounded photo with the category name as a
 * quiet caption underneath, in the spirit of Aesop's product-grid pages. No
 * decorative rating/booking-count chrome: Nestly has no per-category rating
 * data, and this direction's restraint means not inventing one just to fill
 * space (the previous "Resido" pass did fabricate a deterministic rating —
 * removed here rather than carried forward).
 */
export function CategoryTile({ category }: { category: CategorySummary }) {
  const [imageFailed, setImageFailed] = useState(false);
  const showImage = !!category.bannerUrl && !imageFailed;

  return (
    <Link href={`/categories/${category.slug}`} className="group block h-full">
      <motion.div
        whileHover={{ y: -6 }}
        whileTap={{ scale: 0.985 }}
        transition={SPRING}
        className="flex h-full flex-col gap-3"
      >
        <div className="relative aspect-[4/5] w-full overflow-hidden rounded-2xl bg-surface-3 shadow-xs transition-shadow duration-200 ease-out group-hover:shadow-lg">
          {showImage ? (
            // eslint-disable-next-line @next/next/no-img-element -- admin-supplied external URL, unsuited to static optimization.
            <img
              src={category.bannerUrl!}
              alt=""
              onError={() => setImageFailed(true)}
              className="h-full w-full object-cover transition-transform duration-slow ease-out group-hover:scale-[1.04]"
            />
          ) : (
            <div className="flex h-full w-full items-center justify-center text-fg-subtle" aria-hidden>
              {category.iconUrl ? (
                // eslint-disable-next-line @next/next/no-img-element -- admin-supplied external URL, unsuited to static optimization.
                <img src={category.iconUrl} alt="" className="h-14 w-14 object-contain opacity-60" />
              ) : (
                <span className="text-4xl opacity-60">🧰</span>
              )}
            </div>
          )}

          {category.isFeatured ? (
            <span className="absolute left-3 top-3 inline-flex items-center rounded-full bg-surface/90 px-2.5 py-1 text-[0.6875rem] font-medium text-fg shadow-sm backdrop-blur-sm">
              Popular
            </span>
          ) : null}
        </div>

        <div className="px-0.5">
          <p className="font-display text-base leading-snug text-fg">{category.name}</p>
          <p className="mt-0.5 text-xs text-fg-subtle">Vetted, background-checked professionals</p>
        </div>
      </motion.div>
    </Link>
  );
}
