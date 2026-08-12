"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useState } from "react";
import { PriceCalculator } from "@/components/PriceCalculator";
import { ReviewsSummary } from "@/components/ReviewsSummary";
import { ServiceAvailability } from "@/components/ServiceAvailability";
import { ServiceFaqs } from "@/components/ServiceFaqs";
import { Alert, Button, Skeleton } from "@/components/ui";
import { useSelectedCity } from "@/hooks/useSelectedCity";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import { getServiceVisual } from "@/lib/serviceVisuals";
import type { ServiceDetail } from "@/lib/types";

/**
 * Service detail page (SRS 11.6.1): inclusions, exclusions, add-ons, pricing,
 * FAQs, cancellation/reschedule policy, and a reviews/rating summary.
 */
export default function ServiceDetailPage() {
  const { slug } = useParams<{ slug: string }>();
  const { city } = useSelectedCity();

  const query = useQuery({
    queryKey: ["service", slug],
    queryFn: () => apiFetch<ServiceDetail>(`${API_V1}/services/${slug}`),
  });

  if (query.isPending) {
    return <ServiceDetailSkeleton />;
  }

  if (query.isError) {
    return (
      <main className="mx-auto w-full max-w-5xl px-4 py-8 sm:px-6 sm:py-12">
        <Alert
          tone="error"
          title="Couldn't load this service"
          action={
            <Button size="sm" variant="secondary" onClick={() => query.refetch()}>
              Retry
            </Button>
          }
        >
          {describeError(query.error)}
        </Alert>
      </main>
    );
  }

  const service = query.data;

  return (
    <main className="flex w-full animate-rise flex-col">
      {/* Full-bleed at the top, ahead of price/booking info (this direction's
          "photography-led" requirement) — breaks out of the max-w wrapper on
          purpose, like ListingBanner on the category page. */}
      <ServiceHero
        name={service.name}
        coverImageUrl={service.coverImageUrl}
        categoryName={service.categoryName}
        categorySlug={service.categorySlug}
      />

      <div className="mx-auto w-full max-w-5xl px-4 py-8 sm:px-6 sm:py-12">
        <div className="grid gap-8 md:grid-cols-[1fr_20rem]">
          <div className="flex min-w-0 flex-col gap-8">
            <div>
              <h1 className="font-display text-display-sm text-fg">{service.name}</h1>
              <p className="mt-3 leading-relaxed text-fg-muted text-pretty">{service.description}</p>
            </div>

            <div className="grid gap-4 sm:grid-cols-2">
              <InclusionList
                headingId="inclusions-heading"
                title="What's included"
                body={service.inclusions}
                tone="included"
              />
              <InclusionList
                headingId="exclusions-heading"
                title="What's not included"
                body={service.exclusions}
                tone="excluded"
              />
            </div>

            {service.cancellationPolicy || service.reschedulePolicy ? (
              <section aria-labelledby="policies-heading">
                <h2 id="policies-heading" className="mb-3 font-display text-lg text-fg">
                  Cancellation &amp; rescheduling
                </h2>
                <ul className="flex flex-col gap-2 rounded-2xl border border-line bg-surface p-4 text-sm leading-relaxed text-fg-muted">
                  {service.cancellationPolicy ? <li>{service.cancellationPolicy}</li> : null}
                  {service.reschedulePolicy ? <li>{service.reschedulePolicy}</li> : null}
                </ul>
              </section>
            ) : null}

            <ServiceFaqs faqs={service.faqs} />

            <ReviewsSummary slug={service.slug} />
          </div>

          <aside className="flex flex-col gap-4 md:sticky md:top-20 md:self-start">
            <PriceCalculator
              serviceId={service.id}
              addOns={service.addOns}
              cityId={city ? city.id : null}
              variants={service.variants}
              addOnGroups={service.addOnGroups}
            />
            <ServiceAvailability serviceId={service.id} />

            {/* A styled Link, not <Link><Button/></Link>: nesting a button
                inside an anchor is invalid HTML and gives assistive tech two
                nested interactive elements for one action. */}
            <Link
              href={`/booking/summary?serviceSlug=${service.slug}`}
              className="inline-flex h-12 w-full items-center justify-center rounded-lg bg-brand-600 text-[0.9375rem] font-medium text-fg-on-brand shadow-brand transition duration-fast ease-out hover:bg-brand-700 active:scale-[0.98]"
            >
              Book now
            </Link>
          </aside>
        </div>
      </div>
    </main>
  );
}

