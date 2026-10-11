"use client";

import { animate, motion, useMotionValue, useTransform } from "motion/react";
import { useEffect } from "react";
import { SPRING } from "@/components/motion";
import { cx } from "@/components/ui";
import {
  MAX_PERSONS,
  MEAL_LABELS,
  rateFor,
  rupees,
  sessionOf,
} from "@/lib/meals";
import type { MealOption, MealSession, MealType } from "@/lib/meals";
import { formatClock } from "@/lib/monthly-service";

/**
 * The small, reusable pieces of a meal-priced booking (docs/MEAL-SERVICE.md).
 * Each is stateless: the flow owns the selection, these only render it and
 * report changes - so a party-cook or tiffin service can reuse them as-is.
 */

/** A rupee amount that glides to its new value instead of jumping, so a price change reads as a change. */
export function AnimatedRupees({
  value,
  className,
  live = false,
}: {
  value: number;
  className?: string;
  /** Announce changes to screen readers - only for the one total the customer is tracking, never inside a button. */
  live?: boolean;
}) {
  const motionValue = useMotionValue(value);
  const text = useTransform(motionValue, (latest) => rupees(latest));

  useEffect(() => {
    const controls = animate(motionValue, value, {
      duration: 0.45,
      ease: [0.16, 1, 0.3, 1],
    });
    return () => controls.stop();
  }, [motionValue, value]);

  return (
    <motion.span
      className={cx("nums", className)}
      aria-live={live ? "polite" : undefined}
    >
      {text}
    </motion.span>
  );
}

const STEP_BUTTON =
  "relative flex h-11 w-11 items-center justify-center rounded-full border border-line bg-surface text-fg transition duration-fast ease-out hover:border-line-strong hover:bg-surface-2 disabled:cursor-not-allowed disabled:opacity-40";

export function PersonStepper({
  value,
  onChange,
}: {
  value: number;
  onChange: (next: number) => void;
}) {
  return (
    <div
      className="flex items-center gap-4"
      role="group"
      aria-label="Number of people"
    >
      <motion.button
        type="button"
        className={STEP_BUTTON}
        aria-label="One person fewer"
        disabled={value <= 1}
        onClick={() => onChange(value - 1)}
        whileTap={{ scale: 0.9 }}
        transition={SPRING}
      >
        <svg
          viewBox="0 0 24 24"
          className="h-5 w-5"
          fill="none"
          stroke="currentColor"
          strokeWidth="2"
          strokeLinecap="round"
          aria-hidden
        >
          <path d="M5 12h14" />
        </svg>
      </motion.button>

      <div
        className="relative h-14 w-12 overflow-hidden text-center"
        aria-live="polite"
      >
        <motion.span
          key={value}
          initial={{ y: 18, opacity: 0 }}
          animate={{ y: 0, opacity: 1 }}
          transition={{ duration: 0.28, ease: [0.16, 1, 0.3, 1] }}
          className="nums absolute inset-0 flex items-center justify-center text-display-md font-semibold text-fg"
        >
          {value}
        </motion.span>
      </div>

      <motion.button
        type="button"
        className={STEP_BUTTON}
        aria-label="One person more"
        disabled={value >= MAX_PERSONS}
        onClick={() => onChange(value + 1)}
        whileTap={{ scale: 0.9 }}
        transition={SPRING}
      >
        <svg
          viewBox="0 0 24 24"
          className="h-5 w-5"
          fill="none"
          stroke="currentColor"
          strokeWidth="2"
          strokeLinecap="round"
          aria-hidden
        >
          <path d="M12 5v14M5 12h14" />
        </svg>
      </motion.button>
    </div>
  );
}

/** Morning reads warm (amber), evening reads deep (brand navy) - the one colour cue used across the meal flow. */
export const SESSION_TONE: Record<MealSession, { dot: string }> = {
  morning: {
    dot: "bg-accent-500",
  },
  evening: {
    dot: "bg-brand-600",
  },
};

