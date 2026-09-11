"use client";

import Link from "next/link";
import { AnimatePresence, motion, type Variants } from "motion/react";
import { useQuery } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { SearchBar } from "@/components/SearchBar";
import { cx } from "@/components/ui";
import { API_V1, apiFetch } from "@/lib/api";
import { CmsMediaType, type HomeBanner } from "@/lib/types";

/**
 * Hero / primary CTA (SRS 11.1.2). Content is fully admin-managed: the slides
 * come from `GET /api/v1/banners/home` (only Published banners within their
 * publish window, ordered by sort order) - there is no hardcoded slide copy
 * or imagery here. Admin edits in the CMS "Banners" screen surface on the next
 * load with no redeploy.
 *
 * The visual treatment is unchanged from the previous static version:
 * true edge-to-edge full-bleed (lives outside `app/page.tsx`'s max-w-7xl
 * wrapper), a soft radial tint behind the copy plus a text-shadow on the type
 * itself (the photo stays fully visible everywhere the text isn't), and
 * `object-[center_25%]` to keep each photo's subject in view on very wide
 * viewports. Each banner's title (and optional subtitle) replays a word-by-word
 * entrance keyed to the slide index so the copy reads as part of the slide
 * transition, in sync with its image. The trust badge and search bar are page
 * furniture, rendered once so they stay steady while the slide copy swaps.
 *
 * Auto-advance respects `prefers-reduced-motion`, pauses on hover/focus, and
 * only runs with more than one banner; the dots double as manual controls
 * (WCAG 2.2.2). One banner shows statically; zero banners collapse the section
 * to a slim branded search band rather than a broken empty state.
 */

const SLIDE_DURATION_MS = 6000;
// Three layers: a tight near-opaque ring that reads as a thin outline around
// each glyph (the part that actually keeps text legible against a bright
// photo region, where the scrim underneath is thin or absent), plus the
// original soft/wide layers for depth against darker backgrounds.
const TEXT_SHADOW = {
  textShadow:
    "0 1px 2px rgb(0 0 0 / 0.85), 0 1px 4px rgb(0 0 0 / 0.65), 0 4px 20px rgb(0 0 0 / 0.5)",
};

// Cancels `#main`'s top padding (reserved for the fixed `SiteHeader`) so the
// banner still starts at true y=0, flush under the header. Shared by every
// render state so the header overlap is handled identically whether the slider,
// the loading shell, or the empty band is showing.
const HEADER_OFFSET = "-mt-[calc(4.5rem+env(safe-area-inset-top))]";

