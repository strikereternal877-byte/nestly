"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AnimatePresence, motion } from "motion/react";
import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useMemo, useState, useSyncExternalStore } from "react";
import { createPortal } from "react-dom";
import type { ReactNode } from "react";
import { MY_MONTHLY_SERVICES_KEY } from "@/app/monthly-service/_components/shared";
import {
  AnimatedRupees,
  chipClass,
  DayStrip,
  MealPicker,
  PersonStepper,
  TimeChips,
  timeChoices,
} from "@/components/meals/MealParts";
import type { PlannedVisit } from "@/components/meals/MealParts";
import { SegmentedChoice } from "@/components/SegmentedChoice";
import {
  Alert,
  Button,
  Field,
  LinkButton,
  Select,
  cx,
  useToast,
} from "@/components/ui";
import { useSelectedCity } from "@/hooks/useSelectedCity";
import { useServiceability } from "@/hooks/useServiceability";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import { isAuthenticated, subscribeToAuthChanges } from "@/lib/auth";
import { isoDateOffsetFromToday } from "@/lib/date";
import {
  MealType,
  mealsToParam,
  quoteMeals,
  rupees,
  sessionOf,
} from "@/lib/meals";
import type { MealSession } from "@/lib/meals";
import {
  MonthlyServiceBillingMode,
  MonthlyServicePlanBasis,
  WEEKDAYS,
  browseMonthlyPlans,
  requestMonthlyMealService,
} from "@/lib/monthly-service";
import { buildLoginHref } from "@/lib/return-to";
import type { CustomerAddress, ServiceDetail } from "@/lib/types";

/** Average weeks in a month - only for the "about Rs X a month" line, never for billing. */
const WEEKS_PER_MONTH = 4.33;
const MON_TO_SAT = [1, 2, 3, 4, 5, 6];

type Mode = "monthly" | "once";
type Payment = "online" | "wallet";

const EASE = [0.16, 1, 0.3, 1] as const;

function useSignedIn(): boolean {
  return useSyncExternalStore(
    subscribeToAuthChanges,
    isAuthenticated,
    () => false,
  );
}

/**
 * Booking a cook (docs/MEAL-SERVICE.md): how many people, which meals, how
 * often. Everything the customer picks moves the price and the visit times
 * live; nothing is asked that does not change one of them. Monthly sends a
 * request straight from here; "just once" hands the selection to the normal
 * checkout for date, slot and payment.
 */
