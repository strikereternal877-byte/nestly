/**
 * Meal-priced services - a cook (docs/MEAL-SERVICE.md).
 *
 * `quoteMeals` mirrors the server's MealPricing.Quote so prices and visit
 * times move live as the customer picks; the server recomputes everything
 * when the booking or monthly plan is created, so this is display only.
 */

/** Ordinals match the backend's MealType enum - they cross the wire as numbers. */
export enum MealType {
  Breakfast = 0,
  Lunch = 1,
  Snacks = 2,
  Dinner = 3,
}

export type MealSession = "morning" | "evening";

export interface MealOption {
  meal: MealType;
  baseRate: number;
  includedPersons: number;
  extraPersonRate: number;
  baseMinutes: number;
  minutesPerExtraPerson: number;
  /** "HH:mm" */
  defaultStartTime: string;
}

export interface MealVisitQuote {
  session: MealSession;
  meals: MealType[];
  rate: number;
  durationMinutes: number;
  /** "HH:mm" - the earliest default time among this visit's meals. */
  defaultStartTime: string;
  /** "HH:mm" - the latest default time among this visit's meals. */
  lastMealStartTime: string;
}

export const MAX_PERSONS = 12;

/** Visits starting before this are morning visits (backend MealTypeExtensions.EveningStartsAt). */
export const EVENING_STARTS_AT = "15:00";

export const MEAL_LABELS: Record<MealType, string> = {
  [MealType.Breakfast]: "Breakfast",
  [MealType.Lunch]: "Lunch",
  [MealType.Snacks]: "Evening snacks",
  [MealType.Dinner]: "Dinner",
};

export function sessionOf(meal: MealType): MealSession {
  return meal === MealType.Breakfast || meal === MealType.Lunch
    ? "morning"
    : "evening";
}

export function toMinutes(hhmm: string): number {
  const [h, m] = hhmm.split(":").map(Number);
  return h * 60 + m;
}

export function fromMinutes(total: number): string {
  const h = Math.floor(total / 60);
  const m = total % 60;
  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}`;
}

/** Whether a visit starting at `hhmm` belongs to `session`. */
export function sessionContains(session: MealSession, hhmm: string): boolean {
  const before = toMinutes(hhmm) < toMinutes(EVENING_STARTS_AT);
  return session === "morning" ? before : !before;
}

/**
 * The time window a one-time visit should start in: the one the meals' usual
 * start time falls in, else the one starting nearest to it. Times are
 * "HH:mm" or "HH:mm:ss".
 */
export function closestWindow<T extends { startTime: string; endTime: string }>(
  windows: T[],
  hhmm: string,
): T | null {
  const at = toMinutes(hhmm);
  const start = (w: T) => toMinutes(w.startTime);
  return (
    windows.find((w) => start(w) <= at && at < toMinutes(w.endTime)) ??
    [...windows].sort(
      (a, b) => Math.abs(start(a) - at) - Math.abs(start(b) - at),
    )[0] ??
    null
  );
}

/** A visit may start this much before its first meal's usual time, and at least this much after. */
const START_SHIFT_MINUTES = 60;

/**
 * Start times a monthly visit may be moved to, half an hour apart - the
 * server's MealVisitQuote.EarliestStart..LatestStart: from an hour before the
 * first meal's usual time to the last meal's (at least an hour after the
 * first), between 06:00 and 21:30, a morning visit staying before the evening.
 */
export function startChoices(
  quote: Pick<
    MealVisitQuote,
    "session" | "defaultStartTime" | "lastMealStartTime"
  >,
): string[] {
  const first = toMinutes(quote.defaultStartTime);
  const evening = toMinutes(EVENING_STARTS_AT);
  const from = Math.max(
    toMinutes("06:00"),
    quote.session === "evening" ? evening : 0,
    first - START_SHIFT_MINUTES,
  );
  const to = Math.min(
    toMinutes("21:30"),
    quote.session === "morning" ? evening - 30 : Infinity,
    Math.max(toMinutes(quote.lastMealStartTime), first + START_SHIFT_MINUTES),
  );
  const choices: string[] = [];
  for (let m = from; m <= to; m += 30) choices.push(fromMinutes(m));
  return choices;
}

/** A monthly visit's rate: the one-time rate less the plan's discount, rounded like the server (MonthlyServiceContract). */
export function discountedRate(rate: number, discountPercent: number): number {
  return Math.round(rate * (100 - discountPercent)) / 100;
}

/** Average weeks in a month - only for "about Rs X a month", never for billing. */
const WEEKS_PER_MONTH = 4.33;

export function monthlyEstimate(perDay: number, daysPerWeek: number): number {
  return perDay * daysPerWeek * WEEKS_PER_MONTH;
}

export function rateFor(option: MealOption, persons: number): number {
  return (
    option.baseRate +
    Math.max(0, persons - option.includedPersons) * option.extraPersonRate
  );
}

export function minutesFor(option: MealOption, persons: number): number {
  return (
    option.baseMinutes +
    Math.max(0, persons - option.includedPersons) * option.minutesPerExtraPerson
  );
}

/** One quote per session the chosen meals fall in, morning first - same rule as the server. */
export function quoteMeals(
  options: MealOption[],
  meals: MealType[],
  persons: number,
): MealVisitQuote[] {
  const byMeal = new Map(options.map((o) => [o.meal, o]));
  const chosen = [...new Set(meals)]
    .filter((m) => byMeal.has(m))
    .sort((a, b) => a - b);
  return (["morning", "evening"] as const)
    .map((session) => {
      const picked = chosen
        .filter((m) => sessionOf(m) === session)
        .map((m) => byMeal.get(m)!);
      if (picked.length === 0) return null;
      return {
        session,
        meals: picked.map((o) => o.meal),
        rate: picked.reduce((sum, o) => sum + rateFor(o, persons), 0),
        durationMinutes: picked.reduce(
          (sum, o) => sum + minutesFor(o, persons),
          0,
        ),
        defaultStartTime: picked.map((o) => o.defaultStartTime).sort()[0],
        lastMealStartTime: picked
          .map((o) => o.defaultStartTime)
          .sort()
          .at(-1)!,
      };
    })
    .filter((q): q is MealVisitQuote => q !== null);
}

/** "Breakfast and lunch" - a visit's meals in a sentence. */
export function describeMeals(meals: MealType[]): string {
  return meals
    .map((m, i) => (i === 0 ? MEAL_LABELS[m] : MEAL_LABELS[m].toLowerCase()))
    .join(" and ");
}

/** "1 hr 20 min" */
export function formatDuration(minutes: number): string {
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  if (h === 0) return `${m} min`;
  return m === 0 ? `${h} hr` : `${h} hr ${m} min`;
}

/** URL form of a meal selection, read back by the booking summary page: `meals=1.3&persons=4`. */
export function mealsToParam(meals: MealType[]): string {
  return [...meals].sort((a, b) => a - b).join(".");
}

export function mealsFromParam(value: string | null): MealType[] {
  if (!value) return [];
  return value
    .split(".")
    .map(Number)
    .filter((n) => Number.isInteger(n) && n in MealType);
}

const RUPEES = new Intl.NumberFormat("en-IN", {
  style: "currency",
  currency: "INR",
  maximumFractionDigits: 0,
});

/** Whole rupees, Indian grouping: "₹8,320". */
export function rupees(amount: number): string {
  return RUPEES.format(Math.round(amount));
}
