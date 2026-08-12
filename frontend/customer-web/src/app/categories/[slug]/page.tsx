"use client";

import { useQuery } from "@tanstack/react-query";
import { motion } from "motion/react";
import Link from "next/link";
import { useParams } from "next/navigation";
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
        breadcrumb={<Breadcrumb categoryName={category.name} />}
      />

      <div className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14">
        {category.subcategories.length > 0 ? (
          <section aria-labelledby="subcategories-heading" className="mb-10">
            <h2 id="subcategories-heading" className="font-display mb-3 text-base text-fg">
              Browse by type
            </h2>
            <SubcategoryChips subcategories={category.subcategories} />
          </section>
        ) : null}

        <section aria-labelledby="services-heading">
          <h2 id="services-heading" className="font-display mb-5 text-base text-fg">
            Services
            <span className="nums ml-2 font-mono text-sm font-normal text-fg-subtle">{totalServiceCount}</span>
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
                <Reveal className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
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

function Breadcrumb({ categoryName }: { categoryName: string }) {
  return (
    <nav aria-label="Breadcrumb" className="text-sm">
      <ol className="flex items-center gap-1.5 text-white/70">
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
        <li className="truncate font-medium text-white" aria-current="page">
          {categoryName}
        </li>
      </ol>
    </nav>
  );
}

/**
 * Full-bleed listing banner (visual-only, matching the Resido reference
 * site's "classical-property" listing page: a solid brand-600 band with
 * scattered translucent circles behind a centered title). Breaks out of the
 * page's max-w wrapper on purpose — `ListingBanner` owns its own full-width
 * background and re-applies the max-w constraint only to its inner content.
 * The service count is real (the same total the page body computes), not
 * fabricated chrome.
 */
function ListingBanner({
  title,
  description,
  serviceCount,
  breadcrumb,
}: {
  title: string;
  description: string;
  serviceCount: number;
  breadcrumb: React.ReactNode;
}) {
  return (
    <section className="listing-banner relative isolate overflow-hidden px-4 py-12 sm:px-6 sm:py-16">
      <div aria-hidden className="pointer-events-none absolute inset-0 overflow-hidden">
        <span className="banner-blob absolute -left-10 -top-16 h-56 w-56" />
        <span className="banner-blob absolute -right-16 top-6 h-72 w-72" />
        <span className="banner-blob absolute bottom-[-4.5rem] left-1/3 h-48 w-48" />
      </div>

      <div className="relative mx-auto flex w-full max-w-7xl flex-col items-start gap-4">
        {breadcrumb}
        <h1 className="font-display text-2xl text-white sm:text-display-md">{title}</h1>
        {description ? (
          <p className="max-w-2xl text-[0.9375rem] leading-relaxed text-white/85 text-pretty">{description}</p>
        ) : null}
        <span className="nums mt-1 inline-flex items-center gap-1.5 rounded-sm border-2 border-white/40 bg-white/10 px-3.5 py-1.5 font-mono text-xs font-semibold text-white backdrop-blur-sm">
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
      <div className="listing-banner h-[13.5rem] w-full sm:h-[15.5rem]" aria-hidden />

      <div className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14">
        <Skeleton className="h-6 w-28" />
        <div className="mt-5 grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {Array.from({ length: 6 }, (_, index) => (
            <Skeleton key={index} className="h-72 rounded-2xl" />
          ))}
        </div>
      </div>
    </main>
  );
}
