"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useParams } from "next/navigation";
import { ChatWidget } from "@/components/ChatWidget";
import { RequireAuth } from "@/components/RequireAuth";
import { Alert, Button, Card, PageHeading } from "@/components/ui";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import { BookingStatus, ChatContextType, RefundMethod, RefundStatus, RefundType } from "@/lib/types";
import type {
  BookingCompletionProofResponse,
  BookingDetail,
  BookingStatusTimelineEntry,
  PriceBreakdown,
  RefundTransactionResponse,
} from "@/lib/types";

/** Both reachable only via RefundPending/Refunded in BookingLifecycle - see BookingStatusMapper.cs. */
const REFUND_STATUSES: BookingStatus[] = [BookingStatus.RefundPending, BookingStatus.Refunded];

/**
 * Booking detail with status timeline, refund info, and action CTAs (SRS
 * 11.13.2, tasks 65a-c).
 */
export default function BookingDetailPage() {
  return (
    <RequireAuth>
      <BookingDetailScreen />
    </RequireAuth>
  );
}

function BookingDetailScreen() {
  const { id } = useParams<{ id: string }>();

  const query = useQuery({
    queryKey: ["booking", id],
    queryFn: () => apiFetch<BookingDetail>(`${API_V1}/bookings/${id}`, { authenticated: true }),
  });

  if (query.isPending) {
    return <main className="mx-auto w-full max-w-3xl px-6 py-12 text-sm text-neutral-500">Loading…</main>;
  }

  if (query.isError || !query.data) {
    return (
      <main className="mx-auto w-full max-w-3xl px-6 py-12">
        <Alert>{describeError(query.error)}</Alert>
      </main>
    );
  }

  const booking = query.data;

  return (
    <main className="mx-auto grid w-full max-w-4xl gap-6 px-6 py-10 md:grid-cols-[1fr_320px]">
      <div className="flex flex-col gap-6">
        <PageHeading title={booking.service.name} subtitle={`Booking ID: ${booking.id}`} />

        <Card title="Address">
          <address className="text-sm not-italic leading-relaxed text-neutral-700 dark:text-neutral-300">
            {booking.address.line1}
            {booking.address.line2 ? <>, {booking.address.line2}</> : null}
            {booking.address.landmark ? <>, near {booking.address.landmark}</> : null}
            <br />
            {booking.address.city}, {booking.address.state} {booking.address.pincode}
            <br />
            {booking.address.contactName} · {booking.address.contactMobile}
          </address>
        </Card>

        <Card title="Slot">
          <p className="text-sm">
            {booking.slot.date} · {booking.slot.name} ({booking.slot.startTime.slice(0, 5)}–
            {booking.slot.endTime.slice(0, 5)})
          </p>
        </Card>

        <Card title="Price & payment">
          <PriceRows breakdown={booking.price} />
        </Card>

        {REFUND_STATUSES.includes(booking.status) ? <RefundInfoCard booking={booking} /> : null}

        {booking.status === BookingStatus.Completed ? <CompletionProofCard bookingId={booking.id} /> : null}

        <Card title="Status timeline">
          <Timeline entries={booking.timeline} />
        </Card>

        <ChatWidget contextType={ChatContextType.Booking} contextId={booking.id} />
      </div>

      <aside className="flex flex-col gap-3 md:sticky md:top-6 md:self-start">
        <ActionCtas booking={booking} />
      </aside>
    </main>
  );
}

/**
 * Refund details (task 65b, 78c, SRS 11.13.2 "refund details if any"). Now
 * backed by the real refund transaction(s) (GET /bookings/{id}/refunds)
 * instead of fabricating an amount from the booking's total.
 */
