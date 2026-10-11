"use client";

import { useQuery } from "@tanstack/react-query";
import { AnimatePresence, motion } from "motion/react";
import { useCallback, useMemo, useState, useSyncExternalStore } from "react";
import { createPortal } from "react-dom";
import type { ReactNode } from "react";
import {
  AnimatedRupees,
  ChoiceChips,
  chipClass,
  DateChips,
  MealPicker,
  PersonStepper,
} from "@/components/meals/MealParts";
import { SegmentedChoice } from "@/components/SegmentedChoice";
import {
  AreaGate,
  useSlotDays,
  useUpcomingDates,
} from "@/components/SlotPicker";
import { Alert, Button, LinkButton, Skeleton, cx } from "@/components/ui";
import { useSelectedCity } from "@/hooks/useSelectedCity";
import { useServiceability } from "@/hooks/useServiceability";
import { describeError } from "@/lib/api";
import { isoDateOffsetFromToday } from "@/lib/date";
import {
  MealType,
  closestWindow,
  describeMeals,
  discountedRate,
  formatDuration,
  fromMinutes,
  mealsToParam,
  monthlyEstimate,
  quoteMeals,
  sessionContains,
  sessionOf,
  startChoices,
  toMinutes,
} from "@/lib/meals";
import type { CookPageState, MealSession } from "@/lib/meals";
import {
  MonthlyServicePlanBasis,
  WEEKDAYS,
  browseMonthlyPlans,
  formatClock,
} from "@/lib/monthly-service";
import type {
  ServiceDetail,
  SlotDayAvailability,
  SlotOption,
} from "@/lib/types";

const MON_TO_SAT = [1, 2, 3, 4, 5, 6];

type Mode = "monthly" | "once";

const EASE = [0.16, 1, 0.3, 1] as const;

const isOpenDay = (day: SlotDayAvailability) =>
  day.isServiceable && day.slots.length > 0;

/**
 * Booking a cook (docs/MEAL-SERVICE.md): how many people, which meals, how
 * often. Everything the customer picks moves the price and the visit times
 * live; nothing is asked that does not change one of them. Monthly picks its
 * days, times and first day here, then reviews them (address, how to pay,
 * confirm) on /monthly-service/summary; "just once" picks its day and time window here
 * too (the first open day and the window the meals' usual time falls in are
 * chosen for the customer), then hands everything to the normal checkout for
 * address and payment. How to pay is asked on those review pages, not here.
 */
