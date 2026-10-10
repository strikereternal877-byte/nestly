"use client";

import { useMutation, useQuery } from "@tanstack/react-query";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useEffect, useRef, useState } from "react";
import { CitySelector } from "@/components/CitySelector";
import { LocalitySelector } from "@/components/LocalitySelector";
import {
  BannerBreadcrumb,
  FrequencyPicker,
  STICKY_BAR_SPACER,
  ScreenSkeleton,
  StickyActionBar,
  inr,
} from "@/components/patterns";
import { PageBanner } from "@/components/PageBanner";
import { RequireAuth } from "@/components/RequireAuth";
import { SlotPicker } from "@/components/SlotPicker";
import {
  Alert,
  Button,
  Card,
  CheckboxField,
  EmptyState,
  Field,
  LinkButton,
  Select,
  Skeleton,
  cx,
} from "@/components/ui";
import { useSelectedCity } from "@/hooks/useSelectedCity";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import { todayIsoDate } from "@/lib/date";
import { DAY_OF_WEEK_LABELS, RecurringBookingRecurrenceFrequency } from "@/lib/types";
import type {
  CreateRecurringBookingPlanRequestBody,
  CustomerAddress,
  RecurringBookingPlanResponse,
  ServiceDetail,
} from "@/lib/types";

/**
 * Set up a recurring booking plan (PRODUCT-ENHANCEMENTS.md section 2, task
 * 187), reached from the booking summary page ("Set up as recurring
 * instead") and from a past booking's detail page ("Set up a recurring
 * booking"). Deliberately mirrors booking/summary/page.tsx's structure
 * (service lookup by slug, address list, SlotPicker) rather than
 * reinventing that flow - the only genuinely new UI is the recurrence
 * section below.
 *
 * The day-of-week (weekly/biweekly) or day-of-month (monthly) the plan
 * recurs on is derived from whichever date the customer picks in the
 * SlotPicker's date strip, rather than asking for it a second time via a
 * separate control - "book it on this day, every week" is the natural
 * reading of picking a Tuesday there.
 */
export default function NewRecurringBookingPlanPage() {
  return (
    <Suspense
      fallback={
        <main className="flex w-full flex-col">
          <div className="listing-banner h-[13.5rem] w-full sm:h-[15.5rem]" aria-hidden />
          <ScreenSkeleton cards={4} className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14" />
        </main>
      }
    >
      <RequireAuth>
        <NewRecurringBookingPlanScreen />
      </RequireAuth>
    </Suspense>
  );
}