export function HeroBanner() {
  const query = useQuery({
    queryKey: ["home-banners"],
    queryFn: () => apiFetch<HomeBanner[]>(`${API_V1}/banners/home`),
  });

  const banners = query.data ?? [];

  const [index, setIndex] = useState(0);
  const [paused, setPaused] = useState(false);

  // Guard the index against a shrinking list (e.g. a banner unpublished
  // between loads) so it never points past the end.
  useEffect(() => {
    if (index > banners.length - 1) setIndex(0);
  }, [banners.length, index]);

  useEffect(() => {
    if (paused || banners.length <= 1) return;
    if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) return;

    const id = setInterval(() => {
      setIndex((current) => (current + 1) % banners.length);
    }, SLIDE_DURATION_MS);
    return () => clearInterval(id);
  }, [paused, banners.length]);

  // Loading: reserve the full hero height with a branded placeholder so the
  // page below doesn't jump when the slides arrive. No fabricated copy.
  if (query.isPending) {
    return <HeroBand tall />;
  }

  // Empty or failed: collapse to a slim branded band that still carries the
  // search bar, rather than a tall empty image area or a broken state.
  if (banners.length === 0) {
    return <HeroBand />;
  }

  const active = banners[Math.min(index, banners.length - 1)];
  const multiple = banners.length > 1;

  return (
    <section
      className={cx("relative isolate w-full overflow-hidden", HEADER_OFFSET)}
      onMouseEnter={() => setPaused(true)}
      onMouseLeave={() => setPaused(false)}
      onFocus={() => setPaused(true)}
      onBlur={() => setPaused(false)}
    >
      <div className="relative min-h-[440px] w-full sm:min-h-[500px] md:min-h-[540px] lg:min-h-[580px]">
        <AnimatePresence initial={false}>
          <motion.div
            key={active.id}
            className="absolute inset-0"
            initial={{ opacity: 0, scale: 1.04 }}
            animate={{ opacity: 1, scale: 1 }}
            exit={{ opacity: 0 }}
            transition={{ opacity: { duration: 1, ease: "easeInOut" }, scale: { duration: 6, ease: "linear" } }}
          >
            <BannerImage banner={active} eager={index === 0} />
          </motion.div>
        </AnimatePresence>

        {multiple ? <NextSlidePreloader banner={banners[(index + 1) % banners.length]} /> : null}

        {/* Soft radial tint behind the copy only — the photo stays fully
            visible at the edges instead of sitting under a flat wash. */}
        <div
          aria-hidden
          className="absolute inset-0 bg-[radial-gradient(62%_58%_at_50%_45%,rgb(0_0_0/0.58),transparent_75%)]"
        />
        {/* Taller and darker than the radial tint alone reaches: the trust
            badge sits right at this band (`pt-24`), where the radial tint
            (centered lower, at 45% of the hero's height) is at its weakest -
            without this, that strip of text sat almost directly on the raw
            photo. */}
        <div aria-hidden className="absolute inset-x-0 top-0 h-40 bg-gradient-to-b from-black/55 via-black/20 to-transparent" />
        <div aria-hidden className="absolute inset-x-0 bottom-0 h-32 bg-gradient-to-t from-black/40 to-transparent" />

        <div className="relative z-10 mx-auto flex h-full w-full max-w-3xl flex-col items-center justify-start gap-6 px-5 pb-10 pt-24 text-center sm:px-8">
          <TrustBadge />

          {/* Keyed on index so the title/subtitle re-enter on every slide
              change, in sync with the crossfading image behind them. */}
          <AnimatePresence mode="wait">
            <motion.div
              key={active.id}
              className="flex flex-col items-center gap-6"
              initial="hidden"
              animate="show"
              exit={{ opacity: 0, y: -8, transition: { duration: 0.3 } }}
              variants={{ hidden: {}, show: { transition: { staggerChildren: 0.05 } } }}
            >
              <Headline text={active.title} linkUrl={active.linkUrl} />

              {active.subtitle ? (
                <motion.p
                  variants={fadeUp}
                  style={TEXT_SHADOW}
                  className="max-w-lg text-[0.9375rem] leading-relaxed text-white/90 text-pretty"
                >
                  {active.subtitle}
                </motion.p>
              ) : null}
            </motion.div>
          </AnimatePresence>

          <div className="w-full max-w-lg">
            <SearchBar variant="hero" />
          </div>
        </div>

        {multiple ? (
          <div className="absolute bottom-6 left-1/2 z-10 flex -translate-x-1/2 items-center gap-2 sm:bottom-8">
            {banners.map((banner, slideIndex) => (
              <button
                key={banner.id}
                type="button"
                onClick={() => setIndex(slideIndex)}
                aria-label={`Show slide ${slideIndex + 1} of ${banners.length}`}
                aria-current={slideIndex === index}
                className={cx(
                  "h-1.5 rounded-full transition-all duration-fast ease-out",
                  slideIndex === index ? "w-6 bg-white" : "w-1.5 bg-white/45 hover:bg-white/70",
                )}
              />
            ))}
          </div>
        ) : null}
      </div>
    </section>
  );
}

/** The slide's image or video, wrapped in a link when the banner has a destination. */
function BannerImage({ banner, eager }: { banner: HomeBanner; eager: boolean }) {
  const media =
    banner.mediaType === CmsMediaType.Video ? (
      // Hero video slides play like a moving photo, not a media player: no
      // controls, no sound, loops indefinitely. `muted` is required (not just
      // preferred) for autoplay to be allowed by browser policy at all.
      <video
        src={banner.imageUrl}
        aria-label={banner.imageAltText ?? banner.title}
        className="h-full w-full object-cover object-[center_25%]"
        autoPlay
        muted
        loop
        playsInline
        preload={eager ? "auto" : "metadata"}
      />
    ) : (
      // eslint-disable-next-line @next/next/no-img-element -- admin-supplied URL from the managed media library, unsuited to next/image's build-time domain allowlist (same reasoning as CategoryTile).
      <img
        src={banner.imageUrl}
        alt={banner.imageAltText ?? banner.title}
        loading={eager ? "eager" : "lazy"}
        decoding="async"
        className="h-full w-full object-cover object-[center_25%]"
      />
    );

  if (banner.linkUrl) {
    return (
      <Link href={banner.linkUrl} aria-label={banner.title} className="absolute inset-0 block">
        {media}
      </Link>
    );
  }

  return media;
}