export function MealBookingFlow({
  service,
  initial = {},
}: {
  service: ServiceDetail;
  /** Where to start: the page's own URL, from a review page's "Change" or "keep this cook monthly". */
  initial?: CookPageState;
}) {
  const options = useMemo(
    () => service.mealOptions ?? [],
    [service.mealOptions],
  );
  const { city, locality } = useSelectedCity();
  const { isUnserviceable } = useServiceability(service.id);

  const [persons, setPersons] = useState(initial.persons ?? 2);
  const [meals, setMeals] = useState<MealType[]>(() => {
    const offered = initial.meals?.filter((m) =>
      options.some((o) => o.meal === m),
    );
    if (offered?.length) return offered;
    return options.some((o) => o.meal === MealType.Lunch)
      ? [MealType.Lunch]
      : options.slice(0, 1).map((o) => o.meal);
  });
  const [chosenMode, setChosenMode] = useState<Mode | null>(
    initial.mode ?? null,
  );
  const [days, setDays] = useState<number[]>(initial.days ?? MON_TO_SAT);
  // A first day already past (an old link) falls back to tomorrow.
  const [startDate, setStartDate] = useState(() =>
    initial.start && initial.start >= isoDateOffsetFromToday(0)
      ? initial.start
      : isoDateOffsetFromToday(1),
  );
  const upcomingDates = useUpcomingDates();
  const [times, setTimes] = useState<Partial<Record<MealSession, string>>>({
    morning: initial.morning,
    evening: initial.evening,
  });
  // What the customer picked for a one-time visit; null until they do.
  const [onceDate, setOnceDate] = useState<string | null>(initial.date ?? null);
  // Remembered with its session: a morning window means nothing once the meals move to the evening.
  const [onceSlot, setOnceSlot] = useState<{
    id: string;
    session: MealSession;
  } | null>(() =>
    initial.slot && initial.meals?.length
      ? { id: initial.slot, session: sessionOf(initial.meals[0]) }
      : null,
  );

  const plansQuery = useQuery({
    queryKey: ["monthly-plans", city?.id ?? "all"],
    queryFn: () => browseMonthlyPlans(city?.id),
    enabled: city !== undefined,
  });
  const plan =
    plansQuery.data?.find(
      (p) =>
        p.serviceId === service.id &&
        p.basis === MonthlyServicePlanBasis.MealBased,
    ) ?? null;
  const mode: Mode = chosenMode ?? (plan ? "monthly" : "once");
  const monthly = mode === "monthly" && plan !== null;

  const quotes = useMemo(
    () => quoteMeals(options, meals, persons),
    [options, meals, persons],
  );

  // A chosen time survives meal changes only while it is still one of the
  // offered choices for its session; otherwise the meals' own default wins.
  const visits = quotes.map((quote) => {
    const choices = startChoices(quote);
    const chosen = times[quote.session];
    return {
      session: quote.session,
      meals: quote.meals,
      choices,
      start:
        chosen && choices.includes(chosen) ? chosen : quote.defaultStartTime,
      durationMinutes: quote.durationMinutes,
    };
  });

  const discount = monthly ? (plan?.monthlyDiscountPercent ?? 0) : 0;
  const perDay = quotes.reduce(
    (sum, q) => sum + discountedRate(q.rate, discount),
    0,
  );
  const spansSessions = quotes.length > 1;
  const onceSession = quotes.length === 1 ? quotes[0].session : null;
  const onceSlotId =
    onceSlot && onceSlot.session === onceSession ? onceSlot.id : null;
  const onceSlotFilter = useCallback(
    (startTime: string) =>
      onceSession !== null &&
      sessionContains(onceSession, startTime.slice(0, 5)),
    [onceSession],
  );
  // Fetched as soon as the meals fit one visit, so "Just once" opens ready.
  const onceSlots = useSlotDays(
    service.id,
    onceSession ? (locality?.id ?? null) : null,
    onceSlotFilter,
  );
  // Picked for the customer, like monthly's start time: the first open day,
  // and the window the meals' usual start time falls in.
  const onceDay =
    onceSlots.days.find((d) => d.date === onceDate && isOpenDay(d)) ??
    onceSlots.days.find(isOpenDay) ??
    null;
  const onceWindow =
    onceDay && onceSession
      ? (onceDay.slots.find((s) => s.slotWindowId === onceSlotId) ??
        closestWindow(onceDay.slots, quotes[0].defaultStartTime))
      : null;
  const estimate = monthlyEstimate(perDay, days.length);

  const toggleMeal = (meal: MealType) =>
    setMeals((current) =>
      current.includes(meal)
        ? current.filter((m) => m !== meal)
        : [...current, meal],
    );

  const keepSession = (session: MealSession) =>
    setMeals((current) => current.filter((m) => sessionOf(m) === session));

  const toggleDay = (day: number) =>
    setDays((current) =>
      current.includes(day)
        ? current.filter((d) => d !== day)
        : [...current, day],
    );

  const noMeals = quotes.length === 0;
  // Monthly reviews what and when on its own summary page (address, how to
  // pay, confirm), as a one-time visit does at checkout.
  const timeFor = (session: MealSession) =>
    visits.find((v) => v.session === session)?.start ?? "";
  const monthlyHref = `/monthly-service/summary?serviceSlug=${service.slug}&meals=${mealsToParam(meals)}&persons=${persons}&days=${days.join(",")}&start=${startDate}&morning=${timeFor("morning")}&evening=${timeFor("evening")}${
    initial.provider
      ? `&provider=${initial.provider.id}&providerName=${encodeURIComponent(initial.provider.name)}`
      : ""
  }`;
  const checkoutHref = `/booking/summary?serviceSlug=${service.slug}&meals=${mealsToParam(meals)}&persons=${persons}&date=${onceDay?.date ?? ""}&slot=${onceWindow?.slotWindowId ?? ""}&wallet=1`;

  return (
    <div className="mx-auto w-full max-w-3xl px-4 pb-44 pt-10 sm:px-6 sm:pt-14">
      <div className="flex flex-col gap-12">
        <section
          aria-labelledby="persons-heading"
          className="flex flex-wrap items-center justify-between gap-6"
        >
          <div className="flex flex-col gap-1">
            <h2
              id="persons-heading"
              className="text-xl font-semibold tracking-tight text-fg"
            >
              How many people are eating?
            </h2>
            <p className="text-sm text-fg-muted">
              Prices include cooking for up to 2. Each extra person adds a
              little.
            </p>
          </div>
          <PersonStepper value={persons} onChange={setPersons} />
        </section>

        <section
          aria-labelledby="meals-heading"
          className="flex flex-col gap-4"
        >
          <div className="flex flex-col gap-1">
            <h2
              id="meals-heading"
              className="text-xl font-semibold tracking-tight text-fg"
            >
              Which meals should we cook?
            </h2>
            <p className="text-sm text-fg-muted">
              Breakfast and lunch are cooked in the morning, snacks and dinner
              in the evening.
            </p>
          </div>
          <MealPicker
            options={options}
            persons={persons}
            selected={meals}
            onToggle={toggleMeal}
          />
        </section>

        <section
          aria-labelledby="often-heading"
          className="flex flex-col gap-5"
        >
          <h2
            id="often-heading"
            className="text-xl font-semibold tracking-tight text-fg"
          >
            How often?
          </h2>

          {plansQuery.isPending ? (
            // Whether a monthly plan exists decides which choices show; wait
            // for it rather than flashing "just once" and swapping.
            <div
              aria-hidden
              className="h-[4.25rem] animate-pulse rounded-2xl bg-surface-2"
            />
          ) : null}

          {plan ? (
            <SegmentedChoice
              label="How often"
              layoutId="meal-mode-pill"
              value={mode}
              onChange={setChosenMode}
              options={[
                {
                  value: "monthly",
                  label: "Monthly",
                  hint: "Same cook on your days",
                },
                { value: "once", label: "Just once", hint: "A single visit" },
              ]}
            />
          ) : null}

          {plansQuery.isPending ? null : (
            <AnimatePresence mode="wait" initial={false}>
              {mode === "monthly" && plan ? (
                <motion.div
                  key="monthly"
                  initial={{ opacity: 0, y: 8 }}
                  animate={{ opacity: 1, y: 0 }}
                  exit={{ opacity: 0, y: -8 }}
                  transition={{ duration: 0.3, ease: EASE }}
                  className="flex flex-col gap-6"
                >
                  <div className="flex flex-col gap-2">
                    <span className="text-sm text-fg-muted">Cooking days</span>
                    <div
                      className="grid grid-cols-7 gap-1.5 sm:flex sm:gap-2"
                      role="group"
                      aria-label="Cooking days"
                    >
                      {WEEKDAYS.map((day) => {
                        const on = days.includes(day.value);
                        return (
                          <button
                            key={day.value}
                            type="button"
                            aria-pressed={on}
                            aria-label={day.long}
                            onClick={() => toggleDay(day.value)}
                            className={cx(
                              "h-11 rounded-xl text-sm sm:w-12",
                              chipClass(on),
                            )}
                          >
                            {day.short}
                          </button>
                        );
                      })}
                    </div>
                  </div>

                  {visits.map((visit) => (
                    <ChoiceChips
                      key={visit.session}
                      label={`${visit.session === "morning" ? "Morning" : "Evening"} visit · ${describeMeals(visit.meals)}, starts at`}
                      options={visit.choices.map((time) => ({
                        value: time,
                        label: formatClock(time),
                      }))}
                      note={`Your cook is there ${formatClock(visit.start)} – ${formatClock(
                        fromMinutes(
                          toMinutes(visit.start) + visit.durationMinutes,
                        ),
                      )}, about ${formatDuration(visit.durationMinutes)}.`}
                      value={visit.start}
                      onChange={(next) =>
                        setTimes((current) => ({
                          ...current,
                          [visit.session]: next,
                        }))
                      }
                    />
                  ))}

                  <DateChips
                    label="First day"
                    dates={upcomingDates}
                    isOpen={() => true}
                    value={startDate}
                    onChange={setStartDate}
                  />

                  <p className="text-sm text-fg-muted">
                    {initial.provider
                      ? `We'll ask ${initial.provider.name} first, the cook you had before.`
                      : "The same cook comes on your days."}{" "}
                    You’ll choose the address and how to pay next.
                  </p>
                </motion.div>
              ) : (
                <motion.div
                  key="once"
                  initial={{ opacity: 0, y: 8 }}
                  animate={{ opacity: 1, y: 0 }}
                  exit={{ opacity: 0, y: -8 }}
                  transition={{ duration: 0.3, ease: EASE }}
                  className="flex flex-col gap-6"
                >
                  {spansSessions ? (
                    <Alert tone="info" title="One booking is one visit">
                      <span className="flex flex-col gap-3">
                        Keep the morning meals or the evening ones. For both,
                        choose Monthly.
                        <span className="flex flex-wrap gap-2">
                          <Button
                            size="sm"
                            variant="secondary"
                            onClick={() => keepSession("morning")}
                          >
                            Keep morning meals
                          </Button>
                          <Button
                            size="sm"
                            variant="secondary"
                            onClick={() => keepSession("evening")}
                          >
                            Keep evening meals
                          </Button>
                        </span>
                      </span>
                    </Alert>
                  ) : onceSession ? (
                    <AreaGate>
                      {() => (
                        <OnceVisit
                          slots={onceSlots}
                          day={onceDay}
                          window={onceWindow}
                          session={onceSession}
                          meals={quotes[0].meals}
                          durationMinutes={quotes[0].durationMinutes}
                          onDay={(date) => {
                            setOnceDate(date);
                            setOnceSlot(null);
                          }}
                          onWindow={(id) =>
                            setOnceSlot({ id, session: onceSession })
                          }
                        />
                      )}
                    </AreaGate>
                  ) : null}
                </motion.div>
              )}
            </AnimatePresence>
          )}
        </section>

        <IncludedDetails
          inclusions={service.inclusions}
          exclusions={service.exclusions}
        />
      </div>

      <QuoteBar>
        <div className="min-w-0">
          {noMeals || (spansSessions && !(mode === "monthly" && plan)) ? (
            <p className="text-sm text-fg-muted">
              {noMeals
                ? "Choose at least one meal"
                : "Choose morning or evening meals"}
            </p>
          ) : monthly ? (
            <>
              <p className="flex items-baseline gap-1.5">
                <AnimatedRupees
                  value={perDay}
                  className="text-2xl font-semibold text-fg"
                  live
                />
                <span className="text-sm text-fg-muted">a day</span>
                {discount > 0 ? (
                  <span className="rounded-full bg-success-soft px-2 py-0.5 text-xs font-medium text-success">
                    {discount}% off
                  </span>
                ) : null}
              </p>
              <p className="truncate text-xs text-fg-muted">
                {persons > 1 ? (
                  <>
                    <AnimatedRupees value={perDay / persons} /> per person ·
                    about{" "}
                  </>
                ) : (
                  "About "
                )}
                <AnimatedRupees value={estimate} /> a month
              </p>
            </>
          ) : (
            <>
              <p className="flex items-baseline gap-1.5">
                <AnimatedRupees
                  value={quotes[0].rate}
                  className="text-2xl font-semibold text-fg"
                  live
                />
                <span className="text-sm text-fg-muted">for the visit</span>
              </p>
              <p className="truncate text-xs text-fg-muted">
                {persons > 1 ? (
                  <>
                    <AnimatedRupees value={quotes[0].rate / persons} /> per
                    person ·{" "}
                  </>
                ) : null}
                {persons > 1
                  ? "taxes and fees at checkout"
                  : "Taxes and fees at checkout"}
              </p>
            </>
          )}
        </div>

        {isUnserviceable ? (
          <Button size="lg" disabled>
            Not available in your area
          </Button>
        ) : mode === "monthly" && plan ? (
          noMeals || days.length === 0 ? (
            <Button size="lg" disabled>
              Continue
            </Button>
          ) : (
            <LinkButton size="lg" href={monthlyHref}>
              Continue
            </LinkButton>
          )
        ) : noMeals || spansSessions || !onceWindow ? (
          <Button size="lg" disabled>
            Pick a time
          </Button>
        ) : (
          <LinkButton size="lg" href={checkoutHref}>
            Continue
          </LinkButton>
        )}
      </QuoteBar>
    </div>
  );
}

