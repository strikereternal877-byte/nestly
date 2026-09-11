"use client";

import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useParams, useRouter, useSearchParams } from "next/navigation";
import { Suspense, useEffect, useRef, useState } from "react";
import {
  BannerBreadcrumb,
  BookingProgress,
  BookingStatusBadge,
  DetailList,
  DetailRow,
  PriceBreakdownList,
  STICKY_BAR_SPACER,
  ScreenSkeleton,
  StickyActionBar,
  formatCalendarDate,
  formatTimeRange,
  inr,
} from "@/components/patterns";
import { BookingHelpLink } from "@/components/BookingHelpLink";
import { PageBanner } from "@/components/PageBanner";
import { RequireAuth } from "@/components/RequireAuth";
import { Alert, Button, Card, Skeleton, Spinner, cx, useToast } from "@/components/ui";
import { API_V1, apiFetch, describeError, errorCode } from "@/lib/api";
import { clearDraft } from "@/lib/booking-draft";
import { BookingStatus } from "@/lib/types";
import type { BookingDetail, PaymentOrderResponse, PaymentTransactionResponse } from "@/lib/types";

/**
 * Sandbox payment page (tasks 76a-c): initiates a gateway order for a
 * PaymentPending/PaymentFailed booking, lets the customer simulate completing
 * payment (there is no real gateway - see SandboxPaymentGateway on the
 * backend), and handles the outcome (success redirects to the confirmation
 * page, failure surfaces a retry affordance).
 *
 * Wrapped in Suspense for useSearchParams (see booking/summary/page.tsx for
 * the same pattern).
 */
export default function BookingPaymentPage() {
  return (
    <Suspense
      fallback={
        <main className="flex w-full flex-col">
          <div className="listing-banner h-[13.5rem] w-full sm:h-[15.5rem]" aria-hidden />
          <ScreenSkeleton cards={2} className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14" />
        </main>
      }
    >
      <RequireAuth>
        <BookingPaymentScreen />
      </RequireAuth>
    </Suspense>
  );
}

