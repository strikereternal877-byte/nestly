"use client";

import { cx } from "@/components/ui";
import type { RepeatPlanKind } from "@/lib/booking-draft";

const PLAN_KINDS: { value: RepeatPlanKind; title: string; summary: string; detail: string }[] = [
  {
    value: "daily",
    title: "Daily plan",
    summary: "A visit every day, paid day by day",
    detail: "Each day's visit is paid as it's booked - automatically from your wallet.",
  },
  {
    value: "prepaid",
    title: "Prepaid plan",
    summary: "Daily, weekly or monthly - paid now",
    detail: "Pick the visits you want and pay for all of them in one payment.",
  },
];

/**
 * The choice between the two ways to auto-schedule a service, as two large tiles rather than a dropdown: the
 * difference is about *when the money leaves the customer's account*, which is the one thing they need to see
 * side by side before they commit. A radio group, so it is a single tab stop with arrow-key movement.
 */
export function PlanKindPicker({
  value,
  onChange,
}: {
  value: RepeatPlanKind;
  onChange: (value: RepeatPlanKind) => void;
}) {
  return (
    <div role="radiogroup" aria-label="Plan type" className="grid gap-3 sm:grid-cols-2">
      {PLAN_KINDS.map((kind) => {
        const isSelected = value === kind.value;
        return (
          <button
            key={kind.value}
            type="button"
            role="radio"
            aria-checked={isSelected}
            onClick={() => onChange(kind.value)}
            className={cx(
              "flex flex-col gap-1 rounded-xl border p-4 text-left transition duration-fast ease-out",
              isSelected
                ? "border-brand-600 bg-brand-50 shadow-brand dark:bg-brand-500/15"
                : "border-line bg-surface hover:border-line-strong hover:bg-surface-2",
            )}
          >
            <span className="flex items-center justify-between gap-2">
              <span className="text-sm font-semibold text-fg">{kind.title}</span>
              <span
                aria-hidden
                className={cx(
                  "h-4 w-4 shrink-0 rounded-full border-2",
                  isSelected ? "border-brand-600 bg-brand-600 ring-2 ring-inset ring-surface" : "border-line-strong",
                )}
              />
            </span>
            <span className="text-xs font-medium text-fg-muted">{kind.summary}</span>
            <span className="text-xs leading-relaxed text-fg-subtle">{kind.detail}</span>
          </button>
        );
      })}
    </div>
  );
}