/** A one-time visit's day and time window, laid out like the monthly schedule. */
function OnceVisit({
  slots,
  day,
  window,
  session,
  meals,
  durationMinutes,
  onDay,
  onWindow,
}: {
  slots: ReturnType<typeof useSlotDays>;
  day: SlotDayAvailability | null;
  window: SlotOption | null;
  session: MealSession;
  meals: MealType[];
  durationMinutes: number;
  onDay: (date: string) => void;
  onWindow: (slotWindowId: string) => void;
}) {
  if (slots.query.isError) {
    return (
      <Alert
        tone="error"
        title="Couldn't load visit times"
        action={
          <Button
            size="sm"
            variant="secondary"
            onClick={() => slots.query.refetch()}
          >
            Retry
          </Button>
        }
      >
        {describeError(slots.query.error)}
      </Alert>
    );
  }
  if (slots.query.isPending) {
    return (
      <div aria-hidden className="flex flex-col gap-6">
        <Skeleton className="h-14 rounded-xl" />
        <Skeleton className="h-9 w-64 rounded-full" />
      </div>
    );
  }
  if (!day) {
    return (
      <p className="text-sm text-fg-muted">
        No {session} visits are open this week. Try the{" "}
        {session === "morning" ? "evening" : "morning"} meals, or Monthly.
      </p>
    );
  }
  const open = new Set(slots.days.filter(isOpenDay).map((d) => d.date));
  return (
    <>
      <DateChips
        label="Day"
        dates={slots.dates}
        isOpen={(date) => open.has(date)}
        value={day.date}
        onChange={onDay}
      />
      <ChoiceChips
        label={`${session === "morning" ? "Morning" : "Evening"} visit · ${describeMeals(meals)}, starts between`}
        options={day.slots.map((slot) => ({
          value: slot.slotWindowId,
          label: `${formatClock(slot.startTime.slice(0, 5))} – ${formatClock(slot.endTime.slice(0, 5))}`,
        }))}
        value={window?.slotWindowId ?? null}
        onChange={onWindow}
      />
      <p className="text-sm text-fg-muted">
        Your cook arrives in this window and cooks for about{" "}
        {formatDuration(durationMinutes)}. You’ll choose the address and pay at
        checkout.
      </p>
    </>
  );
}

