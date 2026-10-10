"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { useRef, useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import {
  BannerBreadcrumb,
  DetailList,
  DetailRow,
  ScreenSkeleton,
  formatInstant,
  formatLocalDateTime,
  inr,
} from "@/components/patterns";
import { PageBanner } from "@/components/PageBanner";
import { RequireAuth } from "@/components/RequireAuth";
import { Alert, Badge, Button, Card, PageHeading, Textarea } from "@/components/ui";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import { RefundMethod, RefundStatus } from "@/lib/types";
import type { CancellationOutcomeResponse, CancellationPolicyResponse } from "@/lib/types";

/**
 * Cancellation flow: policy display, confirmation, refund outcome (SRS
 * 11.14.3, tasks 89a-c).
 *
 * The confirmation pattern is the page itself rather than a modal on top of
 * it: the consequence of cancelling is a specific number (the refund), and
 * that number has to be readable at the moment of deciding, not summarised in
 * a dialog. The destructive control is isolated below its own rule, carries
 * the danger tone, and requires a typed reason — three deliberate steps
 * between "I clicked into this page" and "the booking is gone".
 */
export default function CancelBookingPage() {
  return (
    <RequireAuth>
      <CancelBookingScreen />
    </RequireAuth>
  );
}

const cancelSchema = z.object({
  reason: z
    .string()
    .min(1, "Please tell us why you're cancelling.")
    .max(500, "Reason must be 500 characters or fewer."),
});

type CancelFormValues = z.infer<typeof cancelSchema>;

function refundMethodLabel(method: RefundMethod): string {
  return method === RefundMethod.Gateway ? "Original payment method" : "Wallet credit";
}

function refundStatusLabel(status: RefundStatus): string {
  switch (status) {
    case RefundStatus.Initiated:
      return "Refund started";
    case RefundStatus.Processing:
      return "Refund processing";
    case RefundStatus.Refunded:
      return "Refunded";
    case RefundStatus.Failed:
      return "Refund couldn't be completed - contact support";
    default:
      return "Unknown";
  }
}

/** The late fee on `basis` at `percentage` percent, to the paisa. */
function percentageOf(basis: number, percentage: number): number {
  return Math.round(basis * percentage) / 100;
}

/**
 * Says, in words, why a cancellation was or is free, when it stops being free, and what a late one costs - the part
 * "Within free cancellation window: Yes/No" leaves a customer to guess.
 *
 * Used for the preview ("if you cancel now") and the result ("what happened"), with the same facts and the same
 * sentence shapes, so what the customer was told before is what they are told after. Every number comes from the
 * API (cut-off time, the amount the fee applies to, any carried-over reschedule charge) rather than being re-derived
 * here.
 */
