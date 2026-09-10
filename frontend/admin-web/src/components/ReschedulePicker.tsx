"use client";

import { useQuery } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { Alert, Field, Select, Skeleton, cx } from "@/components/ui";
import { describeError } from "@/lib/api";
import { getRescheduleCities, getRescheduleSlots, searchRescheduleLocalities } from "@/lib/bookings-api";
import { SlotUnavailabilityReason } from "@/lib/bookings-types";

/**
 * Locality + slot-window picker for the admin reschedule panel (row 26,
 * docs/OPEN-FIXES-FEATURES.csv) - replaces the hand-typed "Locality ID" and
 * "Slot window ID" UUID fields with the same city → locality search → date →
 * slot-window flow customer-web's LocalitySelector/SlotPicker already give
 * the customer, built on the same admin-api endpoints
 * (getRescheduleCities/searchRescheduleLocalities/getRescheduleSlots), which
 * in turn call the exact application services (IGeographyQueryService,
 * ISlotAvailabilityService) those customer-facing components' APIs do.
 */
export function ReschedulePicker({
  bookingId,
  localityId,
  onLocalityChange,
  slotWindowId,
  onSlotChange,
  slotDate,
  onDateChange,
}: {
  bookingId: string;
  localityId: string;
  onLocalityChange: (localityId: string, label: string) => void;
  slotWindowId: string;
  onSlotChange: (slotWindowId: string, label: string) => void;
  slotDate: string;
  onDateChange: (date: string) => void;
}) {
  const [cityId, setCityId] = useState("");
  const [localitySearch, setLocalitySearch] = useState("");
  const [debouncedSearch, setDebouncedSearch] = useState("");

  useEffect(() => {
    const timer = window.setTimeout(() => setDebouncedSearch(localitySearch), 250);
    return () => window.clearTimeout(timer);
  }, [localitySearch]);

  const citiesQuery = useQuery({
    queryKey: ["admin-reschedule-cities"],
    queryFn: getRescheduleCities,
  });

  const localitiesQuery = useQuery({
    queryKey: ["admin-reschedule-localities", cityId, debouncedSearch],
    queryFn: () => searchRescheduleLocalities(cityId, debouncedSearch),
    enabled: cityId.length > 0,
  });

  const slotsQuery = useQuery({
    queryKey: ["admin-reschedule-slots", bookingId, localityId, slotDate],
    queryFn: () => getRescheduleSlots(bookingId, localityId, slotDate),
    enabled: localityId.length > 0 && slotDate.length > 0,
  });

  return (
    <div className="flex flex-col gap-4">
      {citiesQuery.isError ? (
        <Alert tone="error">{describeError(citiesQuery.error)}</Alert>
      ) : (
        <Select
          label="City"
          placeholder="Select a city…"
          options={(citiesQuery.data ?? []).map((city) => ({ value: city.id, label: `${city.name}, ${city.stateName}` }))}
          value={cityId}
          onChange={(e) => {
            setCityId(e.target.value);
            onLocalityChange("", "");
          }}
          disabled={citiesQuery.isPending}
        />
      )}

      {cityId ? (
        <div className="flex flex-col gap-2">
          <Field
            label="Find locality"
            value={localitySearch}
            onChange={(e) => setLocalitySearch(e.target.value)}
            placeholder="Locality name or pincode"
          />

          {localitiesQuery.isPending ? (
            <Skeleton className="h-10 w-full" />
          ) : localitiesQuery.isError ? (
            <Alert tone="error">{describeError(localitiesQuery.error)}</Alert>
          ) : localitiesQuery.data.length === 0 ? (
            <p className="text-sm text-fg-muted">No localities matched.</p>
          ) : (
            <ul className="flex max-h-48 flex-col gap-1 overflow-y-auto rounded-lg border border-line p-1">
              {localitiesQuery.data.map((locality) => {
                const isSelected = locality.id === localityId;
                return (
                  <li key={locality.id}>
                    <button
                      type="button"
                      onClick={() => {
                        onLocalityChange(locality.id, `${locality.name} (${locality.pincodeCode})`);
                        onSlotChange("", "");
                      }}
                      aria-current={isSelected ? "true" : undefined}
                      className={cx(
                        "flex w-full items-center justify-between gap-3 rounded-md px-3 py-2 text-left text-sm transition-colors duration-fast ease-out",
                        isSelected ? "bg-brand-50 font-medium text-brand-700 dark:bg-brand-500/15 dark:text-brand-300" : "text-fg hover:bg-surface-2",
                      )}
                    >
                      <span className="truncate">
                        {locality.name} <span className="text-xs text-fg-subtle">· {locality.zoneName} · {locality.pincodeCode}</span>
                      </span>
                    </button>
                  </li>
                );
              })}
            </ul>
          )}
        </div>
      ) : null}

      <Field label="New slot date" type="date" required value={slotDate} onChange={(e) => { onDateChange(e.target.value); onSlotChange("", ""); }} />

      {localityId && slotDate ? (
        <div>
          <h4 className="mb-2 text-sm font-medium text-fg">Slot window</h4>
          {slotsQuery.isPending ? (
            <div className="flex flex-wrap gap-2">
              {Array.from({ length: 3 }, (_, index) => (
                <Skeleton key={index} className="h-11 w-36 rounded-xl" />
              ))}
            </div>
          ) : slotsQuery.isError ? (
            <Alert tone="error">{describeError(slotsQuery.error)}</Alert>
          ) : !slotsQuery.data.isServiceable ? (
            <Alert tone="error">This service isn&rsquo;t available at this locality.</Alert>
          ) : slotsQuery.data.slots.length === 0 ? (
            <Alert tone="info">
              {slotsQuery.data.reason === SlotUnavailabilityReason.FullyBooked
                ? "Every window on this date is already fully booked."
                : "No slots available on this date — try another date."}
            </Alert>
          ) : (
            <div className="flex flex-wrap gap-2">
              {slotsQuery.data.slots.map((slot) => {
                const isSelected = slot.slotWindowId === slotWindowId;
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
                    <span className={cx("nums text-xs", isSelected ? "text-fg-on-brand/85" : "text-fg-muted")}>{range}</span>
                  </button>
                );
              })}
            </div>
          )}
        </div>
      ) : null}
    </div>
  );
}
