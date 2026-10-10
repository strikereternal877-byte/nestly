import type { MetadataRoute } from "next";

import { serverJson } from "@/lib/server-api";
import { siteUrl } from "@/lib/site-url";
import type { CategoryDetail, CategorySummary, City } from "@/lib/types";

/** Re-crawled hourly; the catalog changes far more slowly than that. */
export const revalidate = 3600;

/** Public routes that always exist, independent of the catalog. */
const STATIC_PATHS = [
  "/",
  "/categories",
  "/search",
  "/terms",
  "/privacy",
  "/refund-policy",
  "/contact",
  "/install-app",
];

/**
 * Every service and category slug reachable from the catalog tree.
 *
 * A category's services live in four places once groups are involved -
 * `services`, `serviceGroups[].services`, `subcategories` and
 * `subcategoryGroups[].subcategories` - so this walks all of them, keyed by a
 * visited set because a deep tree would otherwise refetch shared branches.
 *
 * Breadth-first in parallel waves, one wave per tree depth, rather than one
 * request at a time: the catalog is wide (dozens of categories) but shallow
 * (a handful of levels), so fetching a whole wave with Promise.all turns an
 * O(category count) sequential build-time crawl into O(tree depth) - the
 * difference between finishing well inside Next's static-generation timeout
 * and running past it. Safe to do unconditionally because serverJson never
 * rejects (see its own doc comment) - a failed fetch resolves to null and is
 * skipped, exactly like the old one-at-a-time version already did.
 */
async function crawlCatalog(): Promise<{ categories: Set<string>; services: Set<string> }> {
  const categories = new Set<string>();
  const services = new Set<string>();

  const cities = (await serverJson<City[]>("/geography/cities")) ?? [];

  const topLevelByCity = await Promise.all(
    cities.map((city) => serverJson<CategorySummary[]>(`/categories?cityId=${city.id}`)),
  );

  let frontier: string[] = [];
  for (const summaries of topLevelByCity) {
    for (const summary of summaries ?? []) {
      if (!categories.has(summary.slug)) {
        categories.add(summary.slug);
        frontier.push(summary.slug);
      }
    }
  }

  while (frontier.length > 0) {
    const details = await Promise.all(frontier.map((slug) => serverJson<CategoryDetail>(`/categories/${slug}`)));

    const nextFrontier: string[] = [];
    for (const detail of details) {
      if (!detail) continue;

      const nested = [
        ...detail.services,
        ...detail.serviceGroups.flatMap((group) => group.services),
      ];
      for (const service of nested) services.add(service.slug);

      const children = [
        ...detail.subcategories,
        ...detail.subcategoryGroups.flatMap((group) => group.subcategories),
      ];
      for (const child of children) {
        if (!categories.has(child.slug)) {
          categories.add(child.slug);
          nextFrontier.push(child.slug);
        }
      }
    }
    frontier = nextFrontier;
  }

  return { categories, services };
}

export default async function sitemap(): Promise<MetadataRoute.Sitemap> {
  const base = siteUrl();
  const lastModified = new Date();

  const entries: MetadataRoute.Sitemap = STATIC_PATHS.map((path) => ({
    url: `${base}${path}`,
    lastModified,
    changeFrequency: "weekly",
    priority: path === "/" ? 1 : 0.5,
  }));

  // A catalog that cannot be reached degrades to the static routes rather
  // than failing the whole file - an empty sitemap is worse than a partial one.
  const { categories, services } = await crawlCatalog();

  for (const slug of Array.from(categories)) {
    entries.push({
      url: `${base}/categories/${slug}`,
      lastModified,
      changeFrequency: "weekly",
      priority: 0.8,
    });
  }

  for (const slug of Array.from(services)) {
    entries.push({
      url: `${base}/services/${slug}`,
      lastModified,
      changeFrequency: "weekly",
      priority: 0.7,
    });
  }

  return entries;
}
