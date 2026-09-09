"use client";

import { useQuery } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { Alert, Button, Skeleton, cx } from "@/components/ui";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import { SlotUnavailabilityReason } from "@/lib/types";
import type { SlotDayAvailability, SlotRange } from "@/lib/types";

/** How many upcoming days are offered in the date strip (SRS 11.8.2's "available dates"). */
const VISIBLE_DAYS = 7;

/**
 * Local calendar date as YYYY-MM-DD.
 *
 * Deliberately built from the local getFullYear/getMonth/getDate rather than
 * `toISOString().slice(0, 10)`: toISOString converts to UTC first, so for any
 * timezone ahead of UTC the early hours of the morning render as the previous
 * day. In IST (UTC+05:30) that meant every customer opening the picker before
 * 05:30 got a strip starting on yesterday, and "today" silently shifted.
 */
function isoDate(date: Date): string {
  const year = date.getFullYear();
  const month = `${date.getMonth() + 1}`.padStart(2, "0");
  const day = `${date.getDate()}`.padStart(2, "0");
  return `${year}-${month}-${day}`;
}

function upcomingDates(): string[] {
  const today = new Date();
  return Array.from({ length: VISIBLE_DAYS }, (_, index) => {
    const date = new Date(today);
    date.setDate(date.getDate() + index);
    return isoDate(date);
  });
}

function formatDateLabel(iso: string): { weekday: string; day: string } {
  const date = new Date(`${iso}T00:00:00`);
  return {
    weekday: date.toLocaleDateString(undefined, { weekday: "short" }),
    day: date.toLocaleDateString(undefined, { day: "numeric", month: "short" }),
  };
}

/**
 * What to tell the customer about a date with nothing bookable.
 *
 * The availability API now reports *why* a date came back empty. Before that
 * every empty list was rendered as "Fully booked", so opening the app in the
 * evening - when the day's windows have simply closed - announced the service
 * as fully booked. That reads as scarcity, and it isn't true: the customer's
 * only real problem is that they need to book for tomorrow.
 */
const UNAVAILABILITY_COPY: Record<
  SlotUnavailabilityReason,
  { title: string; description: string; chip: string } | null
> = {
  [SlotUnavailabilityReason.None]: null,
  [SlotUnavailabilityReason.NotServiceable]: {
    title: "Not available here",
    description: "This service isn't available at this address.",
    chip: "Not available at this address",
  },
  [SlotUnavailabilityReason.DateOutOfBookableRange]: {
    title: "Too far ahead",
    description: "We're not taking bookings this far in advance yet — try a nearer date.",
    chip: "Outside the booking window",
  },
  [SlotUnavailabilityReason.Blackout]: {
    title: "We're not working this day",
    description: "We aren't taking bookings on this date — pick another day from the strip above.",
    chip: "Not taking bookings",
  },
  [SlotUnavailabilityReason.NoWindowsConfigured]: {
    title: "No slots on this day",
    description: "We don't run this service on this day — pick another day from the strip above.",
    chip: "No slots this day",
  },
  [SlotUnavailabilityReason.CutoffPassed]: {
    title: "Bookings have closed for this date",
    description: "It's too late to book this one — the next day is still open.",
    chip: "Bookings closed for this date",
  },
  [SlotUnavailabilityReason.FullyBooked]: {
    title: "Fully booked",
    description: "No slots left on this date — pick another day from the strip above.",
    chip: "Fully booked",
  },
};

/** Buckets a slot by its start hour so the list reads as a day, not a flat wall of chips. */
const PARTS = [
  { key: "morning", label: "Morning", until: 12 },
  { key: "afternoon", label: "Afternoon", until: 17 },
  { key: "evening", label: "Evening", until: 24 },
] as const;

function partOfDay(startTime: string): (typeof PARTS)[number]["key"] {
  const hour = Number.parseInt(startTime.slice(0, 2), 10);
  return (PARTS.find((part) => hour < part.until) ?? PARTS[PARTS.length - 1]).key;
}

/**
 * Date picker + time window selection (SRS 11.8, tasks 63a-c).
 *
 * Every one of the next VISIBLE_DAYS days is fetched up front (GET
 * /slots?serviceId&localityId&date, one call per date) rather than only the
 * selected date - the availability API only ever returns the slots that ARE
 * bookable (serviceability, cutoff, blackout, advance-window and per-day
 * capacity filtering all happen server-side, see SlotAvailabilityService), and
 * reports why an empty date is empty rather than returning disabled slots.
 * Prefetching lets the date strip itself show which dates have nothing
 * bookable (SRS 11.8.2 "disabled slots"), and say why, instead of the customer
 * discovering that only after tapping in.
 *
 * Stale-slot handling (task 63c, SRS 11.8.3 "must fail gracefully if no
 * longer available") is deliberately NOT done here: this component only
 * reflects what the availability list currently says. The authoritative
 * re-check against a stale selection happens at submit time via
 * GET /slots/revalidate, driven by the parent booking summary page.
 */
