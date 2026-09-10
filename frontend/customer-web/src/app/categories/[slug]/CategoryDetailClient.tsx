"use client";

import { useQuery } from "@tanstack/react-query";
import { motion } from "motion/react";
import { CategoryGroupSection } from "@/components/CategoryGroupSection";
import { ServiceCard } from "@/components/ServiceCard";
import { ServiceGroupSection } from "@/components/ServiceGroupSection";
import { SubcategoryTileGrid } from "@/components/SubcategoryTileGrid";
import { Reveal, revealItem } from "@/components/motion";
import { Alert, Button, EmptyState, LinkButton, Skeleton } from "@/components/ui";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import { getCategoryCounts } from "@/lib/catalog";
import type { CategoryDetail } from "@/lib/types";

/**
 * Category detail page (SRS 11.5.2): its service/subcategory listing.
 *
 * The banner (name, description, image, count badge) now renders
 * server-side in `page.tsx`'s `PageBanner` (SEO fix,
 * docs/OPEN-FIXES-FEATURES.csv "Server-side rendering"). `initialCategory`
 * is the same server-fetched category `page.tsx` already rendered the
 * banner from, passed through as `useQuery`'s `initialData` so this renders
 * immediately instead of re-fetching and flashing its loading skeleton.
 */
export default function CategoryDetailPage({
  initialCategory,
  slug,
}: {
  initialCategory: CategoryDetail | null;
  slug: string;
}) {
  const query = useQuery({
    queryKey: ["category", slug],
    queryFn: () => apiFetch<CategoryDetail>(`${API_V1}/categories/${slug}`),
    ...(initialCategory ? { initialData: initialCategory } : {}),
  });

  if (query.isPending) {
    return <CategoryDetailSkeleton />;
  }

  if (query.isError) {
    return (
      <div className="mx-auto w-full max-w-7xl px-4 py-8 sm:px-6 sm:py-12">
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
      </div>
    );
  }

  const category = query.data;

  // Appliance/Service Group catalog redesign: total count spans both the
  // grouped sections and the ungrouped grid, so a category whose services
  // are entirely grouped (e.g. "AC") doesn't wrongly show the empty state.
  const { totalServiceCount, hasSubcategories } = getCategoryCounts(category);

  return (
    <>
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
    </>
  );
}

/**
 * Mirrors the loaded page's content frame so the heading and grid don't jump
 * into place. Only reached when the server fetch in `page.tsx` failed too
 * (no `initialData`) - no banner placeholder here, matching what `page.tsx`
 * renders in that case.
 */
function CategoryDetailSkeleton() {
  return (
    <div className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14">
      <Skeleton className="h-6 w-28" />
      <div className="mt-5 grid grid-cols-2 gap-5 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5">
        {Array.from({ length: 10 }, (_, index) => (
          <Skeleton key={index} className="h-56 rounded-2xl" />
        ))}
      </div>
    </div>
  );
}
