import { cx } from "@/components/ui";

/**
 * Star row for a rating, filled to the actual value at half-star resolution
 * (mirrors customer-web's `ReviewsSummary.tsx` `Stars` - kept as its own copy
 * here rather than a shared import since neither app depends on the other's
 * `src/components`). The number itself is the accessible name; the glyphs
 * carry no meaning on their own to a screen reader.
 */
export function Stars({ rating, size = "md" }: { rating: number; size?: "sm" | "md" }) {
  const rounded = Math.round(rating * 2) / 2;
  const dimension = size === "sm" ? "h-3.5 w-3.5" : "h-4 w-4";

  return (
    <span
      className="flex items-center gap-0.5"
      role="img"
      aria-label={`${rating.toFixed(1)} out of 5 stars`}
    >
      {[1, 2, 3, 4, 5].map((position) => {
        const fill = Math.max(0, Math.min(1, rounded - position + 1));
        return (
          <span key={position} className={cx("relative inline-block", dimension)}>
            <StarIcon className={cx("absolute inset-0", dimension, "text-line-strong")} />
            {fill > 0 ? (
              <span
                className="absolute inset-0 overflow-hidden"
                style={{ width: `${fill * 100}%` }}
                aria-hidden
              >
                <StarIcon className={cx(dimension, "text-accent-500")} />
              </span>
            ) : null}
          </span>
        );
      })}
    </span>
  );
}

function StarIcon({ className = "" }: { className?: string }) {
  return (
    <svg viewBox="0 0 24 24" fill="currentColor" className={className} aria-hidden>
      <path d="m12 2.5 2.9 5.9 6.6.9-4.8 4.6 1.2 6.5-5.9-3.1-5.9 3.1 1.2-6.5L2.5 9.3l6.6-.9L12 2.5Z" />
    </svg>
  );
}