/**
 * Full-bleed photo hero, in front of everything else on the page — the
 * "quiet ground" direction's photography-led requirement for this page.
 * Admin-supplied photo when present (see ServiceCard for the same pattern);
 * otherwise the icon-on-gradient fallback from src/lib/serviceVisuals.tsx,
 * since a broken/missing photo is a real gap, not something to fake. The
 * breadcrumb and title sit over the image on a bottom scrim, matching the
 * category page's `ListingBanner`.
 */
function ServiceHero({
  name,
  coverImageUrl,
  categoryName,
  categorySlug,
}: {
  name: string;
  coverImageUrl?: string | null;
  categoryName: string;
  categorySlug: string;
}) {
  const { icon: Icon, gradient } = getServiceVisual(name);
  const [imageFailed, setImageFailed] = useState(false);
  const showImage = coverImageUrl && !imageFailed;

  return (
    <div className="relative flex h-72 flex-col justify-end overflow-hidden sm:h-96">
      {showImage ? (
        <>
          {/* eslint-disable-next-line @next/next/no-img-element -- admin-supplied external URL, unsuited to static optimization. */}
          <img
            src={coverImageUrl}
            alt=""
            onError={() => setImageFailed(true)}
            className="absolute inset-0 -z-20 h-full w-full object-cover"
          />
          <div aria-hidden className="photo-scrim absolute inset-0 -z-10" />
        </>
      ) : (
        <div
          aria-hidden
          className={`absolute inset-0 -z-10 flex items-center justify-center bg-gradient-to-br ${gradient} text-white/90`}
        >
          <span className="scale-[2]">
            <Icon />
          </span>
        </div>
      )}

      <nav aria-label="Breadcrumb" className="relative px-4 pb-6 text-sm sm:px-6 sm:pb-8">
        <ol className="flex flex-wrap items-center gap-1.5 text-white/75">
          <li>
            <Link href="/categories" className="hover:text-white">
              Categories
            </Link>
          </li>
          <li aria-hidden>/</li>
          <li>
            <Link href={`/categories/${categorySlug}`} className="hover:text-white">
              {categoryName}
            </Link>
          </li>
          <li aria-hidden>/</li>
          <li className="font-medium text-white" aria-current="page">
            {name}
          </li>
        </ol>
      </nav>
    </div>
  );
}

function InclusionList({
  headingId,
  title,
  body,
  tone,
}: {
  headingId: string;
  title: string;
  body: string;
  tone: "included" | "excluded";
}) {
  if (!body) return null;

  return (
    <section aria-labelledby={headingId} className="rounded-2xl border border-line bg-surface p-4">
      <h2 id={headingId} className="flex items-center gap-2 text-sm font-semibold text-fg">
        {tone === "included" ? (
          <svg
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2.25"
            strokeLinecap="round"
            strokeLinejoin="round"
            className="h-4 w-4 text-success"
            aria-hidden
          >
            <path d="m5 13 4 4L19 7" />
          </svg>
        ) : (
          <svg
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2.25"
            strokeLinecap="round"
            className="h-4 w-4 text-fg-subtle"
            aria-hidden
          >
            <path d="M18 6 6 18M6 6l12 12" />
          </svg>
        )}
        {title}
      </h2>
      <p className="mt-2 text-sm leading-relaxed text-fg-muted">{body}</p>
    </section>
  );
}

/** Mirrors the loaded layout (full-bleed hero + two columns) so nothing jumps into place. */
function ServiceDetailSkeleton() {
  return (
    <main className="flex w-full flex-col">
      <Skeleton className="h-72 w-full !rounded-none sm:h-96" />
      <div className="mx-auto w-full max-w-5xl px-4 py-8 sm:px-6 sm:py-12">
        <div className="grid gap-8 md:grid-cols-[1fr_20rem]">
          <div className="flex flex-col gap-6">
            <Skeleton className="h-9 w-3/4" />
            <div className="flex flex-col gap-2">
              <Skeleton className="h-4 w-full" />
              <Skeleton className="h-4 w-5/6" />
            </div>
            <div className="grid gap-4 sm:grid-cols-2">
              <Skeleton className="h-28 rounded-2xl" />
              <Skeleton className="h-28 rounded-2xl" />
            </div>
            <Skeleton className="h-40 rounded-2xl" />
          </div>
          <div className="flex flex-col gap-4">
            <Skeleton className="h-56 rounded-2xl" />
            <Skeleton className="h-40 rounded-2xl" />
            <Skeleton className="h-12 rounded-lg" />
          </div>
        </div>
      </div>
    </main>
  );
}
