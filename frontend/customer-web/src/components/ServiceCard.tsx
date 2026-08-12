"use client";

import Link from "next/link";
import { motion } from "motion/react";
import { useState } from "react";
import { SPRING } from "@/components/motion";
import { getServiceVisual } from "@/lib/serviceVisuals";

/**
 * Service/package card for a listing (SRS 11.5.3): photo, name, duration,
 * starting price, view-detail CTA. Image-forward, matching the
 * photo-driven card pattern used by home-services marketplace apps -
 * `coverImageUrl` is null until an admin sets one (Phase 3 catalog
 * redesign follow-up), in which case a graphic fallback panel renders
 * instead of a broken image. The same fallback also covers a real photo
 * that fails to load (a dead URL, a network hiccup) - `onError` flips to it
 * rather than leaving a browser's broken-image icon in the card.
 */
export function ServiceCard({
  slug,
  name,
  description,
  price,
  durationMinutes,
  coverImageUrl,
  addOnCount,
}: {
  slug: string;
  name: string;
  description: string;
  price: number;
  durationMinutes?: number;
  coverImageUrl?: string | null;
  addOnCount?: number;
}) {
  const { icon: Icon, gradient } = getServiceVisual(name);
  const [imageFailed, setImageFailed] = useState(false);
  const showImage = coverImageUrl && !imageFailed;

  return (
    <Link href={`/services/${slug}`} className="group block h-full">
      <motion.div
        whileHover={{ y: -5 }}
        whileTap={{ scale: 0.98 }}
        transition={SPRING}
        className="flex h-full flex-col overflow-hidden rounded-lg border-2 border-line bg-surface shadow-xs transition-shadow duration-200 ease-out group-hover:border-brand-600/50 group-hover:shadow-md"
      >
        <div className="relative aspect-[4/3] w-full shrink-0 overflow-hidden bg-brand-gradient">
          {showImage ? (
            // eslint-disable-next-line @next/next/no-img-element -- admin-supplied external URL, unsuited to static optimization.
            <img
              src={coverImageUrl}
              alt=""
              onError={() => setImageFailed(true)}
              className="h-full w-full object-cover transition-transform duration-slow ease-out group-hover:scale-[1.04]"
            />
          ) : (
            <div
              aria-hidden
              className={`flex h-full w-full items-center justify-center bg-gradient-to-br ${gradient} text-white/90`}
            >
              <Icon />
            </div>
          )}
        </div>

        <div className="flex flex-1 flex-col p-5">
          <p className="font-medium leading-snug text-fg">{name}</p>

          <p className="mt-1 flex items-center gap-1.5 text-xs text-fg-subtle">
            {typeof durationMinutes === "number" ? (
              <>
                <span>{durationMinutes} mins</span>
                <span aria-hidden>·</span>
              </>
            ) : null}
            <span>
              Starts at <span className="nums font-mono font-medium text-fg">₹{price}</span>
            </span>
          </p>

          {/* Clamped so a long admin-authored description can't make one card in a
              grid twice the height of its neighbours. */}
          <p className="mt-2 line-clamp-2 text-sm leading-relaxed text-fg-muted">{description}</p>

          <div className="mt-4 flex flex-1 items-end justify-between gap-3 border-t border-line pt-3">
            {addOnCount ? (
              <span className="text-xs text-fg-subtle">
                {addOnCount} add-on{addOnCount === 1 ? "" : "s"} available
              </span>
            ) : (
              <span />
            )}
            <span className="inline-flex shrink-0 items-center gap-1 rounded-full bg-brand-600 px-3.5 py-1.5 text-xs font-semibold text-fg-on-brand shadow-brand transition-colors duration-fast ease-out group-hover:bg-brand-700">
              Explore details
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
