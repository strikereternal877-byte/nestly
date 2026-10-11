"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { AddressPicker } from "@/components/AddressPicker";
import { preferredAddress, resolveAddress } from "@/lib/addresses";
import type { AddressPick } from "@/lib/addresses";
import { BookingHelpLink } from "@/components/BookingHelpLink";
import { PageBanner } from "@/components/PageBanner";
import {
  BannerBreadcrumb,
  PriceLine,
  STICKY_BAR_SPACER,
  ScreenSkeleton,
  StickyActionBar,
  formatCalendarDate,
  inr,
} from "@/components/patterns";
import { RequireAuth } from "@/components/RequireAuth";
import { SegmentedChoice } from "@/components/SegmentedChoice";
import { Alert, Button, Card, LinkButton, cx, useToast } from "@/components/ui";
import { useSelectedCity } from "@/hooks/useSelectedCity";
import { API_V1, apiFetch, describeError, errorCode } from "@/lib/api";
import {
  describeMeals,
  discountedRate,
  formatDuration,
  fromMinutes,
  mealsFromParam,
  monthlyEstimate,
  quoteMeals,
  rupees,
  sessionContains,
  startChoices,
  toMinutes,
} from "@/lib/meals";
import type { MealSession } from "@/lib/meals";
import {
  MonthlyServiceBillingMode,
  MonthlyServiceContractStatus,
  MonthlyServicePlanBasis,
  browseMonthlyPlans,
  describeDays,
  formatClock,
  listMyMonthlyServices,
  requestMonthlyMealService,
} from "@/lib/monthly-service";
import type { CategoryDetail, CustomerAddress, ServiceDetail } from "@/lib/types";
import { useWalletTopUpConfig } from "@/lib/wallet-topup";
import { MY_MONTHLY_SERVICES_KEY } from "../_components/shared";

type Billing = "wallet" | "month";

const LINK = "font-medium text-brand-600 underline-offset-4 hover:underline dark:text-brand-400";

/**
 * Review step of a monthly cook (docs/MEAL-SERVICE.md) - the monthly twin of
 * /booking/summary. The cook page sends what and when in the URL; here the
 * customer picks the address, settles how to pay and confirms. Nothing is
 * charged on confirm (a daily-wallet plan pays each day the night before, a
 * monthly bill comes at month end), so this is a review, not a payment step.
 * The server re-checks and re-prices everything; the numbers here are the
 * same rules as the cook page's (lib/meals.ts).
 *
 * Suspense: useSearchParams needs a boundary for the production build.
 */
export default function MonthlyCookSummaryPage() {
  return (
    <Suspense fallback={<SummarySkeleton />}>
      <RequireAuth>
        <SummaryScreen />
      </RequireAuth>
    </Suspense>
  );
}

function SummarySkeleton() {
  return (
    <main className="flex w-full flex-col" aria-hidden>
      <div className="listing-banner h-[13.5rem] w-full sm:h-[15.5rem]" />
      <ScreenSkeleton cards={3} className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14" />
    </main>
  );
}