function NewRecurringBookingPlanScreen() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const serviceSlug = searchParams.get("serviceSlug");
  const { city, locality } = useSelectedCity();

  const [quantity, setQuantity] = useState(1);
  const [selectedAddOnIds, setSelectedAddOnIds] = useState<Set<string>>(new Set());
  const [selectedAddressId, setSelectedAddressId] = useState<string | null>(null);
  const [selectedDate, setSelectedDate] = useState<string>(todayIsoDate);
  const [selectedSlotWindowId, setSelectedSlotWindowId] = useState<string | null>(null);
  const [frequency, setFrequency] = useState<RecurringBookingRecurrenceFrequency>(
    RecurringBookingRecurrenceFrequency.Weekly,
  );
  const [occurrenceCount, setOccurrenceCount] = useState<string>("4");
  const [endDate, setEndDate] = useState<string>("");
  // Off by default (task 370) - matches booking/summary's own wallet
  // checkbox precedent, not a silent auto-apply.
  const [applyWalletCredit, setApplyWalletCredit] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);

  /** Synchronous double-submit guard - see booking/summary/page.tsx. */
  const inFlight = useRef(false);

  const serviceQuery = useQuery({
    queryKey: ["service", serviceSlug],
    queryFn: () => apiFetch<ServiceDetail>(`${API_V1}/services/${serviceSlug}`),
    enabled: !!serviceSlug,
  });

  const addressesQuery = useQuery({
    queryKey: ["addresses"],
    queryFn: () => apiFetch<CustomerAddress[]>(`${API_V1}/addresses`, { authenticated: true }),
  });

  useEffect(() => {
    if (selectedAddressId !== null || !addressesQuery.data) return;
    const preferred = addressesQuery.data.find((a) => a.isDefault) ?? addressesQuery.data[0];
    // Reacting to the addresses query arriving, not a render-time prop change.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    if (preferred) setSelectedAddressId(preferred.id);
  }, [addressesQuery.data, selectedAddressId]);

  const toggleAddOn = (id: string) => {
    setSelectedAddOnIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const service = serviceQuery.data;

  const createMutation = useMutation({
    mutationFn: (body: CreateRecurringBookingPlanRequestBody) =>
      apiFetch<RecurringBookingPlanResponse>(`${API_V1}/recurring-booking-plans`, {
        method: "POST",
        authenticated: true,
        body: JSON.stringify(body),
      }),
    onError: () => {
      // Everything entered stays in component state, so a retry is one click.
      inFlight.current = false;
    },
    onSuccess: () => router.push("/recurring-bookings"),
  });

  const handleSubmit = () => {
    setFormError(null);

    if (!service || !city || !locality || !selectedAddressId || !selectedSlotWindowId) {
      setFormError("Choose an address and a slot before continuing.");
      return;
    }

    // Neither a count nor an end date is allowed: the plan then runs until
    // the customer cancels it.
    const trimmedCount = occurrenceCount.trim();

    // Local midnight, not `new Date(selectedDate)` - that parses a bare
    // YYYY-MM-DD as UTC and shifts the derived weekday/day-of-month by one
    // for anyone behind UTC.
    const anchor = new Date(`${selectedDate}T00:00:00`);
    const isMonthly = frequency === RecurringBookingRecurrenceFrequency.Monthly;
    const isDaily = frequency === RecurringBookingRecurrenceFrequency.Daily;

    const body: CreateRecurringBookingPlanRequestBody = {
      serviceId: service.id,
      cityId: city.id,
      addressId: selectedAddressId,
      localityId: locality.id,
      slotWindowId: selectedSlotWindowId,
      quantity,
      frequency,
      recurrenceDayOfWeek: isMonthly || isDaily ? null : anchor.getDay(),
      recurrenceDayOfMonth: isMonthly ? anchor.getDate() : null,
      startDate: selectedDate,
      endDate: endDate || null,
      occurrenceCount: trimmedCount ? Number(trimmedCount) : null,
      addOns: Array.from(selectedAddOnIds, (addOnId) => ({ addOnId, quantity: 1 })),
      applyWalletCredit,
    };

    if (inFlight.current) return;
    inFlight.current = true;
    createMutation.mutate(body);
  };

  if (!serviceSlug) {
    return (
      <main className="mx-auto w-full max-w-7xl px-4 py-8 sm:px-6 sm:py-12">
        <EmptyState
          title="No service selected"
          description="Pick the service you'd like on a repeating schedule."
          action={<LinkButton href="/categories">Browse services</LinkButton>}
        />
      </main>
    );
  }

  if (serviceQuery.isPending) {
    return (
      <main className="flex w-full flex-col">
        <div className="listing-banner h-[13.5rem] w-full sm:h-[15.5rem]" aria-hidden />
        <ScreenSkeleton cards={4} className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14" />
      </main>
    );
  }

  if (serviceQuery.isError || !service) {
    return (
      <main className="mx-auto w-full max-w-7xl px-4 py-8 sm:px-6 sm:py-12">
        <Alert
          tone="error"
          title="Couldn't load this service"
          action={
            <Button size="sm" variant="secondary" onClick={() => serviceQuery.refetch()}>
              Retry
            </Button>
          }
        >
          {describeError(serviceQuery.error)}
        </Alert>
      </main>
    );
  }

  const anchorDate = new Date(`${selectedDate}T00:00:00`);
  const dayLabel =
    frequency === RecurringBookingRecurrenceFrequency.Daily
      ? "day"
      : frequency === RecurringBookingRecurrenceFrequency.Monthly
        ? `day ${anchorDate.getDate()} of the month`
        : DAY_OF_WEEK_LABELS[anchorDate.getDay()];

  return (
    <main className="flex w-full flex-col animate-rise">
      <PageBanner
        title="Set up a recurring booking"
        description={service.name}
        breadcrumb={
          <BannerBreadcrumb
            items={[
              { label: "Home", href: "/" },
              { label: "Recurring bookings", href: "/recurring-bookings" },
              { label: "Set up a plan" },
            ]}
          />
        }
      />

      <div
        className={cx(
          "mx-auto flex w-full max-w-7xl flex-col gap-6 px-4 py-10 sm:px-6 sm:py-14",
          STICKY_BAR_SPACER,
        )}
      >
      <Card title="Service address" description="Every visit in this plan goes to this address.">
        {addressesQuery.isPending ? (
          <Skeleton className="h-10 rounded-lg" />
        ) : addressesQuery.isError ? (
          <Alert
            tone="error"
            title="Couldn't load your addresses"
            action={
              <Button size="sm" variant="secondary" onClick={() => addressesQuery.refetch()}>
                Retry
              </Button>
            }
          >
            {describeError(addressesQuery.error)}
          </Alert>
        ) : addressesQuery.data.length === 0 ? (
          <EmptyState
            title="No saved addresses"
            description="Add an address before setting up a repeating plan."
            action={<LinkButton href="/addresses/new">Add an address</LinkButton>}
          />
        ) : (
          <Select
            label="Address"
            value={selectedAddressId ?? ""}
            onChange={(e) => setSelectedAddressId(e.target.value)}
            options={addressesQuery.data.map((address) => ({
              value: address.id,
              label: `${address.label} — ${address.line1}, ${address.city} ${address.pincode}`,
            }))}
          />
        )}
      </Card>

      {/* Quantity only for unit-measured services (AC units, rooms, seats);
          a flat-rate service books one per visit and hides the field. */}
      {service.isQuantityAllowed ? (
        <Card title="Quantity">
          <Field
            label="Units per visit"
            type="number"
            min={1}
            className="max-w-[8rem]"
            value={quantity}
            onChange={(e) => setQuantity(Math.max(1, Number(e.target.value) || 1))}
          />
        </Card>
      ) : null}

      {service.addOns.length > 0 ? (
        <Card title="Add-ons" description="Applied to every visit in the plan.">
          <div className="flex flex-col gap-2">
            {service.addOns.map((addOn) => (
              <CheckboxField
                key={addOn.id}
                label={`${addOn.name} (+${inr(addOn.price)})`}
                checked={selectedAddOnIds.has(addOn.id)}
                onChange={() => toggleAddOn(addOn.id)}
              />
            ))}
          </div>
        </Card>
      ) : null}

      <Card
        title="Slot"
        description="This day and time window repeats on the schedule you choose below."
      >
        {city === undefined ? (
          <Skeleton className="h-24 rounded-xl" />
        ) : city === null ? (
          <div className="flex flex-col items-start gap-2.5">
            <p className="text-sm text-fg-muted">Select your city first.</p>
            <CitySelector />
          </div>
        ) : locality === null ? (
          <LocalitySelector cityId={city.id} />
        ) : (
          <SlotPicker
            serviceId={service.id}
            localityId={locality.id}
            selectedDate={selectedDate}
            onDateChange={setSelectedDate}
            selectedSlotWindowId={selectedSlotWindowId}
            onSlotChange={(id) => setSelectedSlotWindowId(id)}
          />
        )}
      </Card>

      <Card title="Recurrence" description={`Repeats every ${dayLabel.toLowerCase()}.`}>
        <div className="flex flex-col gap-5">
          <FrequencyPicker label="Frequency" value={frequency} onChange={setFrequency} />

          <div className="grid gap-4 sm:grid-cols-2">
            <Field
              id="occurrence-count"
              label="Number of visits (optional)"
              type="number"
              min={1}
              value={occurrenceCount}
              onChange={(e) => setOccurrenceCount(e.target.value)}
            />
            <Field
              id="end-date"
              label="End date (optional)"
              type="date"
              min={selectedDate}
              value={endDate}
              onChange={(e) => setEndDate(e.target.value)}
            />
          </div>

          <p className="text-xs leading-relaxed text-fg-subtle">
            Leave both empty and the plan keeps going until you cancel it — pause or cancel any time
            from the manage screen.
          </p>
        </div>
      </Card>

      <Card
        title="Payment"
        description="Applies to every visit this plan generates — off by default, same as a one-off booking."
      >
        <CheckboxField
          label="Use my wallet balance for every visit"
          description="Applied automatically up to what's payable, on top of any pricing already in effect at the time."
          checked={applyWalletCredit}
          onChange={setApplyWalletCredit}
        />
      </Card>

      {formError ? (
        <Alert tone="warning" title="Almost there">
          {formError}
        </Alert>
      ) : null}
      {createMutation.isError ? (
        <Alert tone="error" title="We couldn't set up this plan">
          {describeError(createMutation.error)} Nothing you entered has been lost.
        </Alert>
      ) : null}

      <StickyActionBar>
        <div className="flex flex-col gap-3 sm:flex-row">
          <Button
            type="button"
            size="lg"
            className="flex-1"
            loading={createMutation.isPending}
            onClick={handleSubmit}
          >
            Set up recurring booking
          </Button>
          <Button type="button" size="lg" variant="secondary" onClick={() => router.back()}>
            Cancel
          </Button>
        </div>
      </StickyActionBar>
      </div>
    </main>
  );
}
