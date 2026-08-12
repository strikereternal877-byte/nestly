import Link from "next/link";
import { categoryTabBg } from "@/lib/categoryVisuals";
import type { CategorySummary } from "@/lib/types";

/**
 * Horizontally scrollable strip of subcategory chips shown at the top of a
 * category page (Phase 3 catalog redesign, visual pass) - the
 * within-category-page navigation pattern common to home-services apps,
 * replacing what was previously a tile grid. Each chip is a plain `Link`,
 * not a toggle: clicking navigates to the subcategory's own page rather than
 * filtering in place, so there is no "selected" state to track here.
 *
 * Follows the same `-mx-N ... overflow-x-auto px-N ... shrink-0` idiom
 * already established by `SlotPicker`'s date strip, for a consistent feel
 * with the one other horizontal-scroll surface in the app.
 */
export function SubcategoryChips({ subcategories }: { subcategories: CategorySummary[] }) {
  if (subcategories.length === 0) return null;

  return (
    <div className="-mx-1 flex gap-2 overflow-x-auto px-1 pb-2">
      {subcategories.map((subcategory) => (
        <Link
          key={subcategory.id}
          href={`/categories/${subcategory.slug}`}
          className="group flex shrink-0 items-center gap-2 rounded-md border-2 border-line bg-surface py-2 pl-2.5 pr-4 text-sm font-medium text-fg shadow-xs transition duration-fast ease-out hover:border-line-strong hover:bg-surface-2"
        >
          <span
            aria-hidden="true"
            className={`h-2 w-2 shrink-0 rounded-full ${categoryTabBg(subcategory.slug)}`}
          />
          <span
            aria-hidden="true"
            className="flex h-8 w-8 shrink-0 items-center justify-center rounded-md bg-surface-2 text-base"
          >
            {subcategory.iconUrl ? (
              // eslint-disable-next-line @next/next/no-img-element -- admin-supplied external URL, unsuited to static optimization.
              <img src={subcategory.iconUrl} alt="" className="h-5 w-5 object-contain" />
            ) : (
              "🧰"
            )}
          </span>
          {subcategory.name}
        </Link>
      ))}
    </div>
  );
}
