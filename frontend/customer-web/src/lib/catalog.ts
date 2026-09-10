import type { CategoryDetail } from "@/lib/types";

/**
 * Service/subcategory counts for a category, shared by the server-rendered
 * `PageBanner` badge (`app/categories/[slug]/page.tsx`) and the client
 * listing (`CategoryDetailClient`) so both read the same rule instead of
 * drifting - Appliance/Service Group catalog redesign: a group's members
 * count too, not just the ungrouped top-level list.
 */
export function getCategoryCounts(category: CategoryDetail): {
  totalServiceCount: number;
  subcategoryCount: number;
  hasSubcategories: boolean;
} {
  const totalServiceCount =
    category.serviceGroups.reduce((count, group) => count + group.services.length, 0) + category.services.length;

  const subcategoryCount =
    category.subcategoryGroups.reduce((count, group) => count + group.subcategories.length, 0) +
    category.subcategories.length;

  return { totalServiceCount, subcategoryCount, hasSubcategories: subcategoryCount > 0 };
}