function RefundInfoCard({ booking }: { booking: BookingDetail }) {
  const refundsQuery = useQuery({
    queryKey: ["booking-refunds", booking.id],
    queryFn: () =>
      apiFetch<RefundTransactionResponse[]>(`${API_V1}/bookings/${booking.id}/refunds`, {
        authenticated: true,
      }),
  });

  if (refundsQuery.isPending) {
    return (
      <Card title="Refund">
        <p className="text-sm text-neutral-500">Loading refund details…</p>
      </Card>
    );
  }

  if (refundsQuery.isError) {
    return (
      <Card title="Refund">
        <Alert>{describeError(refundsQuery.error)}</Alert>
      </Card>
    );
  }

  const refunds = refundsQuery.data;

  if (refunds.length === 0) {
    // The booking reached a refund-related status but no refund transaction
    // has been recorded yet - a reasonable "still processing" fallback
    // rather than rendering nothing.
    return (
      <Card title="Refund">
        <dl className="flex flex-col gap-2 text-sm">
          <div className="flex items-center justify-between">
            <dt className="text-neutral-600 dark:text-neutral-400">Status</dt>
            <dd className="font-medium">{booking.statusLabel}</dd>
          </div>
        </dl>
        <p className="mt-3 text-sm text-neutral-600 dark:text-neutral-400">
          Your refund is being processed. We&apos;ll update this page once it&apos;s initiated.
        </p>
      </Card>
    );
  }

  return (
    <Card title="Refund">
      <div className="flex flex-col gap-4">
        {refunds.map((refund) => (
          <dl
            key={refund.id}
            className="flex flex-col gap-2 border-t border-black/10 pt-3 text-sm first:border-t-0 first:pt-0 dark:border-white/15"
          >
            <div className="flex items-center justify-between">
              <dt className="text-neutral-600 dark:text-neutral-400">Amount</dt>
              <dd className="font-medium">₹{refund.amount.toFixed(2)}</dd>
            </div>
            <div className="flex items-center justify-between">
              <dt className="text-neutral-600 dark:text-neutral-400">Type</dt>
              <dd className="font-medium">{refundTypeLabel(refund.type)}</dd>
            </div>
            <div className="flex items-center justify-between">
              <dt className="text-neutral-600 dark:text-neutral-400">Method</dt>
              <dd className="font-medium">{refundMethodLabel(refund.method)}</dd>
            </div>
            <div className="flex items-center justify-between">
              <dt className="text-neutral-600 dark:text-neutral-400">Status</dt>
              <dd className="font-medium">{refundStatusLabel(refund.status)}</dd>
            </div>
            {refund.gatewayRefundRef ? (
              <div className="flex items-center justify-between">
                <dt className="text-neutral-600 dark:text-neutral-400">Reference</dt>
                <dd className="font-medium">{refund.gatewayRefundRef}</dd>
              </div>
            ) : null}
          </dl>
        ))}
      </div>
    </Card>
  );
}

function refundTypeLabel(type: RefundType): string {
  return type === RefundType.Full ? "Full refund" : "Partial refund";
}

function refundMethodLabel(method: RefundMethod): string {
  return method === RefundMethod.Gateway ? "Original payment method" : "Wallet credit";
}

function refundStatusLabel(status: RefundStatus): string {
  switch (status) {
    case RefundStatus.Initiated:
      return "Initiated";
    case RefundStatus.Processing:
      return "Processing";
    case RefundStatus.Refunded:
      return "Refunded";
    case RefundStatus.Failed:
      return "Failed";
    default:
      return "Unknown";
  }
}

function Timeline({ entries }: { entries: BookingStatusTimelineEntry[] }) {
  if (entries.length === 0) {
    return <p className="text-sm text-neutral-500">No status history yet.</p>;
  }

  return (
    <ol className="flex flex-col gap-4">
      {entries.map((entry, i) => (
        <li key={`${entry.toStatus}-${entry.changedAtUtc}-${i}`} className="flex gap-3">
          <div className="flex flex-col items-center">
            <span className="mt-1 h-2.5 w-2.5 rounded-full bg-black dark:bg-white" aria-hidden="true" />
            {i < entries.length - 1 ? <span className="w-px flex-1 bg-black/15 dark:bg-white/20" /> : null}
          </div>
          <div className="pb-2 text-sm">
            <p className="font-medium">{entry.toStatusLabel}</p>
            <p className="text-neutral-500">{new Date(entry.changedAtUtc).toLocaleString()}</p>
            {entry.reason ? <p className="mt-1 text-neutral-600 dark:text-neutral-400">{entry.reason}</p> : null}
          </div>
        </li>
      ))}
    </ol>
  );
}

/**
 * Action CTAs (task 65c, SRS 11.13.2), now wired to the real post-booking
 * flows (tasks 89a-c, 90, 91, 92): cancellation, reschedule, review and
 * support ticketing all have live endpoints, so every CTA here points at a
 * real route. Cancel/reschedule/review are shown unconditionally - each
 * target page gates on its own eligibility endpoint and explains why an
 * action isn't available, the same pattern the review flow requires.
 */