export function MealBookingFlow({ service }: { service: ServiceDetail }) {
  const options = useMemo(
    () => service.mealOptions ?? [],
    [service.mealOptions],
  );
  const router = useRouter();
  const pathname = usePathname();
  const toast = useToast();
  const queryClient = useQueryClient();
  const signedIn = useSignedIn();
  const { city } = useSelectedCity();
  const { isUnserviceable } = useServiceability(service.id);

  const [persons, setPersons] = useState(2);
  const [meals, setMeals] = useState<MealType[]>(() =>
    options.some((o) => o.meal === MealType.Lunch)
      ? [MealType.Lunch]
      : options.slice(0, 1).map((o) => o.meal),
  );
  const [chosenMode, setChosenMode] = useState<Mode | null>(null);
  const [days, setDays] = useState<number[]>(MON_TO_SAT);
  const [startDate, setStartDate] = useState(isoDateOffsetFromToday(1));
  const [times, setTimes] = useState<Partial<Record<MealSession, string>>>({});
  const [addressId, setAddressId] = useState<string | null>(null);
  const [chosenPayment, setChosenPayment] = useState<Payment | null>(null);

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
  // Monthly defaults to paying day by day from the wallet (a day is on once
  // it's paid); a one-time visit defaults to paying online at checkout.
  const payment: Payment = chosenPayment ?? (monthly ? "wallet" : "online");

  const addressesQuery = useQuery({
    queryKey: ["addresses"],
    queryFn: () =>
      apiFetch<CustomerAddress[]>(`${API_V1}/addresses`, {
        authenticated: true,
      }),
    enabled: signedIn && mode === "monthly",
  });

  // Same key as the wallet page and checkout, so all three share one cache entry.
  const walletQuery = useQuery({
    queryKey: ["wallet-balance"],
    queryFn: () =>
      apiFetch<{ balance: number }>(`${API_V1}/wallet/balance`, {
        authenticated: true,
      }),
    enabled: signedIn,
  });
  const balance = walletQuery.data?.balance;

  // The saved default (or first) address until the customer picks another.
  const effectiveAddressId =
    addressId ??
    addressesQuery.data?.find((a) => a.isDefault)?.id ??
    addressesQuery.data?.[0]?.id ??
    null;

  const quotes = useMemo(
    () => quoteMeals(options, meals, persons),
    [options, meals, persons],
  );

  // A chosen time survives meal changes only while it is still one of the
  // offered choices for its session; otherwise the meals' own default wins.
  const visits: PlannedVisit[] = quotes.map((quote) => {
    const choices = timeChoices(quote.session, quote.defaultStartTime);
    const chosen = times[quote.session];
    return {
      session: quote.session,
      meals: quote.meals,
      start:
        chosen && choices.includes(chosen) ? chosen : quote.defaultStartTime,
      durationMinutes: quote.durationMinutes,
    };
  });

  // Same rounding as the server's monthly discount (MonthlyServiceContract).
  const discount = monthly ? (plan?.monthlyDiscountPercent ?? 0) : 0;
  const perDay = quotes.reduce(
    (sum, q) => sum + Math.round(q.rate * (100 - discount)) / 100,
    0,
  );
  const spansSessions = quotes.length > 1;
  const monthlyEstimate = perDay * days.length * WEEKS_PER_MONTH;
  const daysCovered =
    balance === undefined || perDay <= 0 ? null : Math.floor(balance / perDay);
  const walletHint = !signedIn
    ? "Sign in to use it"
    : balance === undefined
      ? "Balance loading"
      : monthly && daysCovered !== null
        ? `${rupees(balance)} covers ${daysCovered} day${daysCovered === 1 ? "" : "s"}`
        : `Balance ${rupees(balance)}`;

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

  const requestMutation = useMutation({
    mutationFn: requestMonthlyMealService,
    onSuccess: (created) => {
      queryClient.invalidateQueries({ queryKey: MY_MONTHLY_SERVICES_KEY });
      toast(
        "success",
        created.length > 1
          ? "Morning and evening cook requested. We'll assign your cook shortly."
          : "Cook requested. We'll assign your cook shortly.",
      );
      router.push(
        created.length === 1
          ? `/monthly-service/${created[0].id}`
          : "/monthly-service",
      );
    },
  });

  const submitMonthly = () => {
    if (!plan || !effectiveAddressId) return;
    const timeFor = (session: MealSession) =>
      visits.find((v) => v.session === session)?.start ?? null;
    requestMutation.mutate({
      planId: plan.id,
      addressId: effectiveAddressId,
      days,
      startDate,
      endDate: null,
      note: null,
      meals,
      persons,
      morningVisitTime: timeFor("morning"),
      eveningVisitTime: timeFor("evening"),
      payFromWallet: false,
      billingMode:
        payment === "wallet"
          ? MonthlyServiceBillingMode.DailyWallet
          : MonthlyServiceBillingMode.MonthEnd,
    });
  };

  const noMeals = quotes.length === 0;
  const checkoutHref = `/booking/summary?serviceSlug=${service.slug}&meals=${mealsToParam(meals)}&persons=${persons}${
    payment === "wallet" ? "&wallet=1" : ""
  }`;

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
          <DayStrip visits={visits} />
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
                    <TimeChips
                      key={visit.session}
                      label={
                        visit.session === "morning"
                          ? "Morning visit starts at"
                          : "Evening visit starts at"
                      }
                      choices={timeChoices(
                        visit.session,
                        quotes.find((q) => q.session === visit.session)!
                          .defaultStartTime,
                      )}
                      value={visit.start}
                      onChange={(next) =>
                        setTimes((current) => ({
                          ...current,
                          [visit.session]: next,
                        }))
                      }
                    />
                  ))}

                  <div className="grid gap-4 sm:grid-cols-2">
                    <Field
                      label="First day"
                      type="date"
                      value={startDate}
                      min={isoDateOffsetFromToday(0)}
                      onChange={(event) => setStartDate(event.target.value)}
                    />
                    {signedIn ? (
                      addressesQuery.data && addressesQuery.data.length > 0 ? (
                        <Select
                          label="Cook at"
                          value={effectiveAddressId ?? ""}
                          onChange={(event) => setAddressId(event.target.value)}
                          options={addressesQuery.data.map((a) => ({
                            value: a.id,
                            label: `${a.label}, ${a.line1}`,
                          }))}
                        />
                      ) : addressesQuery.isSuccess ? (
                        <div className="flex flex-col justify-end gap-2">
                          <span className="text-sm text-fg-muted">Cook at</span>
                          <LinkButton href="/addresses/new" variant="secondary">
                            Add your address
                          </LinkButton>
                        </div>
                      ) : null
                    ) : null}
                  </div>

                  <p className="text-sm text-fg-muted">
                    The same cook comes on your days. You pay at the end of each
                    month, only for the days they cooked.
                  </p>
                </motion.div>
              ) : (
                <motion.div
                  key="once"
                  initial={{ opacity: 0, y: 8 }}
                  animate={{ opacity: 1, y: 0 }}
                  exit={{ opacity: 0, y: -8 }}
                  transition={{ duration: 0.3, ease: EASE }}
                  className="flex flex-col gap-4"
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
                  ) : (
                    <p className="text-sm text-fg-muted">
                      You’ll pick the date, time and address next.
                    </p>
                  )}
                </motion.div>
              )}
            </AnimatePresence>
          )}

          {requestMutation.isError ? (
            <Alert tone="error" title="Couldn't request your cook">
              {describeError(requestMutation.error)}
            </Alert>
          ) : null}
        </section>

        <section aria-labelledby="pay-heading" className="flex flex-col gap-4">
          <h2
            id="pay-heading"
            className="text-xl font-semibold tracking-tight text-fg"
          >
            How will you pay?
          </h2>
          <SegmentedChoice
            label="How will you pay"
            layoutId="meal-payment-pill"
            value={payment}
            onChange={setChosenPayment}
            options={
              monthly
                ? [
                    {
                      value: "wallet",
                      label: "Pay daily from wallet",
                      hint: walletHint,
                    },
                    {
                      value: "online",
                      label: "Monthly bill",
                      hint: "Pay at month end",
                    },
                  ]
                : [
                    {
                      value: "online",
                      label: "Pay online",
                      hint: "At checkout",
                    },
                    { value: "wallet", label: "Use wallet", hint: walletHint },
                  ]
            }
          />
          {payment === "wallet" ? (
            <p className="text-sm text-fg-muted">
              {monthly
                ? "Each day’s visit is paid from your wallet the night before. A day your wallet can’t pay for isn’t booked, and we let you know a day ahead when it’s running low. Skipped days and days your cook doesn’t come go back to your wallet."
                : "Your wallet balance is used at checkout. Anything it doesn’t cover is paid online."}{" "}
              {signedIn ? (
                <Link
                  href={`/wallet?addMoney=1&returnTo=${encodeURIComponent(pathname ?? "/")}`}
                  className="font-medium text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
                >
                  Add money
                </Link>
              ) : null}
            </p>
          ) : null}
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
                About <AnimatedRupees value={monthlyEstimate} /> a month
                <span className="hidden sm:inline">
                  {payment === "wallet"
                    ? ", paid daily from wallet"
                    : ", billed at month end"}
                </span>
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
                Taxes and fees shown at checkout
              </p>
            </>
          )}
        </div>

        {isUnserviceable ? (
          <Button size="lg" disabled>
            Not available in your area
          </Button>
        ) : mode === "monthly" && plan ? (
          signedIn ? (
            <Button
              size="lg"
              onClick={submitMonthly}
              loading={requestMutation.isPending}
              disabled={noMeals || days.length === 0 || !effectiveAddressId}
            >
              Request cook
            </Button>
          ) : (
            <LinkButton size="lg" href={buildLoginHref(pathname ?? "/")}>
              Sign in to continue
            </LinkButton>
          )
        ) : noMeals || spansSessions ? (
          <Button size="lg" disabled>
            Choose date and time
          </Button>
        ) : (
          <LinkButton size="lg" href={checkoutHref}>
            Choose date and time
          </LinkButton>
        )}
      </QuoteBar>
    </div>
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
