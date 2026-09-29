"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useRouter } from "next/navigation";
import { useEffect, useMemo, useRef, useState } from "react";
import { BannerBreadcrumb, STICKY_BAR_SPACER, inr } from "@/components/patterns";
import { Reveal, RevealItem } from "@/components/motion";
import { PageBanner } from "@/components/PageBanner";
import { RequireAuth } from "@/components/RequireAuth";
import {
  Alert,
  Button,
  Card,
  EmptyState,
  Field,
  LinkButton,
  Select,
  Skeleton,
  Textarea,
  cx,
  useToast,
} from "@/components/ui";
import { useSelectedCity } from "@/hooks/useSelectedCity";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import { isoDateOffsetFromToday, todayIsoDate } from "@/lib/date";
import {
  MAX_MONTH_DATE,
  MonthlyServiceFrequency,
  WEEKDAYS,
  describeFrequency,
  browseMonthlyPlans,
  describeVisit,
  requestMonthlyService,
} from "@/lib/monthly-service";
import type { MonthlyServicePlan } from "@/lib/monthly-service";
import type { CustomerAddress } from "@/lib/types";
import { MY_MONTHLY_SERVICES_KEY } from "../_components/shared";

/** Average weeks in a month - only for the "about Rs X a month" estimate, never for billing. */
const WEEKS_PER_MONTH = 4.33;
const MON_TO_SAT = [1, 2, 3, 4, 5, 6];

/** Sensible starting picks for "N times a week" - spread out, not bunched. */
const EVEN_SPREAD_WEEKDAYS: Record<number, number[]> = {
  1: [0],
  2: [3, 0],
  3: [1, 3, 5],
  4: [1, 3, 5, 0],
  5: [1, 2, 3, 4, 5],
  6: MON_TO_SAT,
  7: [1, 2, 3, 4, 5, 6, 0],
};

/** N dates spread across 1-28, e.g. 4 -> 1, 8, 15, 22. */
function evenMonthDates(count: number): number[] {
  const step = 28 / count;
  return Array.from({ length: count }, (_, i) => Math.floor(i * step) + 1);
}

/**
 * Choose a monthly plan and request it (docs/MONTHLY-SERVICE.md "HOW IT
 * WORKS"): address, days, visit time and start date. Nothing is charged
 * now - the request goes to the operations team, who assign a professional;
 * billing happens at month end for the visits that actually happened.
 */
export default function NewMonthlyServicePage() {
  return (
    <RequireAuth>
      <NewMonthlyServiceScreen />
    </RequireAuth>
  );
}

