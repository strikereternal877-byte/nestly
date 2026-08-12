/**
 * THE SERVICE LEDGER: per-category "binder divider" tab colour, one per real
 * category (public/images/categories) — never invented, never fabricated.
 * Keyed by slug because that's the stable identifier CategorySummary/
 * CategoryDetail already carry; falls back to a neutral ink tone for a
 * category not in this set rather than guessing a colour.
 *
 * Tailwind's JIT scanner only picks up class names it can see literally in
 * source, so these are written out in full here rather than built with
 * string interpolation — `bg-category-${slug}` would never match anything.
 */
const CATEGORY_TAB_BG: Record<string, string> = {
  "home-cleaning": "bg-category-home-cleaning",
  "ac-repair-service": "bg-category-ac-repair-service",
  "appliance-repair": "bg-category-appliance-repair",
  carpentry: "bg-category-carpentry",
  electrical: "bg-category-electrical",
  painting: "bg-category-painting",
  "pest-control": "bg-category-pest-control",
  plumbing: "bg-category-plumbing",
  "salon-for-men": "bg-category-salon-for-men",
  "salon-for-women": "bg-category-salon-for-women",
};

const CATEGORY_TAB_TEXT: Record<string, string> = {
  "home-cleaning": "text-category-home-cleaning",
  "ac-repair-service": "text-category-ac-repair-service",
  "appliance-repair": "text-category-appliance-repair",
  carpentry: "text-category-carpentry",
  electrical: "text-category-electrical",
  painting: "text-category-painting",
  "pest-control": "text-category-pest-control",
  plumbing: "text-category-plumbing",
  "salon-for-men": "text-category-salon-for-men",
  "salon-for-women": "text-category-salon-for-women",
};

/** Tab-flap fill colour class for a category slug. Falls back to a neutral ink tone. */
export function categoryTabBg(slug: string): string {
  return CATEGORY_TAB_BG[slug] ?? "bg-fg-subtle";
}

/** Matching text-colour class, for uses where the tab colour sits on the kraft ground rather than as a fill (e.g. an active underline). */
export function categoryTabText(slug: string): string {
  return CATEGORY_TAB_TEXT[slug] ?? "text-fg-subtle";
}
