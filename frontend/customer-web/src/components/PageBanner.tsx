"use client";

import Image from "next/image";
import type { ReactNode } from "react";
import { cx } from "@/components/ui";

/**
 * Full-bleed page banner shared by the category detail page, the categories
 * listing page, and the checkout page (SRS 11.5.2/11.5.1 + booking summary).
 * When `imageUrl` is set (a category's `pageBannerUrl`, uploaded through the
 * admin panel), that photo fills the band with a dark scrim behind the text.
 * With no image, it falls back to the original visual-only treatment
 * (matching the Resido reference site's "classical-property" listing page: a
 * solid brand-600 band with scattered translucent circles) — the same
 * `.listing-banner`/`.banner-blob` primitives every full-bleed banner in the
 * product already used before this component existed.
 *
 * Deliberately a *different* field from `Category.bannerUrl` (the card photo
 * on the home/listing tiles) — admins upload distinct art for the two
 * contexts rather than the same photo stretched into both.
 */
export function PageBanner({
  title,
  description,
  imageUrl,
  breadcrumb,
  badge,
  size = "default",
}: {
  title: string;
  description?: string | null;
  imageUrl?: string | null;
  breadcrumb?: ReactNode;
  badge?: ReactNode;
  /** `compact` is for embedding inside an already-padded page (checkout) rather than sitting flush under the header. */
  size?: "default" | "compact";
}) {
  return (
    <section
      className={cx(
        "relative isolate overflow-hidden px-4 sm:px-6",
        size === "compact" ? "rounded-2xl py-8 sm:py-10" : "py-12 sm:py-16",
        !imageUrl && "listing-banner",
      )}
    >
      {imageUrl ? (
        <>
          {/* next/image, right-sized to the viewport instead of the full
              original upload - every pageBannerUrl in production is
              same-origin (audited 2026-09-09). `priority` (not lazy) because
              this is the single largest above-the-fold element on every page
              that has one - the usual LCP candidate. */}
          <Image src={imageUrl} alt="" aria-hidden fill priority sizes="100vw" className="object-cover" />
          <div aria-hidden className="absolute inset-0 bg-gradient-to-t from-black/70 via-black/40 to-black/20" />
        </>
      ) : (
        <div aria-hidden className="pointer-events-none absolute inset-0 overflow-hidden">
          <span className="banner-blob absolute -left-10 -top-16 h-56 w-56" />
          <span className="banner-blob absolute -right-16 top-6 h-72 w-72" />
          <span className="banner-blob absolute bottom-[-4.5rem] left-1/3 h-48 w-48" />
        </div>
      )}

      <div className="relative mx-auto flex w-full max-w-7xl flex-col items-start gap-4">
        {breadcrumb}
        <h1
          className={cx(
            "font-semibold text-white",
            size === "compact" ? "text-xl sm:text-display-sm" : "text-display-sm sm:text-display-md",
          )}
        >
          {title}
        </h1>
        {description ? (
          <p className="max-w-2xl text-[0.9375rem] leading-relaxed text-white/85 text-pretty">{description}</p>
        ) : null}
        {badge}
      </div>
    </section>
  );
}
