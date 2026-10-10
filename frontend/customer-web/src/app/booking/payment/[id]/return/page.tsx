"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useParams, useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import {
  BannerBreadcrumb,
  BookingProgress,
  ScreenSkeleton,
} from "@/components/patterns";
import { PageBanner } from "@/components/PageBanner";
import { RequireAuth } from "@/components/RequireAuth";
import { Alert, Button, Card, LinkButton, Spinner } from "@/components/ui";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import { clearDraft } from "@/lib/booking-draft";
import { BookingStatus } from "@/lib/types";
import type { BookingDetail, PaymentTransactionResponse } from "@/lib/types";

const POLL_INTERVAL_MS = 2000;
const POLL_TIMEOUT_MS = 20000;

/**
 * Where PayU sends the browser back to after Hosted Checkout - both its
 * success and failure redirect (surl/furl) point here, since the real
 * outcome lives in our own webhook-updated booking state, not in which URL
 * PayU happened to choose (see PayUPaymentGateway.CreateOrderAsync's own
 * comment: the webhook can race this redirect either way). This page's only
 * job is to re-check that real status and route the customer accordingly -
 * exactly what the sandbox payment page already does after its own
 * /orders/simulate call, just triggered by a page load instead of a button
 * click.
 */
export default function BookingPaymentReturnPage() {
  return (
    <RequireAuth>
      <BookingPaymentReturnScreen />
    </RequireAuth>
  );
}

