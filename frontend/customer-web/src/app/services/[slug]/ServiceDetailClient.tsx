"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useParams } from "next/navigation";
import { PageBanner } from "@/components/PageBanner";
import { PriceCalculator } from "@/components/PriceCalculator";
import { ReviewsSummary } from "@/components/ReviewsSummary";
import { ServiceAvailability } from "@/components/ServiceAvailability";
import { ServiceFaqs } from "@/components/ServiceFaqs";
import { STICKY_BAR_SPACER, StickyActionBar } from "@/components/patterns";
import { Alert, Button, LinkButton, Skeleton, cx } from "@/components/ui";
import { useSelectedCity } from "@/hooks/useSelectedCity";
import { useServiceability } from "@/hooks/useServiceability";
import { API_V1, apiFetch, describeError } from "@/lib/api";
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
      <main className="mx-auto w-full max-w-7xl px-4 py-8 sm:px-6 sm:py-12">
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
    <main className="flex w-full flex-col animate-rise">
      <PageBanner
        title={service.name}
        description={service.description}
        imageUrl={service.coverImageUrl}
        breadcrumb={<Breadcrumb categoryName={service.categoryName} categorySlug={service.categorySlug} serviceName={service.name} />}
      />

      <div className={cx("mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14", STICKY_BAR_SPACER)}>
        <div className="grid gap-8 md:grid-cols-[1fr_20rem]">
          <div className="flex min-w-0 flex-col gap-8">
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
                <h2
                  id="policies-heading"
                  className="mb-3 text-lg font-semibold tracking-tight text-fg"
                >
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
              quantityAllowed={service.isQuantityAllowed}
            />
            <ServiceAvailability serviceId={service.id} />

            {/* StickyActionBar: below `md`, `aside`'s own `md:sticky` doesn't
                apply (single-column grid), so without this "Book now" - the
                actual start of the booking funnel per task #344 - sat at the
                bottom of a page that can run description + two inclusion
                lists + policies + FAQs + reviews deep, exactly the
                "primary CTA requires scrolling to find" gap docs/FRONTEND.md's
                RESPONSIVE DESIGN policy calls out. `md:` collapses back to a
                plain inline block, unchanged from before. LinkButton, not
                <Link><Button/></Link>: nesting a button inside an anchor is
                invalid HTML and gives assistive tech two nested interactive
                elements for one action. */}
            <BookingCta service={service} />
          </aside>
        </div>
      </div>
    </main>
  );
}

/**
 * The booking CTA, in its own component so it can read serviceability: the
 * page's own early returns for loading/error sit above this point, so a hook
 * called there would break hook ordering.
 *
 * When the API has positively said the service is not offered at the chosen
 * locality, the funnel entry becomes a disabled button rather than a live
 * link - previously a full-width enabled "Book now" rendered directly under
 * the "Not available here" error and happily started a booking that could
 * never be fulfilled. Loading and error states leave the link enabled: only a
 * definite "no" blocks the customer.
 */
function BookingCta({ service }: { service: ServiceDetail }) {
  const { isUnserviceable } = useServiceability(service.id);

  return (
    <StickyActionBar>
      {isUnserviceable ? (
        <Button size="lg" fullWidth disabled>
          Not available in your area
        </Button>
      ) : (
        <LinkButton href={`/booking/summary?serviceSlug=${service.slug}`} size="lg" fullWidth>
          Book now
        </LinkButton>
      )}
    </StickyActionBar>
  );
}

/**
 * Breadcrumb for the full-bleed `PageBanner` (see `categories/[slug]/page.tsx`'s
 * own `Breadcrumb` for the pattern this mirrors) - white text over the
 * banner's scrim rather than the default `text-fg-muted` a plain-background
 * breadcrumb uses.
 */
function Breadcrumb({
  categoryName,
  categorySlug,
  serviceName,
}: {
  categoryName: string;
  categorySlug: string;
  serviceName: string;
}) {
  return (
    <nav aria-label="Breadcrumb" className="text-sm">
      <ol className="flex flex-wrap items-center gap-1.5 text-white/70">
        <li>
          <Link href="/" className="hover:text-white">
            Home
          </Link>
        </li>
        <li aria-hidden>/</li>
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
        <li className="truncate font-medium text-white" aria-current="page">
          {serviceName}
        </li>
      </ol>
    </nav>
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

  // Multi-point content (one point per line, UC-style) reads as a bulleted
  // list; a single sentence stays a plain paragraph so existing one-line
  // inclusions/exclusions elsewhere in the catalog are unaffected.
  const points = body
    .split("\n")
    .map((line) => line.trim())
    .filter(Boolean);

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
            className="h-4 w-4 text-fg-subtle"
            aria-hidden
          >
            <path d="M18 6 6 18M6 6l12 12" />
          </svg>
        )}
        {title}
      </h2>
      {points.length > 1 ? (
        <ul className="mt-2 flex flex-col gap-1.5 text-sm leading-relaxed text-fg-muted">
          {points.map((point, index) => (
            <li key={index} className="flex gap-2">
              <span aria-hidden className="mt-2 h-1 w-1 shrink-0 rounded-full bg-fg-subtle" />
              <span>{point}</span>
            </li>
          ))}
        </ul>
      ) : (
        <p className="mt-2 text-sm leading-relaxed text-fg-muted">{body}</p>
      )}
    </section>
  );
}

/** Mirrors the loaded page's frame (full-bleed banner + content) so nothing jumps when it resolves - same pattern as `CategoryDetailSkeleton`. */
function ServiceDetailSkeleton() {
  return (
    <main className="flex w-full flex-col">
      {/* Mirrors PageBanner's real height so the page doesn't jump when it resolves. */}
      <div className="listing-banner h-[13.5rem] w-full sm:h-[15.5rem]" aria-hidden />

      <div className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14">
        <div className="grid gap-8 md:grid-cols-[1fr_20rem]">
          <div className="flex flex-col gap-6">
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