/**
 * Fixed to the bottom on every size: the price and the one next step stay in
 * reach through the whole flow. Portalled to <body> because the page's
 * entrance animation leaves a transform on <main>, which would otherwise pin
 * a `fixed` child to the bottom of the page instead of the screen.
 */
function QuoteBar({ children }: { children: ReactNode }) {
  const mounted = useSyncExternalStore(
    () => () => undefined,
    () => true,
    () => false,
  );
  if (!mounted) return null;
  return createPortal(
    <motion.div
      initial={{ y: 24, opacity: 0 }}
      animate={{ y: 0, opacity: 1 }}
      transition={{ duration: 0.4, ease: EASE }}
      className="fixed inset-x-0 bottom-0 z-40 border-t border-line bg-surface/95 backdrop-blur supports-[padding:max(0px)]:pb-[env(safe-area-inset-bottom)]"
    >
      <div className="mx-auto flex w-full max-w-3xl items-center justify-between gap-4 px-4 py-3 sm:px-6">
        {children}
      </div>
    </motion.div>,
    document.body,
  );
}

function IncludedDetails({
  inclusions,
  exclusions,
}: {
  inclusions: string;
  exclusions: string;
}) {
  const lines = (body: string) =>
    body
      .split("\n")
      .map((l) => l.trim())
      .filter(Boolean);
  if (!inclusions && !exclusions) return null;
  return (
    <details className="group rounded-2xl border border-line bg-surface">
      <summary className="flex cursor-pointer list-none items-center justify-between px-5 py-4 text-sm font-medium text-fg">
        What the cook does, and doesn’t
        <svg
          viewBox="0 0 24 24"
          className="h-4 w-4 text-fg-subtle transition-transform duration-fast group-open:rotate-180"
          fill="none"
          stroke="currentColor"
          strokeWidth="2"
          aria-hidden
        >
          <path d="m6 9 6 6 6-6" />
        </svg>
      </summary>
      <div className="grid gap-6 border-t border-line px-5 py-4 text-sm sm:grid-cols-2">
        <ul className="flex flex-col gap-2 text-fg-muted">
          {lines(inclusions).map((line) => (
            <li key={line} className="flex gap-2">
              <span aria-hidden className="text-success">
                ✓
              </span>
              {line}
            </li>
          ))}
        </ul>
        <ul className="flex flex-col gap-2 text-fg-muted">
          {lines(exclusions).map((line) => (
            <li key={line} className="flex gap-2">
              <span aria-hidden className="text-fg-subtle">
                ✕
              </span>
              {line}
            </li>
          ))}
        </ul>
      </div>
    </details>
  );
}