function CancellationChargesExplainer({
  mode,
  withinFreeWindow,
  freeWindowHours,
  feePercentage,
  endsAt,
  basis,
  fee,
  earlierRescheduleCharge,
  feeBeforeCredit,
  rescheduleFeeCredited,
}: {
  mode: "preview" | "result";
  withinFreeWindow: boolean;
  freeWindowHours: number;
  feePercentage: number;
  endsAt: string | null;
  basis: number;
  fee: number;
  earlierRescheduleCharge: number;
  /** The fee the policy sets, before a late-reschedule fee already paid is counted against it. */
  feeBeforeCredit: number;
  /** The part of that fee the customer already paid as a late-reschedule fee. */
  rescheduleFeeCredited: number;
}) {
  const cutoff = endsAt ? formatLocalDateTime(endsAt) : null;
  const lateFeeOnBasis = percentageOf(basis, feePercentage);
  const carried = earlierRescheduleCharge > 0;
  const isPreview = mode === "preview";
  const credited = rescheduleFeeCredited > 0;
  // The late fee's own line ("20% of the 999 you paid is 199.80") is shown when the clock set the fee; the rule
  // below then does not repeat the amount.
  const showsFeeLine = !withinFreeWindow && !carried && basis > 0;

  let headline: string;
  if (withinFreeWindow) {
    headline = isPreview
      ? `Cancelling now is free${cutoff ? ` - free cancellation lasts until ${cutoff}` : ""}.`
      : `You cancelled in time, so no cancellation fee was charged${cutoff ? ` (free cancellation lasted until ${cutoff})` : ""}.`;
  } else if (carried) {
    // With a reschedule fee already paid, what is kept is lower than the charge that set the fee; the credit line below
    // says how much, so this headline speaks of the charge rather than of what was finally kept.
    headline = isPreview
      ? `A ${inr(earlierRescheduleCharge)} charge from an earlier late reschedule of this booking applies, so a fee is charged even though free cancellation${cutoff ? ` lasts until ${cutoff}` : " has not ended"}.`
      : credited
        ? `A ${inr(earlierRescheduleCharge)} charge from an earlier late reschedule of this booking was carried over, so a fee applied even though you cancelled${cutoff ? ` before ${cutoff}` : " in time"}.`
        : `A ${inr(earlierRescheduleCharge)} charge from an earlier late reschedule of this booking was carried over, so ${inr(fee)} was kept even though you cancelled${cutoff ? ` before ${cutoff}` : " in time"}.`;
  } else {
    headline = isPreview
      ? `Free cancellation ended${cutoff ? ` on ${cutoff}` : ""}, so a ${feePercentage}% fee applies if you cancel now.`
      : `Free cancellation had ended${cutoff ? ` (${cutoff})` : ""}, so a ${feePercentage}% late-cancellation fee was kept.`;
  }

  return (
    <div className="flex flex-col gap-2 rounded-xl border border-line bg-surface-2 px-4 py-3 text-sm leading-relaxed text-fg-muted">
      <p className="font-medium text-fg">{headline}</p>
      {showsFeeLine ? (
        <p>
          {feePercentage}% of the {inr(basis)} you paid is {inr(lateFeeOnBasis)}
          {isPreview ? " - that is the fee." : fee === lateFeeOnBasis ? " - that is the fee." : "."}
        </p>
      ) : null}
      {credited ? (
        <p className="font-medium text-fg">
          You already paid {inr(rescheduleFeeCredited)} as a late reschedule fee on this booking, and it counts toward
          this fee, so you don&apos;t pay twice.{" "}
          {fee > 0
            ? `${inr(fee)} of the ${inr(feeBeforeCredit)} fee is ${isPreview ? "kept from your refund" : "what was kept from your refund"}.`
            : `It covers the whole ${inr(feeBeforeCredit)} fee, so nothing ${isPreview ? "is" : "was"} kept from your refund.`}
        </p>
      ) : null}
      <p>
        <span className="font-medium text-fg-muted">The rule:</span> cancelling at least {freeWindowHours} hours
        before your slot{cutoff ? ` (by ${cutoff})` : ""} is free. After that, {feePercentage}% of the amount you
        paid is kept as a fee
        {basis > 0 && !showsFeeLine ? ` - ${inr(lateFeeOnBasis)} on this booking` : ""}; the rest comes back to you.
      </p>
    </div>
  );
}