function BookingPaymentReturnScreen() {
  const router = useRouter();
  const queryClient = useQueryClient();
  const { id } = useParams<{ id: string }>();
  const [timedOut, setTimedOut] = useState(false);

  // A real timer callback (genuinely async, decoupled from the render/effect
  // cycle) rather than a Date.now()/ref comparison computed during render -
  // both of those are impure reads this React version's compiler rejects
  // mid-render.
  useEffect(() => {
    const timer = setTimeout(() => setTimedOut(true), POLL_TIMEOUT_MS);
    return () => clearTimeout(timer);
  }, []);

  const bookingQuery = useQuery({
    queryKey: ["booking", id],
    queryFn: () => apiFetch<BookingDetail>(`${API_V1}/bookings/${id}`, { authenticated: true }),
    // Keep re-checking while still PaymentPending - PayU's redirect can
    // reach the browser before its webhook reaches us. Stops once the
    // timeout above fires rather than polling forever.
    refetchInterval: (query) => {
      const status = query.state.data?.status;
      if (status !== BookingStatus.PaymentPending) return false;
      return timedOut ? false : POLL_INTERVAL_MS;
    },
  });

  const booking = bookingQuery.data;
  const status = booking?.status;
  const isConfirmed = status === BookingStatus.Confirmed;
  const stillPending = status === BookingStatus.PaymentPending && timedOut;
  // BookingLifecycle.cs: Expired has no outgoing transitions - the slot was
  // already released back to the pool (BookingExpirySweepJob), so unlike
  // PaymentFailed this booking can never be retried. Without this branch the
  // page fell through to the generic "Confirming your payment" spinner
  // forever once the 20-minute expiry sweep ran - a dead end worse than the
  // "still confirming" message it followed.
  const isExpired = status === BookingStatus.Expired;

  const transactionQuery = useQuery({
    queryKey: ["payment-transaction", id],
    queryFn: () => apiFetch<PaymentTransactionResponse>(`${API_V1}/payments/bookings/${id}`, { authenticated: true }),
    enabled: status === BookingStatus.PaymentFailed,
  });

  // A checkout the customer abandoned or cancelled before submitting payment
  // details may never trigger the gateway's webhook at all (unlike a real
  // completed attempt, success or decline, which reliably does) - without
  // this, "still confirming" above is where such a booking would sit for the
  // full 20-minute PaymentPending expiry sweep. Firing only once stillPending
  // itself first becomes true - not on every render it stays true - gives a
  // real webhook the full 20s head start this page already grants it before
  // actively asking the gateway instead.
  const verifyMutation = useMutation({
    mutationFn: () => apiFetch(`${API_V1}/payments/bookings/${id}/verify`, { method: "POST", authenticated: true }),
    onSettled: () => bookingQuery.refetch(),
  });

  useEffect(() => {
    if (stillPending) {
      verifyMutation.mutate();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [stillPending]);

  useEffect(() => {
    if (!isConfirmed || !booking) return;
    clearDraft(booking.service.slug);
    queryClient.invalidateQueries({ queryKey: ["bookings"] });
    router.replace(`/booking/success/${id}`);
  }, [isConfirmed, booking, router, id, queryClient]);

  if (bookingQuery.isPending) {
    return (
      <main className="flex w-full flex-col">
        <div className="listing-banner h-[13.5rem] w-full sm:h-[15.5rem]" aria-hidden />
        <ScreenSkeleton cards={1} className="mx-auto w-full max-w-3xl px-4 py-10 sm:px-6 sm:py-14" />
      </main>
    );
  }

  if (bookingQuery.isError || !booking) {
    return (
      <main className="mx-auto w-full max-w-3xl px-4 py-8 sm:px-6 sm:py-12">
        <Alert
          tone="error"
          title="Couldn't check your payment"
          action={
            <Button size="sm" variant="secondary" onClick={() => bookingQuery.refetch()}>
              Retry
            </Button>
          }
        >
          {describeError(bookingQuery.error)}
        </Alert>
      </main>
    );
  }

  if (isConfirmed) {
    return (
      <main className="mx-auto flex w-full max-w-3xl items-center gap-3 px-4 py-12 text-sm text-fg-muted sm:px-6">
        <Spinner />
        Taking you to your confirmation…
      </main>
    );
  }

  const lastAttempt = transactionQuery.data?.attempts.at(-1);

  return (
    <main className="flex w-full flex-col animate-rise">
      <PageBanner
        title="Payment status"
        description={booking.service.name}
        breadcrumb={
          <BannerBreadcrumb
            items={[{ label: "Home", href: "/" }, { label: "My bookings", href: "/bookings" }, { label: "Payment status" }]}
          />
        }
      />

      <div className="mx-auto flex w-full max-w-3xl flex-col gap-6 px-4 py-10 sm:px-6 sm:py-14">
        <BookingProgress current={1} />

        {status === BookingStatus.PaymentFailed ? (
          <Card title="Payment failed">
            <div className="flex flex-col gap-3">
              <Alert tone="error" title="Your payment didn't go through">
                {lastAttempt?.failureReason ?? "No amount was deducted. You can try again right away."}
              </Alert>
              <p className="text-sm leading-relaxed text-fg-muted">
                Your slot is still held on this booking — retrying starts a fresh attempt.
              </p>
              <LinkButton href={`/booking/payment/${id}`} fullWidth>
                Retry payment
              </LinkButton>
            </div>
          </Card>
        ) : isExpired ? (
          <Card title="Payment window expired">
            <div className="flex flex-col gap-3">
              <Alert tone="error" title="This booking's slot was released">
                We didn&apos;t receive payment confirmation in time, so the slot was released back for
                other customers. No amount was deducted for this attempt.
              </Alert>
              <p className="text-sm leading-relaxed text-fg-muted">
                You&apos;ll need to book again to pick a slot.
              </p>
              <div className="flex flex-col gap-2 sm:flex-row">
                <LinkButton href={`/service/${booking.service.slug}`} fullWidth>
                  Book again
                </LinkButton>
                <LinkButton href={`/bookings/${id}`} size="sm" variant="ghost">
                  View booking
                </LinkButton>
              </div>
            </div>
          </Card>
        ) : stillPending ? (
          <Card title="Still confirming your payment">
            <div className="flex flex-col gap-3">
              <Alert tone="warning" title="This is taking longer than usual">
                We haven&apos;t received confirmation from the payment gateway yet. If money was
                deducted, your booking will update automatically within a few minutes.
              </Alert>
              <div className="flex flex-col gap-2 sm:flex-row">
                <Button size="sm" variant="secondary" onClick={() => bookingQuery.refetch()}>
                  Check again
                </Button>
                <LinkButton href={`/bookings/${id}`} size="sm" variant="ghost">
                  View booking
                </LinkButton>
              </div>
            </div>
          </Card>
        ) : (
          <Card title="Confirming your payment">
            <div className="flex items-center gap-3 text-sm text-fg-muted">
              <Spinner />
              Please wait while we confirm your payment…
            </div>
          </Card>
        )}
      </div>
    </main>
  );
}