function BookingPaymentScreen() {
  const router = useRouter();
  const queryClient = useQueryClient();
  const toast = useToast();
  const { id } = useParams<{ id: string }>();
  const serviceSlug = useSearchParams().get("serviceSlug");
  const successHref = `/booking/success/${id}${serviceSlug ? `?serviceSlug=${serviceSlug}` : ""}`;

  // Bumping this re-runs the order-creation query below with a fresh key,
  // which is exactly what a retry needs: POST /payments/orders again starts
  // a new attempt on a PaymentFailed booking (the backend owns that
  // semantics - see PaymentsController).
  const [attempt, setAttempt] = useState(0);

  const [isPaying, setIsPaying] = useState(false);
  const [payError, setPayError] = useState<string | null>(null);
  const [paymentFailed, setPaymentFailed] = useState(false);
  const [failureReason, setFailureReason] = useState<string | null>(null);

  /**
   * Double-charge guard. The button is disabled while `isPaying`, but a
   * second click can be dispatched in the same tick as the first before that
   * state has rendered - and on a payment screen the cost of losing that race
   * is a duplicate gateway attempt, not just a duplicate request.
   *
   * `navigated` holds the guard shut through the route transition to the
   * confirmation screen. Resetting during that window - which is seconds long
   * on a slow connection - hands the button back to a customer who has already
   * paid and is still looking at "Pay ₹…".
   */
  const inFlight = useRef(false);
  const navigated = useRef(false);

  const bookingQuery = useQuery({
    queryKey: ["booking", id],
    queryFn: () => apiFetch<BookingDetail>(`${API_V1}/bookings/${id}`, { authenticated: true }),
  });

  const booking = bookingQuery.data;
  const isConfirmed = booking?.status === BookingStatus.Confirmed;

  // Already paid (e.g. the customer navigated back here after paying, or the
  // gateway webhook confirmed it out of band) - skip straight to the
  // confirmation page rather than trying to pay again, and retire the draft
  // that was tracking this booking as unpaid.
  useEffect(() => {
    if (!isConfirmed) return;
    if (serviceSlug) clearDraft(serviceSlug);
    queryClient.invalidateQueries({ queryKey: ["bookings"] });
    router.replace(successHref);
  }, [isConfirmed, router, successHref, serviceSlug, queryClient]);

  const orderQuery = useQuery({
    queryKey: ["payment-order", id, attempt],
    queryFn: () =>
      apiFetch<PaymentOrderResponse>(`${API_V1}/payments/orders`, {
        method: "POST",
        authenticated: true,
        body: JSON.stringify({ bookingId: id, idempotencyKey: null }),
      }),
    enabled: !!booking && !isConfirmed,
  });

  const handlePay = async () => {
    if (!orderQuery.data) return;
    if (inFlight.current) return;
    inFlight.current = true;

    setIsPaying(true);
    setPayError(null);
    setPaymentFailed(false);
    setFailureReason(null);

    try {
      await apiFetch(`${API_V1}/payments/orders/simulate`, {
        method: "POST",
        authenticated: true,
        body: JSON.stringify({ gatewayOrderId: orderQuery.data.gatewayOrderId }),
      });

      // The simulate call only confirms the request itself went through -
      // the actual outcome (Confirmed vs PaymentFailed) has to be read back
      // off the booking.
      const refreshed = await bookingQuery.refetch();
      const status = refreshed.data?.status;

      if (status === BookingStatus.Confirmed) {
        // The booking is paid for: the draft that was holding this booking's
        // id has done its job and must not resurrect it as a "continue
        // payment" prompt on the next visit to this service.
        if (serviceSlug) clearDraft(serviceSlug);

        queryClient.invalidateQueries({ queryKey: ["bookings"] });
        navigated.current = true;
        router.push(successHref);
        // Left busy through the route transition so the button cannot be
        // pressed a second time while the next screen is loading.
        return;
      }

      if (status === BookingStatus.PaymentFailed) {
        setPaymentFailed(true);
        try {
          const transaction = await apiFetch<PaymentTransactionResponse>(
            `${API_V1}/payments/bookings/${id}`,
            { authenticated: true },
          );
          const lastAttempt = transaction.attempts[transaction.attempts.length - 1];
          setFailureReason(lastAttempt?.failureReason ?? null);
        } catch {
          // Failure reason is a nice-to-have; the retry flow works without it.
        }
      } else {
        setPayError(
          "We couldn't confirm the payment outcome. Please check your bookings page shortly.",
        );
      }
    } catch (err) {
      // Inline (payError, rendered in the Payment card below) is the
      // detailed explanation; the toast is what makes the failure
      // noticeable at all if the customer isn't looking at the card right
      // when a click on "Pay" comes back with nothing to show for it -
      // matching the toast convention the rest of the app uses for a
      // surfaced action error (see profile/subscription/amc pages).
      const message = describeError(err);
      setPayError(message);
      toast("error", message);
    } finally {
      // Once the payment has gone through and the confirmation screen is
      // loading, the button stays busy for good - see `navigated` above.
      if (navigated.current) return;
      inFlight.current = false;
      setIsPaying(false);
    }
  };

  const handleRetry = () => {
    setPaymentFailed(false);
    setPayError(null);
    setFailureReason(null);
    setAttempt((a) => a + 1);
  };

  if (bookingQuery.isPending) {
    return (
      <main className="flex w-full flex-col">
        <div className="listing-banner h-[13.5rem] w-full sm:h-[15.5rem]" aria-hidden />
        <ScreenSkeleton cards={2} className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14" />
      </main>
    );
  }

  if (bookingQuery.isError || !booking) {
    return (
      <main className="mx-auto w-full max-w-7xl px-4 py-8 sm:px-6 sm:py-12">
        <Alert
          tone="error"
          title="Couldn't load this booking"
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
      <main className="mx-auto flex w-full max-w-7xl items-center gap-3 px-4 py-12 text-sm text-fg-muted sm:px-6">
        <Spinner />
        Taking you to your confirmation…
      </main>
    );
  }

  const amount = orderQuery.data?.amount ?? booking.price.totalPayable;

  return (
    <main className="flex w-full flex-col animate-rise">
      <PageBanner
        title="Confirm and pay"
        description={booking.service.name}
        breadcrumb={
          <BannerBreadcrumb
            items={[{ label: "Home", href: "/" }, { label: "My bookings", href: "/bookings" }, { label: "Confirm and pay" }]}
          />
        }
      />

      <div
        className={cx(
          "mx-auto grid w-full max-w-7xl gap-6 px-4 py-10 sm:px-6 sm:py-14 md:grid-cols-[1fr_22rem]",
          STICKY_BAR_SPACER,
        )}
      >
      <div className="flex min-w-0 flex-col gap-6">
        <BookingProgress current={1} />

        <Card title="Booking">
          <DetailList>
            <DetailRow label="Booking ID" numeric>
              <span className="break-all">{booking.reference}</span>
            </DetailRow>
            <DetailRow label="Status">
              <BookingStatusBadge status={booking.status} label={booking.statusLabel} />
            </DetailRow>
            <DetailRow label="Date">{formatCalendarDate(booking.slot.date)}</DetailRow>
            <DetailRow label="Time" numeric>
              {booking.slot.name} · {formatTimeRange(booking.slot.startTime, booking.slot.endTime)}
            </DetailRow>
            <DetailRow label="Address">
              {booking.address.line1}, {booking.address.city} {booking.address.pincode}
            </DetailRow>
          </DetailList>
        </Card>

        <Card title="Price breakdown">
          <PriceBreakdownList
            breakdown={booking.price}
            discount={
              booking.couponDiscountAmount
                ? { code: booking.couponCode, amount: booking.couponDiscountAmount }
                : null
            }
            total={booking.finalPayable}
            totalLabel="Amount payable"
          />
        </Card>
      </div>

      <aside className="flex flex-col gap-4 md:sticky md:top-20 md:self-start">
        {orderQuery.isPending ? (
          <Card title="Payment">
            <div className="flex flex-col gap-3" aria-hidden>
              <Skeleton className="h-5 w-40" />
              <Skeleton className="h-12 rounded-lg" />
            </div>
            <p role="status" className="mt-3 text-sm text-fg-muted">
              Preparing your payment…
            </p>
          </Card>
        ) : orderQuery.isError ? (
          <Card title="Payment">
            {errorCode(orderQuery.error) === "Payment.NoProviderAvailable" ? (
              // Retrying would just re-run the identical check against the
              // identical slot and fail the same way - the customer needs a
              // different slot, not another attempt at this one.
              <Alert
                tone="error"
                title="No one's available for this slot"
                action={
                  <Button
                    size="sm"
                    variant="secondary"
                    onClick={() =>
                      router.push(
                        `/booking/summary${serviceSlug ? `?serviceSlug=${serviceSlug}` : ""}`,
                      )
                    }
                  >
                    Choose another slot
                  </Button>
                }
              >
                {describeError(orderQuery.error)}
              </Alert>
            ) : (
              <Alert
                tone="error"
                title="Couldn't start the payment"
                action={
                  <Button size="sm" variant="secondary" onClick={handleRetry}>
                    Try again
                  </Button>
                }
              >
                {describeError(orderQuery.error)}
              </Alert>
            )}
          </Card>
        ) : paymentFailed ? (
          <Card title="Payment failed">
            <div className="flex flex-col gap-3">
              <Alert tone="error" title="Your payment didn't go through">
                {failureReason ?? "No amount was deducted. You can try again right away."}
              </Alert>
              <p className="text-sm leading-relaxed text-fg-muted">
                Your slot is still held on this booking — retrying starts a fresh attempt with
                everything you already entered.
              </p>
              <Button type="button" fullWidth onClick={handleRetry}>
                Retry payment
              </Button>
            </div>
          </Card>
        ) : orderQuery.data ? (
          <Card
            title="Payment"
            description="Sandbox simulation of the payment gateway — no real payment is processed."
          >
            <div className="flex flex-col gap-4">
              <div className="rounded-xl border border-line bg-surface-2 px-4 py-3">
                <p className="text-xs font-medium uppercase tracking-wide text-fg-muted">
                  Amount payable
                </p>
                <p className="nums mt-1 text-2xl font-semibold text-fg">
                  {inr(orderQuery.data.amount)}{" "}
                  <span className="text-sm font-medium text-fg-subtle">
                    {orderQuery.data.currency}
                  </span>
                </p>
              </div>

              {payError ? (
                <Alert
                  tone="error"
                  title="Payment status unclear"
                  action={
                    <Button size="sm" variant="secondary" onClick={handleRetry}>
                      Start over
                    </Button>
                  }
                >
                  {payError}
                </Alert>
              ) : null}
            </div>
          </Card>
        ) : null}

        {orderQuery.data && !paymentFailed ? (
          <StickyActionBar>
            <div className="flex items-baseline justify-between gap-3 md:hidden">
              <span className="text-xs font-medium uppercase tracking-wide text-fg-muted">
                Amount payable
              </span>
              <span className="nums text-lg font-semibold text-fg">{inr(amount)}</span>
            </div>

            {/* The accessible name is load-bearing for the E2E suite
                (/Pay ₹.*\(Sandbox\)/), so it stays constant while the request
                is in flight - the busy state is carried by the spinner and
                aria-busy that `loading` adds, not by relabelling the button. */}
            <Button type="button" size="lg" fullWidth loading={isPaying} onClick={handlePay}>
              {`Pay ${inr(orderQuery.data.amount)} (Sandbox)`}
            </Button>

            <p role="status" aria-live="polite" className="sr-only">
              {isPaying ? "Processing your payment, please wait." : ""}
            </p>
          </StickyActionBar>
        ) : null}

        <BookingHelpLink />
      </aside>
      </div>
    </main>
  );
}