export function MealPicker({
  options,
  persons,
  selected,
  onToggle,
}: {
  options: MealOption[];
  persons: number;
  selected: MealType[];
  onToggle: (meal: MealType) => void;
}) {
  return (
    <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
      {options.map((option) => {
        const on = selected.includes(option.meal);
        const session = sessionOf(option.meal);
        return (
          <motion.button
            key={option.meal}
            type="button"
            aria-pressed={on}
            aria-label={`${MEAL_LABELS[option.meal]}, ${formatClock(option.defaultStartTime)}, ${rupees(rateFor(option, persons))}`}
            onClick={() => onToggle(option.meal)}
            whileTap={{ scale: 0.97 }}
            transition={SPRING}
            className={cx(
              "group relative flex flex-col items-start gap-3 rounded-2xl border p-4 text-left transition duration-fast ease-out",
              on
                ? "border-brand-600 bg-brand-50 shadow-sm dark:bg-brand-500/10"
                : "border-line bg-surface hover:border-line-strong hover:bg-surface-2",
            )}
          >
            <span className="flex w-full items-center justify-between">
              <span className="flex items-center gap-1.5 text-xs text-fg-muted">
                <span
                  aria-hidden
                  className={cx(
                    "h-1.5 w-1.5 rounded-full",
                    SESSION_TONE[session].dot,
                  )}
                />
                {formatClock(option.defaultStartTime)}
              </span>
              <span
                aria-hidden
                className={cx(
                  "flex h-5 w-5 items-center justify-center rounded-full border transition duration-fast ease-out",
                  on
                    ? "border-brand-600 bg-brand-600 text-fg-on-brand"
                    : "border-line-strong",
                )}
              >
                {on ? (
                  <svg
                    viewBox="0 0 24 24"
                    className="h-3 w-3"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="3"
                    strokeLinecap="round"
                    strokeLinejoin="round"
                  >
                    <path d="m5 13 4 4L19 7" />
                  </svg>
                ) : null}
              </span>
            </span>
            <span className="text-[0.9375rem] font-semibold text-fg">
              {MEAL_LABELS[option.meal]}
            </span>
            <AnimatedRupees
              value={rateFor(option, persons)}
              className="text-sm text-fg-muted"
            />
          </motion.button>
        );
      })}
    </div>
  );
}

/** Selected/idle look for every choice chip in the meal flow: a tint, not a fill - the solid brand colour is kept for the one main action. */
export function chipClass(on: boolean): string {
  return cx(
    "border transition duration-fast ease-out",
    on
      ? "border-brand-600 bg-brand-50 font-medium text-brand-700 dark:bg-brand-500/15 dark:text-brand-200"
      : "border-line bg-surface text-fg hover:border-line-strong hover:bg-surface-2",
  );
}

export interface Choice {
  value: string;
  label: string;
}

/** A labelled row of single-choice pills: monthly start times, one-time time windows. */
export function ChoiceChips({
  label,
  options,
  value,
  onChange,
  note,
}: {
  label: string;
  options: Choice[];
  value: string | null;
  onChange: (next: string) => void;
  /** A line under the chips about the current choice. */
  note?: string;
}) {
  return (
    <div role="radiogroup" aria-label={label} className="flex flex-col gap-2">
      <span className="text-sm text-fg-muted">{label}</span>
      <div className="flex flex-wrap gap-2">
        {options.map((option) => {
          const on = option.value === value;
          return (
            <button
              key={option.value}
              type="button"
              role="radio"
              aria-checked={on}
              onClick={() => onChange(option.value)}
              className={cx(
                "nums rounded-full px-3.5 py-1.5 text-sm",
                chipClass(on),
              )}
            >
              {option.label}
            </button>
          );
        })}
      </div>
      {note ? <p className="text-xs text-fg-muted">{note}</p> : null}
    </div>
  );
}

/**
 * The next few days as chips, the same shape as the monthly "Cooking days"
 * ones. A day with nothing open is shown but can't be picked. Render only
 * after mount: the labels depend on the browser's clock and locale.
 */
export function DateChips({
  label,
  dates,
  isOpen,
  value,
  onChange,
}: {
  label: string;
  /** "YYYY-MM-DD" */
  dates: string[];
  isOpen: (date: string) => boolean;
  value: string | null;
  onChange: (date: string) => void;
}) {
  return (
    <div className="flex flex-col gap-2">
      <span className="text-sm text-fg-muted">{label}</span>
      <div
        role="radiogroup"
        aria-label={label}
        className="grid grid-cols-7 gap-1.5 sm:flex sm:gap-2"
      >
        {dates.map((date) => {
          const on = date === value;
          const day = new Date(`${date}T00:00:00`);
          return (
            <button
              key={date}
              type="button"
              role="radio"
              aria-checked={on}
              aria-label={day.toLocaleDateString(undefined, {
                weekday: "long",
                day: "numeric",
                month: "long",
              })}
              disabled={!isOpen(date)}
              onClick={() => onChange(date)}
              className={cx(
                "flex h-14 flex-col items-center justify-center gap-0.5 rounded-xl text-sm sm:w-14",
                chipClass(on),
                "disabled:cursor-not-allowed disabled:opacity-40 disabled:hover:border-line disabled:hover:bg-surface",
              )}
            >
              <span>
                {day.toLocaleDateString(undefined, { weekday: "short" })}
              </span>
              <span className={cx("nums text-xs", !on && "text-fg-muted")}>
                {day.getDate()}
              </span>
            </button>
          );
        })}
      </div>
    </div>
  );
}