function SummaryScreen() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const toast = useToast();
  const queryClient = useQueryClient();
  const { city, locality } = useSelectedCity();

  const serviceSlug = searchParams.get("serviceSlug");
  const meals = mealsFromParam(searchParams.get("meals"));
  const persons = Number(searchParams.get("persons"));
  const days = (searchParams.get("days") ?? "")
    .split(",")
    .filter(Boolean)
    .map(Number)
    .filter((d) => Number.isInteger(d) && d >= 0 && d <= 6);
  const startDate = searchParams.get("start") ?? "";
  const startTimes: Partial<Record<MealSession, string | null>> = {
    morning: searchParams.get("morning"),
    evening: searchParams.get("evening"),
  };

  const [billing, setBilling] = useState<Billing>(searchParams.get("billing") === "month" ? "month" : "wallet");
  // `newAddressId` comes back from the "add a new address" detour, `address`
  // from any other round trip off this page (adding money).
  const [addressPick, setAddressPick] = useState<AddressPick | null>(() => {
    const id = searchParams.get("newAddressId") ?? searchParams.get("address");
    return id ? { id, areaKey: null } : null;
  });

  const serviceQuery = useQuery({
    queryKey: ["service", serviceSlug],
    queryFn: () => apiFetch<ServiceDetail>(`${API_V1}/services/${serviceSlug}`),
    enabled: !!serviceSlug,
  });
  // Same key as the cook page, so the plan arrives from cache.
  const plansQuery = useQuery({
    queryKey: ["monthly-plans", city?.id ?? "all"],
    queryFn: () => browseMonthlyPlans(city?.id),
    enabled: city !== undefined,
  });
  const addressesQuery = useQuery({
    queryKey: ["addresses"],
    queryFn: () => apiFetch<CustomerAddress[]>(`${API_V1}/addresses`, { authenticated: true }),
  });
  const walletQuery = useQuery({
    queryKey: ["wallet-balance"],
    queryFn: () => apiFetch<{ balance: number }>(`${API_V1}/wallet/balance`, { authenticated: true }),
  });
  const topUpConfigQuery = useWalletTopUpConfig();
  const myServicesQuery = useQuery({ queryKey: MY_MONTHLY_SERVICES_KEY, queryFn: listMyMonthlyServices });
  // The same banner image as checkout (the service's category page banner).
  const categorySlug = serviceQuery.data?.categorySlug;
  const categoryQuery = useQuery({
    queryKey: ["category", categorySlug],
    queryFn: () => apiFetch<CategoryDetail>(`${API_V1}/categories/${categorySlug}`),
    enabled: !!categorySlug,
  });

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
      // replace: Back from the new service must not land on a confirm button again.
      router.replace(created.length === 1 ? `/monthly-service/${created[0].id}` : "/monthly-service");
    },
  });

  if (serviceQuery.isPending || plansQuery.isPending) return <SummarySkeleton />;

  if (serviceQuery.isError || plansQuery.isError) {
    return (
      <main className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6">
        <Alert
          tone="error"
          title="Couldn't load your cook's details"
          action={
            <Button
              size="sm"
              variant="secondary"
              onClick={() => {
                serviceQuery.refetch();
                plansQuery.refetch();
              }}
            >
              Retry
            </Button>
          }
        >
          {describeError(serviceQuery.error ?? plansQuery.error)}
        </Alert>
      </main>
    );
  }

  const service = serviceQuery.data;
  const plan =
    plansQuery.data.find((p) => p.serviceId === service.id && p.basis === MonthlyServicePlanBasis.MealBased) ?? null;
  const quotes = Number.isInteger(persons) && persons >= 1 ? quoteMeals(service.mealOptions ?? [], meals, persons) : [];

  if (!plan || quotes.length === 0 || days.length === 0 || !/^\d{4}-\d{2}-\d{2}$/.test(startDate)) {
    return (
      <main className="mx-auto w-full max-w-3xl px-4 py-10 sm:px-6">
        <Alert
          tone="warning"
          title={plan ? "Choose your cook's meals and days first" : "Monthly cooking isn't offered here"}
          action={
            <LinkButton size="sm" variant="secondary" href={`/services/${service.slug}`}>
              Back to {service.name}
            </LinkButton>
          }
        >
          {plan
            ? "Pick the meals, people and cooking days, then continue to review them here."
            : "Book a single visit instead, or switch your city at the top of the page."}
        </Alert>
      </main>
    );
  }

  const discount = plan.monthlyDiscountPercent;
  const visits = quotes.map((quote) => ({
    ...quote,
    // A time the cook page could not have offered falls back to the meals' own.
    start: startChoices(quote).find((t) => t === startTimes[quote.session]) ?? quote.defaultStartTime,
    monthlyRate: discountedRate(quote.rate, discount),
  }));
  const perDay = visits.reduce((sum, v) => sum + v.monthlyRate, 0);
  const estimate = monthlyEstimate(perDay, days.length);

  const balance = walletQuery.data?.balance;
  const daysCovered = balance === undefined || perDay <= 0 ? null : Math.floor(balance / perDay);
  const walletHint =
    balance === undefined
      ? "Balance loading"
      : daysCovered === 0
        ? `${rupees(balance)}, add money to start`
        : `${rupees(balance)} covers ${daysCovered} day${daysCovered === 1 ? "" : "s"}`;

  // A hint only - the server decides by the address's pincode. Free-text city
  // names can differ ("Jaipur, Rajasthan"), hence a loose match.
  const inPlanCity = (address: CustomerAddress) =>
    address.city.toLowerCase().includes(plan.cityName.toLowerCase());
  const addresses = addressesQuery.data ?? [];
  // Picked like checkout's (lib/addresses.ts): first an address in the area
  // chosen in the header - where the cook page checked the service is
  // available - then one in the plan's city, the server's own rule.
  const inSelectedArea = (address: CustomerAddress) =>
    !locality?.pincodeId || address.pincodeId === locality.pincodeId;
  const selectedAddress =
    resolveAddress(addresses, addressPick, locality?.pincodeId ?? null, inSelectedArea) ??
    preferredAddress(addresses, inPlanCity);

  // Every detour off this page comes back here with what was chosen so far.
  const here = new URLSearchParams(searchParams.toString());
  here.delete("newAddressId");
  here.set("billing", billing);
  if (selectedAddress) here.set("address", selectedAddress.id);
  const herePath = `/monthly-service/summary?${here.toString()}`;
  const addMoneyHref =
    topUpConfigQuery.data?.enabled === true
      ? `/wallet?addMoney=1&returnTo=${encodeURIComponent(herePath)}`
      : null;

  // The server allows one live cook per service, address and session
  // (MonthlyServiceCustomerService.IsDuplicate); find it first, so the
  // customer is sent to it instead of being refused. Same plan implies same
  // service, so this never flags what the server would accept.
  const clashes = visits.flatMap((visit) => {
    const existing = myServicesQuery.data?.find(
      (c) =>
        c.status !== MonthlyServiceContractStatus.Cancelled &&
        c.planId === plan.id &&
        c.address?.id === selectedAddress?.id &&
        sessionContains(visit.session, c.visitStartTime.slice(0, 5)),
    );
    return existing ? [{ session: visit.session, contract: existing }] : [];
  });

  const confirm = () => {
    if (!selectedAddress || clashes.length > 0) return;
    requestMutation.mutate({
      planId: plan.id,
      addressId: selectedAddress.id,
      days,
      startDate,
      endDate: null,
      note: null,
      meals: visits.flatMap((v) => v.meals),
      persons,
      morningVisitTime: visits.find((v) => v.session === "morning")?.start ?? null,
      eveningVisitTime: visits.find((v) => v.session === "evening")?.start ?? null,
      payFromWallet: false,
      billingMode: billing === "wallet" ? MonthlyServiceBillingMode.DailyWallet : MonthlyServiceBillingMode.MonthEnd,
    });
  };

  const visitLabel = (session: MealSession) => (session === "morning" ? "Morning visit" : "Evening visit");
  const visitEnd = (start: string, minutes: number) => formatClock(fromMinutes(toMinutes(start) + minutes));

  // Laid out like /booking/summary (one-time checkout): Service, Service
  // address, then the schedule where checkout has its slot, and the same
  // price summary and sticky action - only what differs for monthly differs.
  return (
    <main className="flex w-full flex-col animate-rise">
      <PageBanner
        title="Review your booking"
        description={`${service.name}, monthly`}
        imageUrl={categoryQuery.data?.pageBannerUrl}
        breadcrumb={
          <BannerBreadcrumb
            items={[
              { label: "Home", href: "/" },
              { label: service.categoryName, href: `/categories/${service.categorySlug}` },
              { label: "Review your booking" },
            ]}
          />
        }
      />

      <div
        className={cx(
          "mx-auto grid w-full max-w-7xl gap-6 px-4 py-10 sm:px-6 sm:py-14 md:grid-cols-[1fr_22rem]",
          STICKY_BAR_SPACER,
        )}
      >
        <div className="flex min-w-0 flex-col gap-6 md:col-start-1">
          <Card title="Service">
            <div className="flex flex-col gap-4">
              {visits.map((visit) => (
                <div key={visit.session} className="flex items-baseline justify-between gap-4">
                  <span className="flex flex-col">
                    <span className="text-sm font-medium text-fg">
                      {service.name} · {visitLabel(visit.session).toLowerCase()}
                    </span>
                    <span className="text-sm text-fg-muted">
                      {describeMeals(visit.meals)} for {persons} {persons === 1 ? "person" : "people"}, about{" "}
                      {formatDuration(visit.durationMinutes)}
                    </span>
                  </span>
                  <span className="nums shrink-0 text-sm text-fg-muted">{inr(visit.rate)}</span>
                </div>
              ))}
            </div>
          </Card>

          <Card title="Service address" description="Where should your cook come?">
            <AddressPicker
              query={addressesQuery}
              selectedId={selectedAddress?.id ?? null}
              onSelect={(id) => setAddressPick({ id, areaKey: locality?.pincodeId ?? null })}
              isOutside={(address) => !inPlanCity(address)}
              areaName={plan.cityName}
              addHref={`/addresses/new?returnTo=${encodeURIComponent(herePath)}`}
            />
          </Card>

          <Card title="Schedule">
            <div className="flex items-center justify-between gap-4">
              <span className="flex flex-col">
                <span className="text-sm font-medium text-fg">
                  {describeDays(days)}, from {formatCalendarDate(startDate)}
                </span>
                {visits.map((visit) => (
                  <span key={visit.session} className="nums text-sm text-fg-muted">
                    {visitLabel(visit.session)}, {formatClock(visit.start)} – {visitEnd(visit.start, visit.durationMinutes)}
                  </span>
                ))}
              </span>
              <LinkButton size="sm" variant="secondary" href={`/services/${service.slug}`}>
                Change
              </LinkButton>
            </div>
          </Card>

          <Card title="How you’ll pay" description="Nothing is charged now.">
            <SegmentedChoice
              label="How you’ll pay"
              layoutId="monthly-summary-billing"
              value={billing}
              onChange={setBilling}
              options={[
                { value: "wallet", label: "Pay daily from wallet", hint: walletHint },
                { value: "month", label: "Monthly bill", hint: "Pay at month end" },
              ]}
            />
            <p className="mt-4 text-sm leading-relaxed text-fg-muted">
              {billing === "wallet"
                ? "Each day’s visit is paid from your wallet the night before. A day your wallet can’t pay for isn’t booked, and we tell you a day ahead when it’s running low. Skipped days and days your cook doesn’t come go back to your wallet."
                : "At the start of each month you get one bill for the days your cook came. You can have it paid from your wallet automatically."}{" "}
              {addMoneyHref ? (
                <Link href={addMoneyHref} className={LINK}>
                  Add money
                </Link>
              ) : null}
            </p>
            {billing === "wallet" && daysCovered === 0 ? (
              <div className="mt-4">
                <Alert tone="warning" title="Your wallet can’t pay for the first day yet">
                  Add at least {inr(perDay)} so your first visit is booked.
                </Alert>
              </div>
            ) : null}
          </Card>
        </div>

        <aside className="flex flex-col gap-4 md:sticky md:top-20 md:col-start-2 md:row-start-1 md:self-start">
          <Card title="Price summary">
            <dl className="flex flex-col gap-2.5">
              {visits.map((visit) => (
                <PriceLine
                  key={visit.session}
                  label={visitLabel(visit.session)}
                  value={inr(visit.rate)}
                  tone="strong"
                />
              ))}
              {discount > 0 ? (
                <PriceLine
                  label={`Monthly discount (${discount}%)`}
                  value={`−${inr(visits.reduce((sum, v) => sum + v.rate - v.monthlyRate, 0))}`}
                  tone="discount"
                />
              ) : null}
              <div className="my-1 border-t border-line-strong" />
              <PriceLine label="Per day" value={inr(perDay)} tone="total" />
              <PriceLine
                label={`About a month, ${days.length} day${days.length === 1 ? "" : "s"} a week`}
                value={rupees(estimate)}
              />
            </dl>
            <p className="mt-4 text-xs leading-relaxed text-fg-subtle">
              {billing === "wallet"
                ? "Taken from your wallet one day at a time, only for the days your cook comes."
                : "Billed at the start of next month, only for the days your cook came."}
            </p>
          </Card>

          {clashes.length > 0 ? (
            <Alert
              tone="warning"
              title={`You already have ${clashes.length > 1 ? "a morning and an evening" : `a ${clashes[0].session}`} cook at this address`}
            >
              <span className="flex flex-col gap-2">
                Open it to change its days or time, or choose another address.
                {clashes.map(({ session, contract }) => (
                  <Link key={session} href={`/monthly-service/${contract.id}`} className={LINK}>
                    Open your {session} cook
                  </Link>
                ))}
              </span>
            </Alert>
          ) : requestMutation.isError ? (
            <Alert tone="error" title="Couldn't request your cook">
              {describeError(requestMutation.error)}{" "}
              {errorCode(requestMutation.error) === "MonthlyService.DuplicateContract" ? (
                <Link href="/monthly-service" className={LINK}>
                  See your monthly services
                </Link>
              ) : null}
            </Alert>
          ) : null}

          <StickyActionBar>
            <div className="flex items-baseline justify-between gap-3 md:hidden">
              <span className="text-xs font-medium uppercase tracking-wide text-fg-muted">Per day</span>
              <span className="nums text-lg font-semibold text-fg">{inr(perDay)}</span>
            </div>
            <Button
              type="button"
              size="lg"
              fullWidth
              loading={requestMutation.isPending}
              disabled={!selectedAddress || clashes.length > 0 || requestMutation.isSuccess}
              onClick={confirm}
            >
              Request cook
            </Button>
          </StickyActionBar>

          <BookingHelpLink />
        </aside>
      </div>
    </main>
  );
}
