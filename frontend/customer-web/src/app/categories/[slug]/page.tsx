"use client";

import { useQuery } from "@tanstack/react-query";
import { motion } from "motion/react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useState } from "react";
import { ServiceCard } from "@/components/ServiceCard";
import { ServiceGroupSection } from "@/components/ServiceGroupSection";
import { SubcategoryChips } from "@/components/SubcategoryChips";
import { Reveal, revealItem } from "@/components/motion";
import { Alert, Button, EmptyState, Skeleton } from "@/components/ui";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import type { CategoryDetail } from "@/lib/types";

/** Category detail page (SRS 11.5.2): banner, description, and its service listing. */
export default function CategoryDetailPage() {
  const { slug } = useParams<{ slug: string }>();

  const query = useQuery({
    queryKey: ["category", slug],
    queryFn: () => apiFetch<CategoryDetail>(`${API_V1}/categories/${slug}`),
  });

  if (query.isPending) {
    return <CategoryDetailSkeleton />;
  }

  if (query.isError) {
    return (
      <main className="mx-auto w-full max-w-7xl px-4 py-8 sm:px-6 sm:py-12">
        <Alert
          tone="error"
          title="Couldn't load this category"
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

  const category = query.data;

  // Appliance/Service Group catalog redesign: total count spans both the
  // grouped sections and the ungrouped grid, so a category whose services
  // are entirely grouped (e.g. "AC") doesn't wrongly show the empty state.
  const totalServiceCount =
    category.serviceGroups.reduce((count, group) => count + group.services.length, 0) + category.services.length;

  return (
    <main className="flex w-full flex-col">
      <ListingBanner
        title={category.name}
        description={category.description}
        serviceCount={totalServiceCount}
        bannerUrl={category.bannerUrl}
        breadcrumb={(light) => <Breadcrumb categoryName={category.name} light={light} />}
      />

      <div className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14">
        {category.subcategories.length > 0 ? (
          <section aria-labelledby="subcategories-heading" className="mb-10">
            <h2 id="subcategories-heading" className="mb-3 font-display text-lg text-fg">
              Browse by type
            </h2>
            <SubcategoryChips subcategories={category.subcategories} />
          </section>
        ) : null}

        <section aria-labelledby="services-heading">
          <h2 id="services-heading" className="mb-5 font-display text-lg text-fg">
            Services
            <span className="ml-2 text-sm font-normal text-fg-subtle">{totalServiceCount}</span>
          </h2>

          {totalServiceCount === 0 ? (
            <EmptyState
              title="Nothing listed yet"
              description="No services are listed under this category in your city yet — check back soon."
              action={
                <Link
                  href="/categories"
                  className="inline-flex h-10 items-center rounded-lg border border-line bg-surface px-4 text-sm font-medium text-fg shadow-xs transition duration-fast ease-out hover:border-line-strong hover:bg-surface-2"
                >
                  Browse other categories
                </Link>
              }
            />
          ) : (
            <div className="flex flex-col gap-8">
              {/* Section headers only for groups that exist (SRS 11.5.5) - a
                  category with none renders exactly the flat grid below, same
                  as every category before service groups existed. */}
              {category.serviceGroups.map((group) => (
                <ServiceGroupSection key={group.id} group={group} />
              ))}

              {category.services.length > 0 ? (
                <Reveal className="grid grid-cols-1 gap-5 sm:grid-cols-2 sm:gap-6 lg:grid-cols-3">
                  {category.services.map((service) => (
                    <motion.div key={service.id} variants={revealItem}>
                      <ServiceCard
                        slug={service.slug}
                        name={service.name}
                        description={service.description}
                        price={service.price}
                        durationMinutes={service.durationMinutes}
                        coverImageUrl={service.coverImageUrl}
                        addOnCount={service.addOns.length}
                      />
                    </motion.div>
                  ))}
                </Reveal>
              ) : null}
            </div>
          )}
        </section>
      </div>
    </main>
  );
}

function Breadcrumb({ categoryName, light }: { categoryName: string; light: boolean }) {
  const base = light ? "text-white/75" : "text-fg-subtle";
  const hover = light ? "hover:text-white" : "hover:text-fg";
  const current = light ? "text-white" : "text-fg";

  return (
    <nav aria-label="Breadcrumb" className="text-sm">
      <ol className={`flex items-center gap-1.5 ${base}`}>
        <li>
          <Link href="/" className={hover}>
            Home
          </Link>
        </li>
        <li aria-hidden>/</li>
        <li>
          <Link href="/categories" className={hover}>
            Categories
          </Link>
        </li>
        <li aria-hidden>/</li>
        <li className={`truncate font-medium ${current}`} aria-current="page">
          {categoryName}
        </li>
      </ol>
    </nav>
  );
}

/**
 * Full-bleed listing banner. "Quiet ground" direction: a real category
 * photo (the same `bannerUrl` field `CategoryTile` renders) with a bottom
 * scrim for legible white text, in place of the previous flat brand-600
 * band with decorative circles. When a category has no photo yet, this
 * falls back to a plain warm stone panel with dark text rather than
 * inventing an image. Breaks out of the page's max-w wrapper on purpose —
 * `ListingBanner` owns its own full-width background and re-applies the
 * max-w constraint only to its inner content. The service count is real
 * (the same total the page body computes), not fabricated chrome.
 */
function ListingBanner({
  title,
  description,
  serviceCount,
  bannerUrl,
  breadcrumb,
}: {
  title: string;
  description: string;
  serviceCount: number;
  bannerUrl: string | null;
  breadcrumb: (light: boolean) => React.ReactNode;
}) {
  const [imageFailed, setImageFailed] = useState(false);
  const showPhoto = !!bannerUrl && !imageFailed;

  return (
    <section className="relative isolate overflow-hidden px-4 py-12 sm:px-6 sm:py-16">
      {showPhoto ? (
        <>
          {/* eslint-disable-next-line @next/next/no-img-element -- admin-supplied external URL, unsuited to static optimization. */}
          <img
            src={bannerUrl!}
            alt=""
            onError={() => setImageFailed(true)}
            className="absolute inset-0 -z-20 h-full w-full object-cover"
          />
          <div aria-hidden className="photo-scrim absolute inset-0 -z-10" />
        </>
      ) : (
        <div aria-hidden className="absolute inset-0 -z-10 bg-surface-2" />
      )}

      <div className="relative mx-auto flex w-full max-w-7xl flex-col items-start gap-4">
        {breadcrumb(showPhoto)}
        <h1 className={`font-display text-display-sm sm:text-display-md ${showPhoto ? "text-white" : "text-fg"}`}>
          {title}
        </h1>
        {description ? (
          <p
            className={`max-w-2xl text-[0.9375rem] leading-relaxed text-pretty ${
              showPhoto ? "text-white/85" : "text-fg-muted"
            }`}
          >
            {description}
          </p>
        ) : null}
        <span
          className={`mt-1 inline-flex items-center gap-1.5 rounded-full px-3.5 py-1.5 text-xs font-semibold backdrop-blur-sm ${
            showPhoto ? "bg-white/15 text-white" : "bg-surface text-fg shadow-xs"
          }`}
        >
          {serviceCount} {serviceCount === 1 ? "service" : "services"} available
        </span>
      </div>
    </section>
  );
}

/** Mirrors the loaded page's frame so the heading and grid don't jump into place. */
function CategoryDetailSkeleton() {
  return (
    <main className="flex w-full flex-col">
      {/* Mirrors ListingBanner's real height so the page doesn't jump when it resolves. */}
      <div className="h-[13.5rem] w-full bg-surface-2 sm:h-[15.5rem]" aria-hidden />

      <div className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14">
        <Skeleton className="h-6 w-28" />
        <div className="mt-5 grid grid-cols-1 gap-5 sm:grid-cols-2 sm:gap-6 lg:grid-cols-3">
          {Array.from({ length: 6 }, (_, index) => (
            <Skeleton key={index} className="h-72 rounded-2xl" />
          ))}
        </div>
      </div>
    </main>
  );
}
