"use client";

import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { SearchBar } from "@/components/SearchBar";
import { useSelectedCity } from "@/hooks/useSelectedCity";
import { API_V1, apiFetch } from "@/lib/api";
import type { CategorySummary } from "@/lib/types";

/**
 * Hero / primary CTA (SRS 11.1.2). Content is static for now: there is no
 * admin-configurable banner backend yet (no Banner/Promotion entity or API
 * anywhere in the catalog module) - SRS 11.1.3's "banner visibility, order,
 * and content shall be admin-configurable" is not implemented server-side,
 * so this deliberately does not fabricate one.
 *
 * "Quiet ground" direction: photography-led, not a gradient. The backdrop is
 * a real admin-supplied category photo (`bannerUrl`, the same field
 * `CategoryTile` already renders) — reusing `CategoryTiles`' own query key so
 * React Query serves it from cache rather than firing a second request. If no
 * category has a photo yet (a new city, before any admin uploads one), the
 * hero honestly falls back to a plain warm surface instead of inventing an
 * image or a decorative gradient — a real gap, not one hidden behind chrome.
 */
export function HeroBanner() {
  const photoUrl = useFirstCategoryPhoto();

  return (
    <section className="relative isolate overflow-hidden rounded-3xl shadow-xl">
      <div className="relative flex min-h-[26rem] flex-col justify-end sm:min-h-[30rem]">
        <HeroBackdrop photoUrl={photoUrl} />

        <div className="relative px-6 py-10 sm:px-12 sm:py-14">
          <p className="motion-safe:animate-rise mb-4 inline-flex items-center gap-2 rounded-full bg-surface/90 px-3.5 py-1.5 text-xs font-medium text-fg shadow-sm backdrop-blur-sm">
            <span className="h-1.5 w-1.5 rounded-full bg-brand-600" aria-hidden />
            Vetted professionals, upfront pricing
          </p>

          <h1
            style={{ animationDelay: "70ms" }}
            className={`motion-safe:animate-rise max-w-2xl font-display text-display-md text-balance ${
              photoUrl ? "text-white" : "text-fg"
            } sm:text-display-lg`}
          >
            Trusted home services, booked in minutes.
          </h1>

          <p
            style={{ animationDelay: "140ms" }}
            className={`motion-safe:animate-rise mt-4 max-w-xl text-[0.9375rem] leading-relaxed text-pretty ${
              photoUrl ? "text-white/85" : "text-fg-muted"
            }`}
          >
            Cleaning, repairs, salon, and more — background-checked professionals,
            prices you see before you book, and slots that fit your day.
          </p>

          <div style={{ animationDelay: "210ms" }} className="motion-safe:animate-rise mt-8 max-w-xl">
            <SearchBar variant="hero" />
          </div>
        </div>
      </div>
    </section>
  );
}

/** Full-bleed photo with a bottom scrim when a real image is available; a quiet warm panel otherwise. */
function HeroBackdrop({ photoUrl }: { photoUrl: string | null }) {
  const [failed, setFailed] = useState(false);
  const showPhoto = photoUrl && !failed;

  if (!showPhoto) {
    return <div aria-hidden className="absolute inset-0 -z-10 bg-surface-2" />;
  }

  return (
    <>
      {/* eslint-disable-next-line @next/next/no-img-element -- admin-supplied external URL, unsuited to static optimization. */}
      <img
        src={photoUrl}
        alt=""
        onError={() => setFailed(true)}
        className="absolute inset-0 -z-20 h-full w-full object-cover"
      />
      <div aria-hidden className="photo-scrim absolute inset-0 -z-10" />
    </>
  );
}

/** First real category photo for the customer's city, or null if none is set yet. */
function useFirstCategoryPhoto(): string | null {
  const { city } = useSelectedCity();
  const query = useQuery({
    queryKey: ["categories", city?.id],
    queryFn: () => apiFetch<CategorySummary[]>(`${API_V1}/categories?cityId=${city!.id}`),
    enabled: !!city,
  });

  const withPhoto = query.data?.find((category) => !!category.bannerUrl);
  return withPhoto?.bannerUrl ?? null;
}