export function SlotPicker({
  serviceId,
  localityId,
  selectedDate,
  onDateChange,
  selectedSlotWindowId,
  onSlotChange,
}: {
  serviceId: string;
  localityId: string;
  selectedDate: string;
  onDateChange: (date: string) => void;
  selectedSlotWindowId: string | null;
  onSlotChange: (slotWindowId: string | null, slotLabel: string | null) => void;
}) {
  // Computed on mount rather than via useMemo: `new Date()` and the
  // locale-dependent weekday/day labels below would otherwise run during the
  // server render too, and the server's default locale/clock can legitimately
  // differ from the browser's (a different `Intl` default locale renders a
  // different weekday string, e.g. "Mon" vs "Lundi") - a hydration mismatch
  // on every page load rather than a rare one. Starting from `[]` keeps the
  // server- and first-client-render markup identical (the strip is simply
  // empty on both) until this effect fills it in just after hydration.
  const [dates, setDates] = useState<string[]>([]);
  useEffect(() => setDates(upcomingDates()), []);

  // One request for the whole strip. This used to be one query per visible
  // day - seven serial round-trips before the picker could say anything,
  // which on a slow connection was the longest wait in the booking flow.
  const rangeFrom = dates[0];
  const rangeTo = dates[dates.length - 1];

  const rangeQuery = useQuery({
    queryKey: ["slots-range", serviceId, localityId, rangeFrom, rangeTo],
    queryFn: () =>
      apiFetch<SlotRange>(
        `${API_V1}/slots/range?serviceId=${serviceId}&localityId=${localityId}&from=${rangeFrom}&to=${rangeTo}`,
      ),
    enabled: dates.length > 0,
  });

  const dayByDate = new Map((rangeQuery.data?.days ?? []).map((day) => [day.date, day]));
  const selectedDay = dayByDate.get(selectedDate);

  // The strip opens on today, which is routinely past its booking cutoff by
  // the time anyone is checking out - the customer landed on "Bookings have
  // closed for this date" and had to work out for themselves that the next
  // day was fine. Once every day has answered, move the selection to the
  // first one that actually has slots.
  //
  // Only ever fires for the initial default: a chip with nothing bookable is
  // rendered disabled above, so a date the customer picked themselves is
  // always bookable and is never overridden. The ref keeps this to a single
  // correction rather than re-running as the queries refetch.
  const hasCorrectedDate = useRef(false);
  useEffect(() => {
    if (hasCorrectedDate.current || dates.length === 0 || !rangeQuery.isSuccess) return;

    const days = rangeQuery.data.days;
    const isBookable = (day: SlotDayAvailability | undefined) =>
      day !== undefined && day.isServiceable && day.slots.length > 0;

    hasCorrectedDate.current = true;

    if (isBookable(days.find((day) => day.date === selectedDate))) return;

    const firstBookable = days.find(isBookable);
    if (firstBookable) {
      onDateChange(firstBookable.date);
      onSlotChange(null, null);
    }
  }, [dates, rangeQuery.isSuccess, rangeQuery.data, selectedDate, onDateChange, onSlotChange]);

  return (
    <div className="flex flex-col gap-6">
      <div>
        <h3 className="mb-2.5 text-sm font-medium text-fg">Date</h3>
        <div className="-mx-1 flex gap-2 overflow-x-auto px-1 pb-2">
          {dates.length === 0
            ? Array.from({ length: VISIBLE_DAYS }, (_, index) => (
                <Skeleton key={index} className="h-[3.25rem] w-[4.75rem] shrink-0 rounded-xl" />
              ))
            : dates.map((date) => {
                const dayAvailability = dayByDate.get(date);
                const { weekday, day } = formatDateLabel(date);
                // A date is disabled once we know for certain it has nothing
                // bookable; while still loading (or on a fetch error) it stays
                // selectable rather than guessing.
                const knownEmpty = dayAvailability !== undefined && dayAvailability.slots.length === 0;
                const notServiceable = dayAvailability !== undefined && !dayAvailability.isServiceable;
                const disabled = knownEmpty || notServiceable;
                const isSelected = date === selectedDate;
                const emptyReason = dayAvailability?.reason;

                return (
                  <button
                    key={date}
                    type="button"
                    disabled={disabled}
                    aria-pressed={isSelected}
                    // Without this a disabled chip is just faded, leaving the
                    // customer to guess why they cannot pick it. The reason
                    // comes from the API rather than being assumed, so a date
                    // past its cutoff no longer claims to be fully booked.
                    title={
                      disabled
                        ? ((emptyReason !== undefined
                            ? UNAVAILABILITY_COPY[emptyReason]?.chip
                            : undefined) ?? "No slots on this date")
                        : undefined
                    }
                    onClick={() => {
                      onDateChange(date);
                      onSlotChange(null, null);
                    }}
                    className={cx(
                      "flex min-w-[4.75rem] shrink-0 flex-col items-center gap-0.5 rounded-xl border px-3 py-2.5 text-xs transition duration-fast ease-out",
                      isSelected
                        ? "border-brand-600 bg-brand-600 text-fg-on-brand shadow-brand"
                        : "border-line bg-surface text-fg hover:border-line-strong hover:bg-surface-2",
                      disabled &&
                        "cursor-not-allowed border-line bg-surface-2 text-fg-subtle line-through opacity-60 hover:bg-surface-2",
                    )}
                  >
                    <span className="font-medium">{weekday}</span>
                    <span className={cx(isSelected ? "text-fg-on-brand/85" : "text-fg-muted")}>
                      {day}
                    </span>
                  </button>
                );
              })}
        </div>
      </div>

      <div>
        <h3 className="mb-2.5 text-sm font-medium text-fg">Time window</h3>

        {rangeQuery.isPending || (rangeQuery.isSuccess && selectedDay === undefined) ? (
          <div className="flex flex-wrap gap-2">
            {Array.from({ length: 6 }, (_, index) => (
              <Skeleton key={index} className="h-11 w-36 rounded-xl" />
            ))}
          </div>
        ) : rangeQuery.isError ? (
          <Alert
            tone="error"
            action={
              <Button size="sm" variant="secondary" onClick={() => rangeQuery.refetch()}>
                Retry
              </Button>
            }
          >
            {describeError(rangeQuery.error)}
          </Alert>
        ) : !selectedDay!.isServiceable ? (
          <Alert tone="error" title={UNAVAILABILITY_COPY[SlotUnavailabilityReason.NotServiceable]!.title}>
            {UNAVAILABILITY_COPY[SlotUnavailabilityReason.NotServiceable]!.description}
          </Alert>
        ) : selectedDay!.slots.length === 0 ? (
          <Alert
            tone="info"
            title={UNAVAILABILITY_COPY[selectedDay!.reason]?.title ?? "No slots on this date"}
          >
            {UNAVAILABILITY_COPY[selectedDay!.reason]?.description ??
              "Pick another day from the strip above."}
          </Alert>
        ) : (
          <div className="flex flex-col gap-4">
            {PARTS.map((part) => {
              const slots = selectedDay!.slots.filter(
                (slot) => partOfDay(slot.startTime) === part.key,
              );
              if (slots.length === 0) return null;

              return (
                <div key={part.key}>
                  <p className="mb-2 text-xs font-semibold uppercase tracking-wide text-fg-subtle">
                    {part.label}
                  </p>
                  <div className="flex flex-wrap gap-2">
                    {slots.map((slot) => {
                      const isSelected = slot.slotWindowId === selectedSlotWindowId;
                      const range = `${slot.startTime.slice(0, 5)}–${slot.endTime.slice(0, 5)}`;
                      return (
                        <button
                          key={slot.slotWindowId}
                          type="button"
                          aria-pressed={isSelected}
                          onClick={() => onSlotChange(slot.slotWindowId, `${slot.name} · ${range}`)}
                          className={cx(
                            "flex flex-col items-start rounded-xl border px-3.5 py-2 text-sm transition duration-fast ease-out",
                            isSelected
                              ? "border-brand-600 bg-brand-600 text-fg-on-brand shadow-brand"
                              : "border-line bg-surface text-fg hover:border-line-strong hover:bg-surface-2",
                          )}
                        >
                          <span className="font-medium">{slot.name}</span>
                          <span
                            className={cx(
                              "nums text-xs",
                              isSelected ? "text-fg-on-brand/85" : "text-fg-muted",
                            )}
                          >
                            {range}
                          </span>
                        </button>
                      );
                    })}
                  </div>
                </div>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );
}
