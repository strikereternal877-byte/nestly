"use client";

import { motion } from "motion/react";
import { cx } from "@/components/ui";

export interface SegmentedOption<T extends string> {
  value: T;
  label: string;
  hint: string;
}

/**
 * Two or three mutually exclusive choices with a sliding highlight - how often
 * a cook comes, how it is paid for. `layoutId` must be unique per instance on
 * the page so each highlight slides only within its own group.
 */
export function SegmentedChoice<T extends string>({
  label,
  layoutId,
  value,
  options,
  onChange,
}: {
  label: string;
  layoutId: string;
  value: T;
  options: SegmentedOption<T>[];
  onChange: (next: T) => void;
}) {
  return (
    <div
      role="radiogroup"
      aria-label={label}
      className={cx(
        "grid gap-1 rounded-2xl border border-line bg-surface-2 p-1",
        options.length === 3 ? "grid-cols-3" : "grid-cols-2",
      )}
    >
      {options.map((option) => {
        const on = option.value === value;
        return (
          <button
            key={option.value}
            type="button"
            role="radio"
            aria-checked={on}
            onClick={() => onChange(option.value)}
            className="relative flex flex-col items-start rounded-xl px-4 py-3 text-left"
          >
            {on ? (
              <motion.span
                layoutId={layoutId}
                className="absolute inset-0 rounded-xl bg-surface shadow-sm"
                transition={{ type: "spring", stiffness: 420, damping: 34 }}
              />
            ) : null}
            <span
              className={cx(
                "relative text-sm font-semibold",
                on ? "text-fg" : "text-fg-muted",
              )}
            >
              {option.label}
            </span>
            <span className="relative text-xs text-fg-muted">
              {option.hint}
            </span>
          </button>
        );
      })}
    </div>
  );
}