/**
 * Fetches the upcoming slide's media into the browser cache before its turn
 * arrives, so the crossfade in {@link BannerImage} never has to wait on a
 * multi-megabyte video/image download mid-transition - the visible stutter
 * this was built to fix. Rendered off-screen (not `display: none`, which some
 * browsers treat as a signal to skip the fetch) rather than mounted as the
 * real slide, so it never autoplays, competes for layout, or gets announced
 * to assistive tech.
 */
function NextSlidePreloader({ banner }: { banner: HomeBanner }) {
  return (
    <div aria-hidden className="pointer-events-none absolute h-px w-px overflow-hidden opacity-0">
      {banner.mediaType === CmsMediaType.Video ? (
        <video src={banner.imageUrl} muted preload="auto" />
      ) : (
        // eslint-disable-next-line @next/next/no-img-element -- prefetch-only element, never displayed at content size.
        <img src={banner.imageUrl} alt="" loading="eager" decoding="async" />
      )}
    </div>
  );
}

function Headline({ text, linkUrl }: { text: string; linkUrl: string | null }) {
  const words = (
    <h1
      style={TEXT_SHADOW}
      className="max-w-2xl text-display-md font-semibold leading-[1.08] text-balance text-white sm:text-display-lg lg:text-display-xl"
    >
      {text.split(" ").map((word, i) => (
        // Margin, not a trailing space: a space as the last text node in an
        // inline-block collapses at the box edge and glues words together.
        <motion.span key={i} variants={fadeUp} className="inline-block mr-[0.28em] last:mr-0">
          {word}
        </motion.span>
      ))}
    </h1>
  );

  // The headline links to the banner destination when one is set, so keyboard
  // users get the same target the image click offers.
  return linkUrl ? (
    <Link href={linkUrl} className="rounded-sm outline-none focus-visible:ring-2 focus-visible:ring-white/70">
      {words}
    </Link>
  ) : (
    words
  );
}

function TrustBadge() {
  return (
    // White, not the brand accent gold: `accent-300` reads fine on the
    // radial-tinted center of the photo but fell below a reliable contrast
    // ratio in the lighter/busier regions a real photo can have near the
    // top, where this badge sits - white plus the text-shadow above is the
    // same treatment the headline already relies on for the same reason.
    <p
      style={TEXT_SHADOW}
      className="flex items-start justify-center gap-2 text-xs font-semibold uppercase tracking-[0.22em] text-white"
    >
      <ShieldIcon className="mt-0.5 h-4 w-4 shrink-0" />
      <span>Vetted professionals &middot; Upfront pricing</span>
    </p>
  );
}

/**
 * Branded fallback band used while banners load and when none are live. Keeps
 * the search bar (the hero's primary CTA) available and carries the same
 * header-offset and full-bleed treatment as the slider, so the home page never
 * shows a broken or empty banner area. `tall` matches the slider height during
 * loading to avoid a layout jump; the empty state is deliberately shorter.
 */
function HeroBand({ tall = false }: { tall?: boolean }) {
  return (
    <section className={cx("relative isolate w-full overflow-hidden bg-brand-gradient", HEADER_OFFSET)}>
      <div
        className={cx(
          "relative w-full",
          tall
            ? "min-h-[440px] sm:min-h-[500px] md:min-h-[540px] lg:min-h-[580px]"
            : "min-h-[300px] sm:min-h-[340px]",
        )}
      >
        <div aria-hidden className="absolute inset-x-0 top-0 h-24 bg-gradient-to-b from-black/20 to-transparent" />
        <div className="relative z-10 mx-auto flex h-full w-full max-w-3xl flex-col items-center justify-start gap-6 px-5 pb-10 pt-24 text-center sm:px-8">
          <TrustBadge />
          <div className="w-full max-w-lg">
            <SearchBar variant="hero" />
          </div>
        </div>
      </div>
    </section>
  );
}

// Explicit `Variants` typing (not inferred) so `ease: "easeOut"` is checked
// against motion's `Easing` union instead of widening to a plain `string`.
const fadeUp: Variants = {
  hidden: { opacity: 0, y: 14 },
  show: { opacity: 1, y: 0, transition: { duration: 0.5, ease: "easeOut" } },
};

function ShieldIcon({ className = "h-5 w-5" }: { className?: string }) {
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
      <path d="M12 3l7 3v5.5c0 4.2-3 8-7 9.5-4-1.5-7-5.3-7-9.5V6l7-3Z" />
      <path d="m9 12 2 2 4-4" />
    </svg>
  );
}