function ActionCtas({ booking }: { booking: BookingDetail }) {
  const canRebook = !!booking.service.slug;

  return (
    <div className="flex flex-col gap-3 rounded-xl border border-black/10 bg-white p-5 dark:border-white/15 dark:bg-neutral-900">
      {canRebook ? (
        <Link href={`/services/${booking.service.slug}`}>
          <Button type="button" className="w-full">
            Book again
          </Button>
        </Link>
      ) : null}
      <Link href={`/bookings/${booking.id}/reschedule`}>
        <Button type="button" variant="secondary" className="w-full">
          Reschedule booking
        </Button>
      </Link>
      {canRebook ? (
        <Link href={`/recurring-bookings/new?serviceSlug=${booking.service.slug}`}>
          <Button type="button" variant="secondary" className="w-full">
            Set up recurring booking
          </Button>
        </Link>
      ) : null}
      <Link href={`/bookings/${booking.id}/cancel`}>
        <Button type="button" variant="secondary" className="w-full">
          Cancel booking
        </Button>
      </Link>
      <Link href={`/bookings/${booking.id}/review`}>
        <Button type="button" variant="secondary" className="w-full">
          Leave a review
        </Button>
      </Link>
      {booking.status === BookingStatus.Completed ? (
        <Link href="/refer-earn">
          <Button type="button" variant="secondary" className="w-full">
            Refer a friend & earn
          </Button>
        </Link>
      ) : null}
      <Link href="/bookings">
        <Button type="button" variant="secondary" className="w-full">
          Back to my bookings
        </Button>
      </Link>
      <Link href={`/support/new?bookingId=${booking.id}`}>
        <Button type="button" variant="secondary" className="w-full">
          Raise an issue
        </Button>
      </Link>
      {/* Kept as an unconditional fallback alongside the real ticketing flow
          above - useful if the in-app flow itself is the thing that's broken. */}
      <a href={`mailto:support@nestly.app?subject=${encodeURIComponent(`Booking ${booking.id}`)}`}>
        <Button type="button" variant="secondary" className="w-full">
          Email support
        </Button>
      </a>
    </div>
  );
}

// Mirrors PriceCalculator.tsx's PriceSummary intentionally rather than
// importing it - see booking/summary/page.tsx's PriceRows for the same note.
function PriceRows({ breakdown }: { breakdown: PriceBreakdown }) {
  return (
    <dl className="flex flex-col gap-1.5 text-sm">
      <Row label={`Base price × ${breakdown.quantity}`} value={breakdown.baseTotal} />
      {breakdown.addOnLineItems.map((item) => (
        <Row key={item.addOnId} label={`${item.name} × ${item.quantity}`} value={item.lineTotal} />
      ))}
      {breakdown.visitCharge > 0 ? <Row label="Visit charge" value={breakdown.visitCharge} /> : null}
      <Row label={`Tax (${breakdown.taxPercentage}%)`} value={breakdown.taxAmount} />
      {breakdown.platformFee > 0 ? <Row label="Platform fee" value={breakdown.platformFee} /> : null}
      <div className="mt-1 flex items-center justify-between border-t border-black/10 pt-2 font-semibold dark:border-white/15">
        <dt>Total payable</dt>
        <dd>₹{breakdown.totalPayable.toFixed(2)}</dd>
      </div>
    </dl>
  );
}

function Row({ label, value }: { label: string; value: number }) {
  return (
    <div className="flex items-center justify-between text-neutral-600 dark:text-neutral-400">
      <dt>{label}</dt>
      <dd>₹{value.toFixed(2)}</dd>
    </div>
  );
}

/** Photo + checklist evidence the provider submitted at job completion (tasks 195-198). */
function CompletionProofCard({ bookingId }: { bookingId: string }) {
  const query = useQuery({
    queryKey: ["booking-completion-proof", bookingId],
    queryFn: () =>
      apiFetch<BookingCompletionProofResponse | undefined>(`${API_V1}/bookings/${bookingId}/completion-proof`, {
        authenticated: true,
      }),
  });

  if (query.isPending) {
    return (
      <Card title="Completion proof">
        <p className="text-sm text-neutral-500">Loading…</p>
      </Card>
    );
  }

  if (query.isError) {
    return (
      <Card title="Completion proof">
        <Alert>{describeError(query.error)}</Alert>
      </Card>
    );
  }

  const proof = query.data;
  if (!proof) {
    return null;
  }

  return (
    <Card title="Completion proof">
      <p className="text-sm text-neutral-600 dark:text-neutral-400">
        Submitted {new Date(proof.submittedAtUtc).toLocaleString()} · {proof.photoRefs.length} photo(s)
      </p>
      {proof.checklistAnswers.length > 0 ? (
        <ul className="mt-3 flex flex-col gap-1 text-sm">
          {proof.checklistAnswers.map((answer, i) => (
            <li key={i}>
              {answer.completed ? "✓" : "○"} {answer.item}
              {answer.notes ? ` — ${answer.notes}` : ""}
            </li>
          ))}
        </ul>
      ) : null}
    </Card>
  );
}
