import type { Metadata } from "next";
import Link from "next/link";

import { PageBanner } from "@/components/PageBanner";
import { getCategoryCounts } from "@/lib/catalog";
import { serverJson } from "@/lib/server-api";
import type { CategoryDetail } from "@/lib/types";

import CategoryDetailClient from "./CategoryDetailClient";

/**
 * Server shell for the category detail page (SEO fix, docs/OPEN-FIXES-FEATURES.csv
 * "Service + category pages, Server-side rendering") - same reasoning as the
 * service route's `page.tsx`: `generateMetadata` already server-rendered the
 * title/description, and this now fetches the same category (deduped by
 * Next's Request Memoization) to render the name, description and banner
 * image in the initial HTML via `PageBanner` too. The listing itself
 * (subcategories/services, counts) stays inside `CategoryDetailClient`,
 * which receives the already-fetched category as `initialCategory` so it
 * renders immediately instead of re-fetching and flashing its skeleton.
 */
export async function generateMetadata({
  params,
}: {
  params: { slug: string };
}): Promise<Metadata> {
  const category = await serverJson<CategoryDetail>(`/categories/${params.slug}`);
  if (!category) return {};

  return {
    title: category.name,
    description: category.description,
    alternates: { canonical: `/categories/${category.slug}` },
    openGraph: {
      title: category.name,
      description: category.description,
      type: "website",
      ...(category.bannerUrl ? { images: [category.bannerUrl] } : {}),
    },
  };
}

export default async function CategoryDetailPage({
  params,
}: {
  params: { slug: string };
}) {
  const category = await serverJson<CategoryDetail>(`/categories/${params.slug}`);

  return (
    <main className="flex w-full flex-col">
      {category ? (
        <PageBanner
          title={category.name}
          description={category.description}
          imageUrl={category.pageBannerUrl}
          breadcrumb={<Breadcrumb categoryName={category.name} />}
          badge={<CountBadge category={category} />}
        />
      ) : null}

      <CategoryDetailClient initialCategory={category} slug={params.slug} />
    </main>
  );
}

function CountBadge({ category }: { category: CategoryDetail }) {
  const { totalServiceCount, subcategoryCount, hasSubcategories } = getCategoryCounts(category);

  return (
    <span className="mt-1 inline-flex items-center gap-1.5 rounded-full bg-white/15 px-3.5 py-1.5 text-xs font-semibold text-white backdrop-blur-sm">
      {hasSubcategories
        ? `${subcategoryCount} ${subcategoryCount === 1 ? "type" : "types"} available`
        : `${totalServiceCount} ${totalServiceCount === 1 ? "service" : "services"} available`}
    </span>
  );
}

/**
 * Breadcrumb for the full-bleed `PageBanner`. Moved here from
 * `CategoryDetailClient` alongside the banner - static content sourced from
 * the same server-fetched category, not interactive.
 */
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