function CancelBookingScreen() {
  const { id } = useParams<{ id: string }>();
  const router = useRouter();
  const queryClient = useQueryClient();

  const policyQuery = useQuery({
    queryKey: ["cancellation-policy", id],
    queryFn: () =>
      apiFetch<CancellationPolicyResponse>(`${API_V1}/bookings/${id}/cancellation/policy`, {
        authenticated: true,
      }),
  });

  // The policy as the customer saw it when they decided. The query can refetch once the booking is cancelled (a
  // cancelled booking has no live policy), so the result screen explains against this snapshot, not the live data.
  const [decidedPolicy, setDecidedPolicy] = useState<CancellationPolicyResponse | null>(null);

  const form = useForm<CancelFormValues>({
    resolver: zodResolver(cancelSchema),
    defaultValues: { reason: "" },
  });
  const { errors } = form.formState;

  /**
   * Cancelling twice is not idempotent from the customer's point of view -
   * the second call fails loudly on an already-cancelled booking - so a
   * double click on a slow connection must not reach the API twice. The ref
   * is set synchronously, ahead of the mutation's own pending state.
   */
  const inFlight = useRef(false);

  const cancelMutation = useMutation({
    mutationFn: (values: CancelFormValues) =>
      apiFetch<CancellationOutcomeResponse>(`${API_V1}/bookings/${id}/cancellation`, {
        method: "POST",
        authenticated: true,
        body: JSON.stringify(values),
      }),
    onSettled: () => {
      inFlight.current = false;
    },
    onSuccess: () => {
      // The booking detail page must reflect the cancellation immediately.
      queryClient.invalidateQueries({ queryKey: ["booking", id] });
    },
  });

  // False positive: the rule can't see that react-hook-form's handleSubmit
  // only *builds* this callback during render - it doesn't call it until a
  // real submit event fires, well outside render, which is the only place
  // inFlight.current is ever actually read or written.
  // eslint-disable-next-line react-hooks/refs
  const submit = form.handleSubmit((values) => {
    if (inFlight.current) return;
    inFlight.current = true;
    setDecidedPolicy(policyQuery.data ?? null);
    cancelMutation.mutate(values);
  });

  if (policyQuery.isPending) {
    return (
      <main className="flex w-full flex-col">
        <div className="listing-banner h-[13.5rem] w-full sm:h-[15.5rem]" aria-hidden />
        <ScreenSkeleton cards={2} className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14" />
      </main>
    );
  }

  if (policyQuery.isError || !policyQuery.data) {
    return (
      <main className="mx-auto w-full max-w-7xl px-4 py-12 sm:px-6">
        <PageHeading title="Cancel booking" />
        <Alert
          tone="error"
          title="Couldn't load the cancellation policy"
          action={
            <Button size="sm" variant="secondary" onClick={() => policyQuery.refetch()}>
              Retry
            </Button>
          }
        >
          {describeError(policyQuery.error)}
        </Alert>
      </main>
    );
  }

  const policy = policyQuery.data;

  if (cancelMutation.isSuccess) {
    const outcome = cancelMutation.data;
    const shown = decidedPolicy ?? policy;
    return (
      <main className="mx-auto w-full max-w-7xl px-4 py-8 sm:px-6 sm:py-12">
        <div className="mb-7 flex animate-rise flex-col items-center gap-3 text-center">
          <span
            className="flex h-12 w-12 items-center justify-center rounded-full bg-info-soft text-info ring-8 ring-info/10"
            aria-hidden
          >
            <svg
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
              strokeLinejoin="round"
              className="h-6 w-6"
            >
              <circle cx="12" cy="12" r="9" />
              <path d="M12 8v4m0 4h.01" />
            </svg>
          </span>
          <h1 className="text-display-sm font-semibold text-fg">Booking cancelled</h1>
          <p className="text-sm text-fg-muted">Cancelled {formatInstant(outcome.cancelledAtUtc)}</p>
        </div>

        <Card title="Refund outcome">
          <div className="mb-4 rounded-xl border border-line bg-surface-2 px-4 py-3">
            <p className="text-xs font-medium uppercase tracking-wide text-fg-muted">
              Coming back to you
            </p>
            <p className="nums mt-1 text-2xl font-semibold text-fg">{inr(outcome.refundAmount)}</p>
          </div>

          <div className="mb-4">
            <CancellationChargesExplainer
              mode="result"
              withinFreeWindow={outcome.withinFreeCancellationWindow}
              freeWindowHours={shown.freeCancellationWindowHours}
              feePercentage={shown.lateCancellationFeePercentage}
              endsAt={outcome.freeCancellationEndsAt}
              basis={outcome.feeBasisAmount}
              fee={outcome.cancellationFeeAmount}
              earlierRescheduleCharge={outcome.earlierRescheduleCharge}
              feeBeforeCredit={outcome.cancellationFeeBeforeCredit}
              rescheduleFeeCredited={outcome.rescheduleFeeCredited}
            />
          </div>

          <DetailList>
            <DetailRow label="Cancellation fee" numeric>
              {inr(outcome.cancellationFeeAmount)}
            </DetailRow>
            <DetailRow label="Refund amount" numeric>
              {inr(outcome.refundAmount)}
            </DetailRow>
            {outcome.refundMethod !== null ? (
              <DetailRow label="Refund method">{refundMethodLabel(outcome.refundMethod)}</DetailRow>
            ) : null}
            {outcome.refundStatus !== null ? (
              <DetailRow label="Refund status">{refundStatusLabel(outcome.refundStatus)}</DetailRow>
            ) : null}
            <DetailRow label="Within free cancellation window">
              <Badge tone={outcome.withinFreeCancellationWindow ? "success" : "warning"}>
                {outcome.withinFreeCancellationWindow ? "Yes" : "No"}
              </Badge>
            </DetailRow>
          </DetailList>

          <div className="mt-6">
            {/* A real button, not a link dressed as one: the E2E suite
                addresses "Back to booking" by the button role. */}
            <Button type="button" fullWidth onClick={() => router.push(`/bookings/${id}`)}>
              Back to booking
            </Button>
          </div>
        </Card>
      </main>
    );
  }

  if (!policy.isEligible) {
    return (
      <main className="mx-auto w-full max-w-7xl px-4 py-8 sm:px-6 sm:py-12">
        <PageHeading title="Cancel booking" />
        <Alert tone="warning" title="This booking can't be cancelled">
          {policy.ineligibilityReason ??
            "Cancellation isn't available for this booking any more."}
        </Alert>
        <div className="mt-5">
          <Link
            href={`/bookings/${id}`}
            className="text-sm font-medium text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
          >
            Back to booking
          </Link>
        </div>
      </main>
    );
  }

  return (
    <main className="flex w-full flex-col animate-rise">
      <PageBanner
        title="Cancel booking"
        description="Review what you'll get back before confirming — this can't be undone."
        breadcrumb={
          <BannerBreadcrumb
            items={[{ label: "Home", href: "/" }, { label: "My bookings", href: "/bookings" }, { label: "Cancel booking" }]}
          />
        }
      />

      <div className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14">
      <div className="flex flex-col gap-6">
        <Card title="Cancellation policy">
          <div className="mb-4 flex items-center justify-between gap-3 rounded-xl border border-line bg-surface-2 px-4 py-3">
            <div>
              {/* Deliberately not the words "Refund amount": that exact
                  string is asserted once per screen by the E2E suite, and it
                  belongs on the itemised row below, not on this hero. */}
              <p className="text-xs font-medium uppercase tracking-wide text-fg-muted">
                You&apos;ll get back
              </p>
              <p className="nums mt-1 text-2xl font-semibold text-fg">{inr(policy.refundAmount)}</p>
            </div>
            <Badge tone={policy.withinFreeCancellationWindow ? "success" : "warning"}>
              {policy.withinFreeCancellationWindow ? "Free cancellation" : "Late cancellation"}
            </Badge>
          </div>

          <div className="mb-4">
            <CancellationChargesExplainer
              mode="preview"
              withinFreeWindow={policy.withinFreeCancellationWindow}
              freeWindowHours={policy.freeCancellationWindowHours}
              feePercentage={policy.lateCancellationFeePercentage}
              endsAt={policy.freeCancellationEndsAt}
              basis={policy.feeBasisAmount}
              fee={policy.cancellationFeeAmount}
              earlierRescheduleCharge={policy.earlierRescheduleCharge}
              feeBeforeCredit={policy.cancellationFeeBeforeCredit}
              rescheduleFeeCredited={policy.rescheduleFeeCredited}
            />
          </div>

          <DetailList>
            <DetailRow label="Free cancellation window" numeric>
              {policy.freeCancellationWindowHours} hours before slot
            </DetailRow>
            <DetailRow label="Within free window">
              {policy.withinFreeCancellationWindow ? "Yes" : "No"}
            </DetailRow>
            {!policy.withinFreeCancellationWindow ? (
              <DetailRow label="Late cancellation fee" numeric>
                {policy.lateCancellationFeePercentage}%
              </DetailRow>
            ) : null}
            <DetailRow label="Cancellation fee" numeric>
              {inr(policy.cancellationFeeAmount)}
            </DetailRow>
            <DetailRow label="Refund amount" numeric>
              {inr(policy.refundAmount)}
            </DetailRow>
            <DetailRow label="Refund method">{refundMethodLabel(policy.refundMethod)}</DetailRow>
          </DetailList>
        </Card>

        <Card title="Confirm cancellation">
          <form onSubmit={submit} className="flex flex-col gap-4" noValidate>
            {cancelMutation.isError ? (
              <Alert tone="error" title="We couldn't cancel this booking">
                {describeError(cancelMutation.error)} Your reason is still here — try again.
              </Alert>
            ) : null}

            <Alert tone="warning" title="This is permanent">
              Cancelling releases your slot immediately. Rebooking the same window later isn&apos;t
              guaranteed.
            </Alert>

            {/* id="cancel-reason" is addressed directly by the E2E suite. */}
            <Textarea
              id="cancel-reason"
              label="Reason for cancellation"
              rows={4}
              maxLength={500}
              hint="Tells us what went wrong so we can do better."
              error={errors.reason?.message}
              {...form.register("reason")}
            />

            <div className="flex flex-col gap-3 sm:flex-row">
              {/* The accessible name is load-bearing for the E2E suite, so it
                  stays constant while the request is in flight — `loading`
                  carries the busy state via a spinner and aria-busy. */}
              <Button type="submit" variant="danger" loading={cancelMutation.isPending}>
                Confirm cancellation
              </Button>
              <Button type="button" variant="secondary" onClick={() => router.back()}>
                Go back
              </Button>
            </div>

            <p role="status" aria-live="polite" className="sr-only">
              {cancelMutation.isPending ? "Cancelling your booking, please wait." : ""}
            </p>
          </form>
        </Card>
      </div>
      </div>
    </main>
  );
}
