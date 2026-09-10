"use client";

import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { CitySelector } from "@/components/CitySelector";
import { LocalitySelector } from "@/components/LocalitySelector";
import { Alert, Button, Skeleton } from "@/components/ui";
import { useServiceability } from "@/hooks/useServiceability";
import { useSelectedCity } from "@/hooks/useSelectedCity";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import { todayIsoDate } from "@/lib/date";
import { clearSelectedLocality } from "@/lib/location";
import type { SlotAvailability } from "@/lib/types";

/**
 * Serviceability + slot availability at the customer's location (SRS 11.4,
 * 24.4). Blocks browsing further into a booking flow when the service isn't
 * offered there (SRS 11.4.3), rather than only discovering that at checkout.
 *
 * Booking itself is out of scope here: there is no booking/cart API yet
 * (Phase 3+), so a confirmed slot is shown as informational availability,
 * not wired to a submit action that would go nowhere.
 */
export function ServiceAvailability({ serviceId }: { serviceId: string }) {
  const { city, locality } = useSelectedCity();

  if (city === undefined) {
    return null;
  }

  return (
    <section
      aria-labelledby="availability-heading"
      className="flex flex-col gap-3 rounded-2xl border border-line bg-surface p-5 shadow-sm"
    >
      <h2 id="availability-heading" className="text-sm font-semibold text-fg">
        Availability at your location
      </h2>

      {city === null ? (
        <div className="flex flex-col items-start gap-2">
          <p className="text-sm text-fg-muted">Select your city to check availability.</p>
          <CitySelector />
        </div>
      ) : locality === null ? (
        <LocalitySelector cityId={city.id} />
      ) : (
        <ServiceLocalityAvailability serviceId={serviceId} localityId={locality.id} localityName={locality.name} />
      )}
    </section>
  );
}

function ServiceLocalityAvailability({
  serviceId,
  localityId,
  localityName,
}: {
  serviceId: string;
  localityId: string;
  localityName: string;
}) {
  // Shared with `BookingCta` (see useServiceability's doc comment) so this
  // panel and the "Book now" CTA can never disagree, and so there is exactly
  // one place that fires the serviceability request for a given
  // service/locality pair.
  const { isUnknown, isUnserviceable, isServiceable, isError, error, refetch } =
    useServiceability(serviceId);

  return (
    <div className="flex flex-col gap-3">
      <div className="flex items-center justify-between gap-3 text-sm">
        <p className="min-w-0 truncate text-fg-muted">
          {isUnknown ? "Checking for" : isServiceable ? "Available in" : "Checked for"}{" "}
          <span className="font-medium text-fg">{localityName}</span>
        </p>
        <button
          type="button"
          onClick={clearSelectedLocality}
          className="shrink-0 font-medium text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
        >
          Change
        </button>
      </div>

      {isUnknown ? (
        <Skeleton className="h-16 w-full" />
      ) : isError ? (
        <Alert
          tone="error"
          action={
            <Button size="sm" variant="secondary" onClick={() => refetch()}>
              Retry
            </Button>
          }
        >
          {describeError(error)}
        </Alert>
      ) : isUnserviceable ? (
        <Alert tone="error" title="Not available here">
          This service isn&apos;t available in your area yet.
        </Alert>
      ) : (
        <div className="flex flex-col gap-3">
          <Alert tone="success" title="Available in your area">
            This service can be booked at {localityName}.
          </Alert>
          <SlotPreview serviceId={serviceId} localityId={localityId} />
        </div>
      )}
    </div>
  );
}

function SlotPreview({ serviceId, localityId }: { serviceId: string; localityId: string }) {
  const [date] = useState(todayIsoDate);

  const query = useQuery({
    queryKey: ["slots", serviceId, localityId, date],
    queryFn: () =>
      apiFetch<SlotAvailability>(
        `${API_V1}/slots?serviceId=${serviceId}&localityId=${localityId}&date=${date}`,
      ),
  });

  if (query.isPending) {
    return (
      <div className="flex flex-wrap gap-2">
        {Array.from({ length: 3 }, (_, index) => (
          <Skeleton key={index} className="h-7 w-28 rounded-full" />
        ))}
      </div>
    );
  }

  if (query.isError) {
    return (
      <Alert
        tone="error"
        action={
          <Button size="sm" variant="secondary" onClick={() => query.refetch()}>
            Retry
          </Button>
        }
      >
        {describeError(query.error)}
      </Alert>
    );
  }

  if (query.data.slots.length === 0) {
    return (
      <p className="text-sm text-fg-muted">
        No slots left today — you can pick another date at checkout.
      </p>
    );
  }

  return (
    <div className="flex flex-wrap gap-2">
      {query.data.slots.map((slot) => (
        <span
          key={slot.slotWindowId}
          className="nums rounded-full border border-line bg-surface-2 px-3 py-1 text-xs text-fg-muted"
        >
          {slot.name} · {slot.startTime.slice(0, 5)}–{slot.endTime.slice(0, 5)}
        </span>
      ))}
    </div>
  );
}