function NewMonthlyServiceScreen() {
  const router = useRouter();
  const toast = useToast();
  const queryClient = useQueryClient();
  const { city } = useSelectedCity();
  const formRef = useRef<HTMLDivElement>(null);

  const [plan, setPlan] = useState<MonthlyServicePlan | null>(null);
  const [addressId, setAddressId] = useState<string | null>(null);
  const [days, setDays] = useState<number[]>(MON_TO_SAT);
  const [monthDates, setMonthDates] = useState<number[]>([]);
  const [visitTime, setVisitTime] = useState("08:00");
  const [startDate, setStartDate] = useState(isoDateOffsetFromToday(1));
  const [note, setNote] = useState("");
  const [formError, setFormError] = useState<string | null>(null);

  const plansQuery = useQuery({
    queryKey: ["monthly-plans", city?.id ?? "all"],
    queryFn: () => browseMonthlyPlans(city?.id),
    enabled: city !== undefined,
  });

  const addressesQuery = useQuery({
    queryKey: ["addresses"],
    queryFn: () => apiFetch<CustomerAddress[]>(`${API_V1}/addresses`, { authenticated: true }),
  });

  useEffect(() => {
    if (addressId !== null || !addressesQuery.data) return;
    const preferred = addressesQuery.data.find((a) => a.isDefault) ?? addressesQuery.data[0];
    if (preferred) setAddressId(preferred.id);
  }, [addressesQuery.data, addressId]);

  const requestMutation = useMutation({
    mutationFn: requestMonthlyService,
    onSuccess: (created) => {
      queryClient.invalidateQueries({ queryKey: MY_MONTHLY_SERVICES_KEY });
      toast("success", "Request received. We'll assign your professional shortly.");
      router.push(`/monthly-service/${created.id}`);
    },
  });

  const perMonth = plan?.frequency === MonthlyServiceFrequency.TimesPerMonth;
  const perWeek = plan?.frequency === MonthlyServiceFrequency.TimesPerWeek;
  const required = plan?.timesPerPeriod ?? 0;

  const monthlyEstimate = useMemo(() => {
    if (!plan) return 0;
    const visits = plan.frequency === MonthlyServiceFrequency.TimesPerMonth ? (plan.timesPerPeriod ?? 0) : days.length * WEEKS_PER_MONTH;
    return Math.round(plan.ratePerVisit * visits);
  }, [plan, days.length]);

  const choosePlan = (next: MonthlyServicePlan) => {
    setPlan(next);
    // Start each plan from a valid-looking default rather than a leftover
    // selection that no longer fits its "N times" rule.
    if (next.frequency === MonthlyServiceFrequency.TimesPerWeek) {
      setDays(EVEN_SPREAD_WEEKDAYS[next.timesPerPeriod ?? 1] ?? MON_TO_SAT.slice(0, next.timesPerPeriod ?? 1));
    } else if (next.frequency === MonthlyServiceFrequency.Weekdays) {
      setDays(MON_TO_SAT);
    }
    setMonthDates(next.frequency === MonthlyServiceFrequency.TimesPerMonth ? evenMonthDates(next.timesPerPeriod ?? 1) : []);
    setFormError(null);
    requestMutation.reset();
    requestAnimationFrame(() => formRef.current?.scrollIntoView({ behavior: "smooth", block: "start" }));
  };

  const toggleDay = (value: number) =>
    setDays((prev) => {
      if (prev.includes(value)) return prev.filter((d) => d !== value);
      // A fixed-count plan swaps out the oldest pick instead of going over.
      if (perWeek && prev.length >= required) return [...prev.slice(1), value];
      return [...prev, value];
    });

  const toggleDate = (value: number) =>
    setMonthDates((prev) => {
      if (prev.includes(value)) return prev.filter((d) => d !== value);
      if (prev.length >= required) return [...prev.slice(1), value].sort((a, b) => a - b);
      return [...prev, value].sort((a, b) => a - b);
    });

  const submit = () => {
    if (!plan || requestMutation.isPending) return;
    if (!addressId) return setFormError("Choose the address the professional should come to.");
    if (perMonth && monthDates.length !== required) return setFormError(`Choose exactly ${required} date(s) of the month.`);
    if (!perMonth && days.length === 0) return setFormError("Choose at least one day.");
    if (perWeek && days.length !== required) return setFormError(`Choose exactly ${required} day(s) of the week.`);
    if (!/^\d{2}:\d{2}$/.test(visitTime)) return setFormError("Choose a visit time.");
    if (startDate < todayIsoDate()) return setFormError("Start date cannot be in the past.");
    setFormError(null);
    requestMutation.mutate({
      planId: plan.id,
      addressId,
      days: perMonth ? [] : days,
      monthDates: perMonth ? monthDates : undefined,
      visitStartTime: visitTime,
      startDate,
      endDate: null,
      note: note.trim() || null,
    });
  };

  return (
    <main className="flex w-full flex-col">
      <PageBanner
        title="Monthly service plans"
        description="Pick a plan, choose your days and time. Pay at month end, only for visits that happened."
        breadcrumb={
          <BannerBreadcrumb
            items={[{ label: "Home", href: "/" }, { label: "Monthly services", href: "/monthly-service" }, { label: "Plans" }]}
          />
        }
      />

      <div className={cx("mx-auto flex w-full max-w-7xl flex-col gap-8 px-4 py-10 sm:px-6 sm:py-14", plan && STICKY_BAR_SPACER)}>
        <section aria-labelledby="plans-heading">
          <h2 id="plans-heading" className="mb-4 text-base font-semibold text-fg">
            1. Choose a plan{city ? ` in ${city.name}` : ""}
          </h2>
          {plansQuery.isPending ? (
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3" aria-hidden>
              {[0, 1, 2].map((card) => (
                <Skeleton key={card} className="h-64 rounded-2xl" />
              ))}
            </div>
          ) : plansQuery.isError ? (
            <Alert
              tone="error"
              title="Couldn't load plans"
              action={
                <Button size="sm" variant="secondary" onClick={() => plansQuery.refetch()}>
                  Retry
                </Button>
              }
            >
              {describeError(plansQuery.error)}
            </Alert>
          ) : plansQuery.data.length === 0 ? (
            <EmptyState
              title="No monthly plans here yet"
              description="Monthly services aren't open in your city yet — check back soon."
            />
          ) : (
            <Reveal as="ul" className="grid list-none grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
              {plansQuery.data.map((option) => (
                <RevealItem key={option.id} className="flex">
                  <PlanCard plan={option} selected={plan?.id === option.id} onChoose={() => choosePlan(option)} />
                </RevealItem>
              ))}
            </Reveal>
          )}
        </section>

        {plan ? (
          <div ref={formRef} className="flex scroll-mt-24 flex-col gap-6">
            <h2 className="text-base font-semibold text-fg">2. Your schedule</h2>

            <Card title="Where" description="The professional comes to this address every visit.">
              {addressesQuery.isPending ? (
                <Skeleton className="h-10 rounded-lg" />
              ) : addressesQuery.isError ? (
                <Alert tone="error">{describeError(addressesQuery.error)}</Alert>
              ) : addressesQuery.data.length === 0 ? (
                <EmptyState
                  title="No saved addresses"
                  description="Add your home address first."
                  action={<LinkButton href="/addresses/new">Add an address</LinkButton>}
                />
              ) : (
                <Select
                  label="Address"
                  value={addressId ?? ""}
                  onChange={(e) => setAddressId(e.target.value)}
                  options={addressesQuery.data.map((address) => ({
                    value: address.id,
                    label: `${address.label} — ${address.line1}, ${address.city} ${address.pincode}`,
                  }))}
                />
              )}
            </Card>

            {perMonth ? (
            <Card
              title="Which dates"
              description={`Choose ${required} date${required === 1 ? "" : "s"} of the month — ${monthDates.length} of ${required} chosen. Dates run 1–28 so every month has them.`}
            >
              <div className="grid grid-cols-7 gap-1.5" role="group" aria-label="Visit dates">
                {Array.from({ length: MAX_MONTH_DATE }, (_, i) => i + 1).map((date) => {
                  const on = monthDates.includes(date);
                  return (
                    <button
                      key={date}
                      type="button"
                      aria-pressed={on}
                      aria-label={`${date} of every month`}
                      onClick={() => toggleDate(date)}
                      className={cx(
                        "nums h-11 rounded-xl border text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand-500",
                        on
                          ? "border-brand-600 bg-brand-600 text-white"
                          : "border-line bg-surface text-fg-muted hover:border-brand-400 hover:text-fg",
                      )}
                    >
                      {date}
                    </button>
                  );
                })}
              </div>
            </Card>
            ) : (
            <Card
              title="Which days"
              description={
                perWeek
                  ? `Choose ${required} day${required === 1 ? "" : "s"} of the week — ${days.length} of ${required} chosen.`
                  : "Tap to add or remove a day. All seven means every day."
              }
            >
              <div className="flex flex-wrap gap-2" role="group" aria-label="Visit days">
                {WEEKDAYS.map((day) => {
                  const on = days.includes(day.value);
                  return (
                    <button
                      key={day.value}
                      type="button"
                      aria-pressed={on}
                      onClick={() => toggleDay(day.value)}
                      className={cx(
                        "h-11 min-w-[3.25rem] rounded-xl border px-3 text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand-500",
                        on
                          ? "border-brand-600 bg-brand-600 text-white"
                          : "border-line bg-surface text-fg-muted hover:border-brand-400 hover:text-fg",
                      )}
                    >
                      <span aria-hidden>{day.short}</span>
                      <span className="sr-only">{day.long}</span>
                    </button>
                  );
                })}
              </div>
              {perWeek ? null : (
              <div className="mt-3 flex flex-wrap gap-2 text-xs">
                <button type="button" className="text-brand-600 hover:underline dark:text-brand-400" onClick={() => setDays(MON_TO_SAT)}>
                  Mon – Sat
                </button>
                <span className="text-fg-subtle">·</span>
                <button
                  type="button"
                  className="text-brand-600 hover:underline dark:text-brand-400"
                  onClick={() => setDays(WEEKDAYS.map((d) => d.value))}
                >
                  Every day
                </button>
              </div>
              )}
            </Card>
            )}

            <Card title="When">
              <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <Field
                  label="Visit time"
                  type="time"
                  step={900}
                  value={visitTime}
                  hint="Same time on every visit day."
                  onChange={(e) => setVisitTime(e.target.value)}
                />
                <Field
                  label="Start from"
                  type="date"
                  min={todayIsoDate()}
                  value={startDate}
                  onChange={(e) => setStartDate(e.target.value)}
                />
              </div>
            </Card>

            <Card title="Anything we should know?">
              <Textarea
                label="Note for the professional (optional)"
                placeholder="e.g. Ring the bell twice, pet at home"
                maxLength={500}
                value={note}
                onChange={(e) => setNote(e.target.value)}
              />
            </Card>

            {formError ? <Alert tone="error">{formError}</Alert> : null}
            {requestMutation.isError ? <Alert tone="error">{describeError(requestMutation.error)}</Alert> : null}

            <div className="fixed inset-x-0 bottom-0 z-30 border-t border-line bg-surface/95 px-4 py-3 backdrop-blur md:static md:rounded-2xl md:border md:px-6 md:py-4">
              <div className="mx-auto flex w-full max-w-7xl flex-wrap items-center justify-between gap-3">
                <div className="min-w-0">
                  <p className="text-xs text-fg-muted">
                    {plan.name} ·{" "}
                    {perMonth ? `${monthDates.length} visit${monthDates.length === 1 ? "" : "s"} a month` : `${days.length} day${days.length === 1 ? "" : "s"} a week`}
                  </p>
                  <p className="nums text-base font-semibold text-fg">
                    about {inr(monthlyEstimate)}
                    <span className="text-sm font-normal text-fg-muted"> / month · billed for actual visits</span>
                  </p>
                </div>
                <Button type="button" size="lg" loading={requestMutation.isPending} onClick={submit}>
                  Request this service
                </Button>
              </div>
            </div>
          </div>
        ) : null}
      </div>
    </main>
  );
}

