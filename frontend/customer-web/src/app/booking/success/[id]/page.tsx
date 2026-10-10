"use client";

import { useQuery } from "@tanstack/react-query";
import { useParams, useSearchParams } from "next/navigation";
import { Suspense } from "react";
import type { ReactNode } from "react";
import {
  BannerBreadcrumb,
  BookingProgress,
  BookingStatusBadge,
  DetailList,
  DetailRow,
  PriceBreakdownList,
  ScreenSkeleton,
  formatCalendarDate,
  formatTimeRange,
  inr,
} from "@/components/patterns";
import { PageBanner } from "@/components/PageBanner";
import { RequireAuth } from "@/components/RequireAuth";
import { Alert, Button, Card, LinkButton } from "@/components/ui";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import type { BookingDetail, PaymentTransactionResponse, ServiceDetail } from "@/lib/types";

/**
 * Booking confirmation page (SRS 11.12.3, tasks 64a-d): booking ID, summary,
 * guidance, and a link into the full detail page.
 *
 * Wrapped in Suspense for useSearchParams (see search/page.tsx).
 */
export default function BookingSuccessPage() {
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
        <BookingSuccessScreen />
      </RequireAuth>
    </Suspense>
  );
}

function BookingSuccessScreen() {
  const { id } = useParams<{ id: string }>();
  // Set by the booking summary page's redirect so this page can show the
  // real cancellation/reschedule policy text - BookingDetailResponse itself
  // doesn't carry policy fields (only BookingSummaryResponse does), so
  // without this the guidance section would have to fabricate the wording.
  const serviceSlug = useSearchParams().get("serviceSlug");

  const bookingQuery = useQuery({
    queryKey: ["booking", id],
    queryFn: () => apiFetch<BookingDetail>(`${API_V1}/bookings/${id}`, { authenticated: true }),
  });

  // A visit bought as part of a prepaid plan was paid together with the rest: this is where the
  // customer sees what their one payment actually came to (the price breakdown below is only this visit).
  const paymentQuery = useQuery({
    queryKey: ["payment-transaction", id],
    queryFn: () =>
      apiFetch<PaymentTransactionResponse>(`${API_V1}/payments/bookings/${id}`, { authenticated: true }),
    retry: false,
  });
  const prepaidTotal = paymentQuery.data?.prepaidCheckoutTotal ?? null;
  const prepaidVisits = paymentQuery.data?.prepaidCheckoutVisitCount ?? null;

  const policyQuery = useQuery({
    queryKey: ["service", serviceSlug],
    queryFn: () => apiFetch<ServiceDetail>(`${API_V1}/services/${serviceSlug}`),
    enabled: !!serviceSlug,
  });

  if (bookingQuery.isPending) {
    return (
      <main className="flex w-full flex-col">
        <div className="listing-banner h-[13.5rem] w-full sm:h-[15.5rem]" aria-hidden />
        <ScreenSkeleton cards={2} className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14" />
      </main>
    );
  }

  if (bookingQuery.isError || !bookingQuery.data) {
    return (
      <main className="mx-auto w-full max-w-7xl px-4 py-8 sm:px-6 sm:py-12">
        <Alert
          tone="error"
          title="Couldn't load your confirmation"
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

  const booking = bookingQuery.data;

  return (
    <main className="flex w-full flex-col animate-rise">
      {/* The E2E suite addresses this by accessible name ("Booking placed!"),
          which is why PageBanner's own title stays exactly that string. */}
      <PageBanner
        title="Booking placed!"
        description={`Booking ID: ${booking.reference}`}
        breadcrumb={<BannerBreadcrumb items={[{ label: "Home", href: "/" }, { label: "Booking placed!" }]} />}
        badge={<BookingStatusBadge status={booking.status} label={booking.statusLabel} />}
      />

      <div className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14">
      <BookingProgress current={2} />

      <div className="mt-6 flex flex-col gap-5">
        <Card title="Booking summary">
          <DetailList>
            <DetailRow label="Service">{booking.service.name}</DetailRow>
            <DetailRow label="Date">{formatCalendarDate(booking.slot.date)}</DetailRow>
            <DetailRow label="Time" numeric>
              {booking.slot.name} · {formatTimeRange(booking.slot.startTime, booking.slot.endTime)}
            </DetailRow>
            <DetailRow label="Address">
              {booking.address.line1}, {booking.address.city} {booking.address.pincode}
            </DetailRow>
          </DetailList>

          <div className="mt-5 border-t border-line pt-4">
            <PriceBreakdownList
              breakdown={booking.price}
              discount={
                booking.couponDiscountAmount
                  ? { code: booking.couponCode, amount: booking.couponDiscountAmount }
                  : null
              }
              total={booking.finalPayable}
              totalLabel={prepaidTotal !== null ? "This visit" : "Amount paid"}
            />
          </div>
        </Card>

        {prepaidTotal !== null && prepaidVisits !== null && prepaidVisits > 1 ? (
          <Card title="Your plan" description="All of your visits were paid for in one payment.">
            <DetailList>
              <DetailRow label="Visits paid for" numeric>
                {prepaidVisits}
              </DetailRow>
              <DetailRow label="Amount paid" numeric>
                {inr(prepaidTotal)}
              </DetailRow>
            </DetailList>
            <p className="mt-3 text-sm leading-relaxed text-fg-muted">
              Each visit is its own booking. Manage or cancel the visits that haven&apos;t happened
              yet from{" "}
              <a
                href="/recurring-bookings"
                className="font-medium text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
              >
                Recurring bookings
              </a>
              ; they&apos;re refunded as per the cancellation policy.
            </p>
          </Card>
        ) : null}

        <Card title="What happens next">
          <ol className="flex flex-col gap-3">
            <NextStep index={1} title="We assign a professional">
              You&apos;ll see their confirmation on this booking as soon as they accept.
            </NextStep>
            <NextStep index={2} title="We keep you posted">
              Status changes appear on the booking page and in your notifications.
            </NextStep>
            <NextStep index={3} title="Plans change? No problem">
              Reschedule or cancel from the booking page, subject to the policy below.
            </NextStep>
          </ol>

          {policyQuery.data?.cancellationPolicy || policyQuery.data?.reschedulePolicy ? (
            <div className="mt-5 border-t border-line pt-4">
              <p className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-fg-subtle">
                Cancellation &amp; rescheduling
              </p>
              <div className="flex flex-col gap-1 text-sm leading-relaxed text-fg-muted">
                {policyQuery.data.cancellationPolicy ? (
                  <p>{policyQuery.data.cancellationPolicy}</p>
                ) : null}
                {policyQuery.data.reschedulePolicy ? <p>{policyQuery.data.reschedulePolicy}</p> : null}
              </div>
            </div>
          ) : null}
        </Card>

        {/* LinkButtons rather than <Link><Button/></Link>: a button inside an
            anchor is invalid HTML and gives assistive tech two nested
            interactive elements for one action. Both names are addressed by
            the E2E suite. `flex-1` (not `fullWidth`) so the two share the row
            equally once `sm:flex-row` kicks in. */}
        <div className="flex flex-col gap-3 sm:flex-row">
          <LinkButton href={`/bookings/${booking.id}`} size="lg" className="flex-1">
            View booking details
          </LinkButton>
          <LinkButton href="/bookings" variant="secondary" size="lg" className="flex-1">
            Go to my bookings
          </LinkButton>
        </div>
      </div>
      </div>
    </main>
  );
}

function NextStep({
  index,
  title,
  children,
}: {
  index: number;
  title: string;
  children: ReactNode;
}) {
  return (
    <li className="flex gap-3">
      <span
        aria-hidden
        className="nums mt-0.5 flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-brand-50 text-xs font-semibold text-brand-700 dark:bg-brand-500/15 dark:text-brand-300"
      >
        {index}
      </span>
      <div className="min-w-0">
        <p className="text-sm font-medium text-fg">{title}</p>
        <p className="mt-0.5 text-sm leading-relaxed text-fg-muted">{children}</p>
      </div>
    </li>
  );
}
