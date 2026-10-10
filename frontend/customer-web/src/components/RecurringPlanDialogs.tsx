"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { formatCalendarDate, formatTimeRange } from "@/components/patterns";
import { Alert, Button, CheckboxField, Field, Modal, Skeleton, cx } from "@/components/ui";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import { isoDateOffsetFromToday } from "@/lib/date";
import {
  MAX_SKIP_DAYS,
  MAX_SKIP_RANGES,
  cancellationChargeSentence,
  visitsLabel,
} from "@/lib/recurring-plan";
import type {
  RecurringBookingPlanResponse,
  SkipVisitsRequestBody,
  SlotAvailability,
} from "@/lib/types";

function planUrl(planId: string, action: string): string {
  return `${API_V1}/recurring-booking-plans/${planId}/${action}`;
}

/**
 * "Skip visits until a date": the customer is away, so no visit is generated before the date they choose and the
 * plan carries on from there. Cheaper and kinder than pausing - nothing has to be remembered and resumed.
 */
export function SkipVisitsDialog({
  plan,
  open,
  onClose,
}: {
  plan: RecurringBookingPlanResponse;
  open: boolean;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [resumeOn, setResumeOn] = useState("");
  const [cancelBooked, setCancelBooked] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const earliest = isoDateOffsetFromToday(1);
  const latest = isoDateOffsetFromToday(MAX_SKIP_DAYS);
  const skipsLeft = Math.max(0, MAX_SKIP_RANGES - plan.skipRangesUsed);
  const isValid = resumeOn >= earliest && resumeOn <= latest;
  // Visits already booked before the chosen date: the only ones the checkbox below is about.
  const bookedBefore = resumeOn === "" ? 0 : (plan.upcomingBookedVisitDates ?? []).filter((d) => d < resumeOn).length;

  const handleClose = () => {
    setError(null);
    setResumeOn("");
    onClose();
  };

  const skipMutation = useMutation({
    mutationFn: (body: SkipVisitsRequestBody) =>
      apiFetch<RecurringBookingPlanResponse>(planUrl(plan.id, "skip-visits"), {
        method: "POST",
        authenticated: true,
        body: JSON.stringify(body),
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["recurring-booking-plans"] });
      queryClient.invalidateQueries({ queryKey: ["bookings"] });
      handleClose();
    },
    onError: (err) => setError(describeError(err)),
  });

  return (
    <Modal
      open={open}
      onClose={handleClose}
      title="Skip visits until a date"
      description={`No new ${plan.serviceName} visit is booked before the date you choose, and the plan carries on from there on its own. Skipping is free.`}
      size="sm"
      footer={
        <>
          <Button type="button" variant="secondary" onClick={handleClose}>
            Never mind
          </Button>
          <Button
            type="button"
            loading={skipMutation.isPending}
            disabled={!isValid || skipsLeft === 0}
            onClick={() => {
              setError(null);
              skipMutation.mutate({ resumeOn, cancelBookedVisits: bookedBefore > 0 && cancelBooked });
            }}
          >
            Skip visits
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        <Field
          id={`skip-until-${plan.id}`}
          label="Visits resume on"
          type="date"
          min={earliest}
          max={latest}
          value={resumeOn}
          onChange={(e) => setResumeOn(e.target.value)}
          hint={`Up to ${MAX_SKIP_DAYS} days from today. You can do this ${skipsLeft} more ${skipsLeft === 1 ? "time" : "times"} on this plan - to be away longer, pause the plan.`}
          error={resumeOn !== "" && !isValid ? "Choose a date from tomorrow, within the next 30 days." : undefined}
        />

        {bookedBefore > 0 ? (
          <CheckboxField
            label={`Also cancel the ${visitsLabel(bookedBefore)} already booked before that date`}
            description={`${cancellationChargeSentence(plan.cancellationFreeWindowHours, plan.lateCancellationFeePercentage)} Leave this off to keep ${bookedBefore === 1 ? "it" : "them"}.`}
            checked={cancelBooked}
            onChange={setCancelBooked}
          />
        ) : resumeOn !== "" ? (
          <p className="text-sm leading-relaxed text-fg-muted">
            No visits are booked before that date, so there is nothing to cancel.
          </p>
        ) : null}

        {error ? (
          <Alert tone="error" title="That didn't work">
            {error}
          </Alert>
        ) : null}
      </div>
    </Modal>
  );
}

/**
 * "Change time": moves every visit booked from now on to another time window of the day. Visits already booked
 * keep the time they have - changing those is a per-booking reschedule, with its own policy - so this can never
 * fail half-way through a batch.
 */
export function ChangePlanTimeDialog({
  plan,
  open,
  onClose,
}: {
  plan: RecurringBookingPlanResponse;
  open: boolean;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [selected, setSelected] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  // The windows are looked up for the plan's next visit date: that is the date the server checks the new
  // window can serve before it accepts the change.
  const slotsQuery = useQuery({
    queryKey: ["plan-slot-options", plan.id, plan.nextOccurrenceDate],
    queryFn: () =>
      apiFetch<SlotAvailability>(
        `${API_V1}/slots?serviceId=${plan.serviceId}&localityId=${plan.localityId}&date=${plan.nextOccurrenceDate}`,
      ),
    enabled: open && plan.localityId !== null,
  });

  const handleClose = () => {
    setError(null);
    setSelected(null);
    onClose();
  };

  const changeMutation = useMutation({
    mutationFn: (slotWindowId: string) =>
      apiFetch<RecurringBookingPlanResponse>(planUrl(plan.id, "slot"), {
        method: "POST",
        authenticated: true,
        body: JSON.stringify({ slotWindowId }),
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["recurring-booking-plans"] });
      handleClose();
    },
    onError: (err) => setError(describeError(err)),
  });

  const slots = slotsQuery.data?.slots ?? [];
  const canChoose = selected !== null && selected !== plan.slotWindowId;

  return (
    <Modal
      open={open}
      onClose={handleClose}
      title="Change visit time"
      description={`Pick the time of day for every ${plan.serviceName} visit from ${formatCalendarDate(plan.nextOccurrenceDate)} on.`}
      size="sm"
      footer={
        <>
          <Button type="button" variant="secondary" onClick={handleClose}>
            Never mind
          </Button>
          <Button
            type="button"
            loading={changeMutation.isPending}
            disabled={!canChoose}
            onClick={() => {
              if (!selected) return;
              setError(null);
              changeMutation.mutate(selected);
            }}
          >
            Change time
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {plan.localityId === null ? (
          <Alert tone="warning" title="This plan's time can't be changed here">
            Cancel it and set up a new plan with the time you want.
          </Alert>
        ) : slotsQuery.isPending ? (
          <div className="flex flex-col gap-2" aria-hidden>
            <Skeleton className="h-11 w-full" />
            <Skeleton className="h-11 w-full" />
          </div>
        ) : slotsQuery.isError ? (
          <Alert
            tone="error"
            title="Couldn't load the available times"
            action={
              <Button size="sm" variant="secondary" onClick={() => slotsQuery.refetch()}>
                Retry
              </Button>
            }
          >
            {describeError(slotsQuery.error)}
          </Alert>
        ) : slots.length === 0 ? (
          <Alert tone="info" title="No other times are open">
            Nothing is available on {formatCalendarDate(plan.nextOccurrenceDate)} - try again later.
          </Alert>
        ) : (
          <div role="radiogroup" aria-label="Visit time" className="flex flex-col gap-2">
            {slots.map((slot) => {
              const isCurrent = slot.slotWindowId === plan.slotWindowId;
              const isSelected = (selected ?? plan.slotWindowId) === slot.slotWindowId;
              return (
                <button
                  key={slot.slotWindowId}
                  type="button"
                  role="radio"
                  aria-checked={isSelected}
                  onClick={() => setSelected(slot.slotWindowId)}
                  className={cx(
                    "flex items-center justify-between gap-3 rounded-xl border px-3.5 py-3 text-left text-sm transition duration-fast ease-out",
                    isSelected
                      ? "border-brand-600 bg-brand-50 dark:bg-brand-500/15"
                      : "border-line bg-surface hover:border-line-strong hover:bg-surface-2",
                  )}
                >
                  <span className="font-medium text-fg">{slot.name}</span>
                  <span className="nums text-fg-muted">
                    {formatTimeRange(slot.startTime, slot.endTime)}
                    {isCurrent ? <span className="ml-2 text-xs text-fg-subtle">Current</span> : null}
                  </span>
                </button>
              );
            })}
          </div>
        )}

        <div className="flex flex-col gap-1.5 text-xs leading-relaxed text-fg-subtle">
          <p>
            Changing the time is free and applies from {formatCalendarDate(plan.nextOccurrenceDate)}.
          </p>
          <p>
            {(plan.upcomingBookedVisitDates ?? []).length > 0
              ? `${visitsLabel((plan.upcomingBookedVisitDates ?? []).length)} already booked ${(plan.upcomingBookedVisitDates ?? []).length === 1 ? "keeps" : "keep"} the old time.`
              : "Visits already booked keep their time."}{" "}
            To move one, reschedule it from My bookings - each follows its service&apos;s reschedule policy, and a late
            reschedule can carry a fee.
          </p>
        </div>

        {error ? (
          <Alert tone="error" title="That didn't work">
            {error}
          </Alert>
        ) : null}
      </div>
    </Modal>
  );
}