function PlanCard({ plan, selected, onChoose }: { plan: MonthlyServicePlan; selected: boolean; onChoose: () => void }) {
  return (
    <Card className={cx("flex flex-col transition-shadow", selected && "ring-2 ring-brand-500")}>
      <div className="flex flex-1 flex-col">
        <p className="text-xs font-medium uppercase tracking-wide text-fg-subtle">
          {plan.serviceName} · {plan.cityName}
        </p>
        <h3 className="mt-1 text-base font-semibold text-fg">{plan.name}</h3>
        {plan.description ? <p className="mt-1 text-sm text-fg-muted">{plan.description}</p> : null}
        <p className="nums mt-3 text-display-sm font-semibold text-fg">
          {inr(plan.ratePerVisit)}
          <span className="text-sm font-normal text-fg-muted"> / visit</span>
        </p>

        <ul className="mt-4 flex flex-1 flex-col gap-2">
          {describeFrequency(plan) ? <PlanPoint>{describeFrequency(plan)}</PlanPoint> : null}
          <PlanPoint>{describeVisit(plan)}</PlanPoint>
          {plan.includedTasks.map((task) => (
            <PlanPoint key={task}>{task}</PlanPoint>
          ))}
          <PlanPoint>Same professional every visit</PlanPoint>
          <PlanPoint>No charge for skipped or missed days</PlanPoint>
        </ul>

        <Button type="button" fullWidth className="mt-6" variant={selected ? "secondary" : "primary"} onClick={onChoose}>
          {selected ? "Selected" : "Choose"}
          <span className="sr-only"> {plan.name}</span>
        </Button>
      </div>
    </Card>
  );
}

function PlanPoint({ children }: { children: React.ReactNode }) {
  return (
    <li className="flex items-start gap-2 text-sm text-fg-muted">
      <svg
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="2.5"
        strokeLinecap="round"
        strokeLinejoin="round"
        className="mt-0.5 h-4 w-4 shrink-0 text-success"
        aria-hidden
      >
        <path d="m5 13 4 4L19 7" />
      </svg>
      <span>{children}</span>
    </li>
  );
}
