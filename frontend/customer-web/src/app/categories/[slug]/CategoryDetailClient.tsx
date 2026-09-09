"use client";

import { useQuery } from "@tanstack/react-query";
import { motion } from "motion/react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { CategoryGroupSection } from "@/components/CategoryGroupSection";
import { PageBanner } from "@/components/PageBanner";
import { ServiceCard } from "@/components/ServiceCard";
import { ServiceGroupSection } from "@/components/ServiceGroupSection";
import { SubcategoryTileGrid } from "@/components/SubcategoryTileGrid";
import { Reveal, revealItem } from "@/components/motion";
import { Alert, Button, EmptyState, LinkButton, Skeleton } from "@/components/ui";
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

  const subcategoryCount =
    category.subcategoryGroups.reduce((count, group) => count + group.subcategories.length, 0) +
    category.subcategories.length;
  const hasSubcategories = subcategoryCount > 0;

  return (
    <main className="flex w-full flex-col">
      <PageBanner
        title={category.name}
        description={category.description}
        imageUrl={category.pageBannerUrl}
        breadcrumb={<Breadcrumb categoryName={category.name} />}
        badge={
          <span className="mt-1 inline-flex items-center gap-1.5 rounded-full bg-white/15 px-3.5 py-1.5 text-xs font-semibold text-white backdrop-blur-sm">
            {hasSubcategories
              ? `${subcategoryCount} ${subcategoryCount === 1 ? "type" : "types"} available`
              : `${totalServiceCount} ${totalServiceCount === 1 ? "service" : "services"} available`}
          </span>
        }
      />

      <div className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14">
        {hasSubcategories ? (
          // A category with subcategories is a pure picker (matches Urban
          // Company: the parent screen never also lists services directly) -
          // any service attached straight to this category is intentionally
          // not shown here; it stays reachable via search/its own URL, just
          // not mixed into this browsing surface.
          <section aria-labelledby="subcategories-heading">
            <h2 id="subcategories-heading" className="mb-3 text-lg font-semibold tracking-tight text-fg">
              Browse by type
            </h2>
            <div className="flex flex-col gap-6">
              {/* Section headers only for groups that exist, same rule as
                  ServiceGroupSection below - a category with none renders
                  exactly the flat tile grid, same as before subcategory
                  groups existed. */}
              {category.subcategoryGroups.map((group) => (
                <CategoryGroupSection key={group.id} group={group} />
              ))}

              {category.subcategories.length > 0 ? (
                <SubcategoryTileGrid subcategories={category.subcategories} />
              ) : null}
            </div>
          </section>
        ) : (
          <section aria-labelledby="services-heading">
            <h2 id="services-heading" className="mb-5 text-lg font-semibold tracking-tight text-fg">
              Services
              <span className="ml-2 text-sm font-normal text-fg-subtle">{totalServiceCount}</span>
            </h2>

            {totalServiceCount === 0 ? (
              <EmptyState
                title="Nothing listed yet"
                description="No services are listed under this category in your city yet — check back soon."
                action={
                  <LinkButton href="/categories" variant="secondary">
                    Browse other categories
                  </LinkButton>
                }
              />
            ) : (
              <div className="flex flex-col gap-6">
                {/* Section headers only for groups that exist (SRS 11.5.5) - a
                    category with none renders exactly the flat grid below, same
                    as every category before service groups existed. */}
                {category.serviceGroups.map((group) => (
                  <ServiceGroupSection key={group.id} group={group} />
                ))}

                {category.services.length > 0 ? (
                  <Reveal className="grid grid-cols-2 gap-5 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5">
                    {category.services.map((service) => (
                      <motion.div key={service.id} variants={revealItem}>
                        <ServiceCard
                          slug={service.slug}
                          name={service.name}
                          price={service.price}
                          coverImageUrl={service.coverImageUrl}
                        />
                      </motion.div>
                    ))}
                  </Reveal>
                ) : null}
              </div>
            )}
          </section>
        )}
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

/** Mirrors the loaded page's frame so the heading and grid don't jump into place. */
function CategoryDetailSkeleton() {
  return (
    <main className="flex w-full flex-col">
      {/* Mirrors ListingBanner's real height so the page doesn't jump when it resolves. */}
      <div className="listing-banner h-[13.5rem] w-full sm:h-[15.5rem]" aria-hidden />

      <div className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14">
        <Skeleton className="h-6 w-28" />
        <div className="mt-5 grid grid-cols-2 gap-5 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5">
          {Array.from({ length: 10 }, (_, index) => (
            <Skeleton key={index} className="h-56 rounded-2xl" />
          ))}
        </div>
      </div>
    </main>
  );
}
