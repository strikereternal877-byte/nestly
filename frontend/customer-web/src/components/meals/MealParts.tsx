"use client";

import { animate, motion, useMotionValue, useTransform } from "motion/react";
import { useEffect } from "react";
import { SPRING } from "@/components/motion";
import { cx } from "@/components/ui";
import {
  MAX_PERSONS,
  MEAL_LABELS,
  fromMinutes,
  rateFor,
  rupees,
  sessionContains,
  sessionOf,
  toMinutes,
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
export const SESSION_TONE: Record<
  MealSession,
  { dot: string; bar: string; text: string }
> = {
  morning: {
    dot: "bg-accent-500",
    bar: "bg-gradient-to-r from-accent-400 to-accent-500",
    text: "text-accent-700 dark:text-accent-300",
  },
  evening: {
    dot: "bg-brand-600",
    bar: "bg-gradient-to-r from-brand-500 to-brand-700",
    text: "text-brand-700 dark:text-brand-300",
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

const DAY_START = toMinutes("06:00");
const DAY_END = toMinutes("22:00");
/** End labels hug the track's edges; middle ones centre on their tick. */
const HOUR_LABELS = [
  { hour: 6, label: "6 AM", align: "translate-x-0" },
  { hour: 12, label: "12 PM", align: "-translate-x-1/2" },
  { hour: 18, label: "6 PM", align: "-translate-x-1/2" },
  { hour: 22, label: "10 PM", align: "-translate-x-full" },
];
const position = (minutes: number) =>
  ((Math.min(Math.max(minutes, DAY_START), DAY_END) - DAY_START) /
    (DAY_END - DAY_START)) *
  100;

export interface PlannedVisit {
  session: MealSession;
  meals: MealType[];
  /** "HH:mm" */
  start: string;
  durationMinutes: number;
}

/**
 * The flow's one signature moment: the customer's day from 6 am to 10 pm,
 * with each cooking visit drawn where it falls and as long as it takes. It
 * shows the two things a price table cannot - that breakfast and dinner are
 * two separate visits, and that cooking for more people takes longer.
 */
export function DayStrip({ visits }: { visits: PlannedVisit[] }) {
  return (
    <div className="rounded-2xl border border-line bg-surface p-4 sm:p-5">
      <div className="relative h-12">
        <div
          aria-hidden
          className="absolute inset-x-0 top-1/2 h-px -translate-y-1/2 bg-line"
        />
        {[6, 9, 12, 15, 18, 21].map((hour) => (
          <span
            key={hour}
            aria-hidden
            className="absolute top-1/2 h-2 w-px -translate-y-1/2 bg-line-strong"
            style={{ left: `${position(hour * 60)}%` }}
          />
        ))}
        {visits.map((visit) => {
          const start = toMinutes(visit.start);
          // Enters in place (grows from its start), then slides/stretches
          // when the time or the number of people changes.
          const place = {
            left: `${position(start)}%`,
            width: `${Math.max(position(start + visit.durationMinutes) - position(start), 1.5)}%`,
          };
          return (
            <motion.div
              key={visit.session}
              aria-hidden
              className={cx(
                "absolute top-1/2 h-3 -translate-y-1/2 rounded-full shadow-sm",
                SESSION_TONE[visit.session].bar,
              )}
              initial={{ opacity: 0, scaleX: 0.4, ...place }}
              animate={{ opacity: 1, scaleX: 1, ...place }}
              transition={{ type: "spring", stiffness: 260, damping: 30 }}
              style={{ originX: 0 }}
            />
          );
        })}
      </div>
      <div
        aria-hidden
        className="relative mt-1 h-4 whitespace-nowrap text-[0.6875rem] text-fg-subtle"
      >
        {HOUR_LABELS.map(({ hour, label, align }) => (
          <span
            key={label}
            className={cx("absolute", align)}
            style={{ left: `${position(hour * 60)}%` }}
          >
            {label}
          </span>
        ))}
      </div>

      <ul className="mt-4 flex flex-col gap-2 border-t border-line pt-4 text-sm">
        {visits.length === 0 ? (
          <li className="text-fg-muted">
            Choose a meal to see when your cook comes.
          </li>
        ) : (
          visits.map((visit) => (
            <motion.li
              key={visit.session}
              layout
              initial={{ opacity: 0, y: 6 }}
              animate={{ opacity: 1, y: 0 }}
              className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-0.5"
            >
              <span className="flex items-center gap-2 font-medium text-fg">
                <span
                  aria-hidden
                  className={cx(
                    "h-2 w-2 rounded-full",
                    SESSION_TONE[visit.session].dot,
                  )}
                />
                {visit.session === "morning"
                  ? "Morning visit"
                  : "Evening visit"}
                <span className="font-normal text-fg-muted">
                  for{" "}
                  {visit.meals
                    .map((m) => MEAL_LABELS[m].toLowerCase())
                    .join(" and ")}
                </span>
              </span>
              <span className="nums text-fg-muted">
                {formatClock(visit.start)} to{" "}
                {formatClock(
                  fromMinutes(toMinutes(visit.start) + visit.durationMinutes),
                )}
              </span>
            </motion.li>
          ))
        )}
      </ul>
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

/** Start-time choices around a session's default, half-hour apart, never crossing into the other session. */
export function timeChoices(
  session: MealSession,
  defaultStart: string,
): string[] {
  const base = toMinutes(defaultStart);
  return [-60, -30, 0, 30, 60]
    .map((offset) => base + offset)
    .filter((m) => m >= toMinutes("06:00") && m <= toMinutes("21:30"))
    .map(fromMinutes)
    .filter((t) => sessionContains(session, t));
}

export function TimeChips({
  label,
  choices,
  value,
  onChange,
}: {
  label: string;
  choices: string[];
  value: string;
  onChange: (next: string) => void;
}) {
  return (
    <div role="radiogroup" aria-label={label} className="flex flex-col gap-2">
      <span className="text-sm text-fg-muted">{label}</span>
      <div className="flex flex-wrap gap-2">
        {choices.map((choice) => {
          const on = choice === value;
          return (
            <button
              key={choice}
              type="button"
              role="radio"
              aria-checked={on}
              onClick={() => onChange(choice)}
              className={cx(
                "nums rounded-full px-3.5 py-1.5 text-sm",
                chipClass(on),
              )}
            >
              {formatClock(choice)}
            </button>
          );
        })}
      </div>
    </div>
  );
}
