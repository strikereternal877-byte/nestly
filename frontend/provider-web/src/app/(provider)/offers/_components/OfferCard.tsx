"use client";

import Link from "next/link";
import { useState } from "react";
import { ErrorState } from "@/components/states";
import { Button, Card, Modal, cx } from "@/components/ui";
import { useJobResponseMutations } from "@/hooks/useJobResponseMutations";
import { formatInr, formatIsoDate, formatTime } from "@/lib/format";
import type { JobListItem } from "@/lib/jobs-types";
import { RecurringJobBadge } from "../../jobs/_components/RecurringJobBadge";
import { useOfferCountdown } from "./useOfferCountdown";

/**
 * One pending offer. Accept/decline are wired to the same mutations
 * `/jobs/[id]` uses (`useJobResponseMutations`) rather than re-derived here,
 * so a provider fielding several offers at once never sees this screen and
 * the detail page disagree about what "accept" does.
 *
 * Full address, not a masked area/locality: `/jobs/[id]`'s "Visible after
 * you accept" placeholder only ever applies to the customer's *phone number*
 * (`MaskMobileUntilAccepted` server-side) - the address itself is shown in
 * full on every screen a provider can see an Assigned job from, `/today`
 * included. Keeping that here rather than inventing a stricter, list-only
 * privacy rule this screen alone would enforce.
 */
export function OfferCard({ job }: { job: JobListItem }) {
  const [confirmDecline, setConfirmDecline] = useState(false);
  const { acceptMutation, rejectMutation } = useJobResponseMutations(job.bookingId, {
    onDeclined: () => setConfirmDecline(false),
  });
  const countdown = useOfferCountdown(job.responseDeadline);
  const isRecurring = job.recurringBookingPlanId !== null;
  const actionError = acceptMutation.error ?? rejectMutation.error;
  const anyPending = acceptMutation.isPending || rejectMutation.isPending;

  return (
    <Card>
      <div className="flex flex-col gap-4">
        <div className="flex items-start justify-between gap-3">
          <div className="flex min-w-0 flex-wrap items-center gap-1.5">
            {isRecurring ? <RecurringJobBadge frequency={job.recurringFrequency} /> : null}
            <Link
              href={`/jobs/${job.bookingId}`}
              className="text-xs font-medium text-fg-muted underline-offset-2 hover:text-fg hover:underline"
            >
              View full details
            </Link>
          </div>
          {/* Net payout, not the customer's gross booking total - see
              JobListItem.netAmountToProvider's doc comment. */}
          <span className="nums shrink-0 text-base font-semibold text-fg">
            {formatInr(job.netAmountToProvider)}
          </span>
        </div>

        <div className="min-w-0">
          <p className="truncate text-lg font-semibold text-fg">{job.customerNameSnapshot}</p>
          <p className="mt-1 nums text-sm text-fg-muted">
            {formatIsoDate(job.slotDate)} · {formatTime(job.slotStartTimeSnapshot)}–
            {formatTime(job.slotEndTimeSnapshot)}
          </p>
          <p className="nums mt-0.5 text-xs text-fg-subtle">{job.bookingReference}</p>
        </div>

        <p className="text-sm leading-relaxed text-fg-subtle">
          {job.addressLine1Snapshot}, {job.addressCitySnapshot} {job.addressPincodeSnapshot}
        </p>

        {/* Same warning chip `/today` and `/jobs/[id]` use for this deadline,
            just with a live "mm:ss" instead of a fixed timestamp - and
            escalated to the danger tone in the last five minutes, since a
            countdown that never changes colour reads as less urgent than one
            that visibly runs out. */}
        <p
          className={cx(
            "flex items-center gap-1.5 rounded-lg px-2.5 py-1.5 text-xs font-medium",
            countdown.isUrgent || countdown.isExpired
              ? "bg-danger-soft text-danger"
              : "bg-warning-soft text-warning",
          )}
        >
          <svg
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            strokeLinecap="round"
            className="h-3.5 w-3.5 shrink-0"
            aria-hidden
          >
            <circle cx="12" cy="12" r="9" />
            <path d="M12 7v5l3 2" />
          </svg>
          <span className="nums">
            {countdown.isExpired ? "Response window expired" : `${countdown.label} left to respond`}
          </span>
        </p>

        {actionError ? <ErrorState title="That action didn't go through" error={actionError} /> : null}

        <div className="flex gap-2.5">
          <Button
            type="button"
            fullWidth
            loading={acceptMutation.isPending}
            disabled={anyPending}
            onClick={() => acceptMutation.mutate()}
            icon={
              <svg
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                strokeWidth="2.5"
                strokeLinecap="round"
                strokeLinejoin="round"
                className="h-5 w-5"
                aria-hidden
              >
                <path d="m5 13 4 4L19 7" />
              </svg>
            }
          >
            Accept
          </Button>
          <Button
            type="button"
            variant="secondary"
            fullWidth
            disabled={anyPending}
            onClick={() => setConfirmDecline(true)}
          >
            Decline
          </Button>
        </div>
      </div>

      {/* Declining is irreversible and hands the job to someone else - same
          one confirm step as `/jobs/[id]`, not a bare button, since these
          are used one-handed and often while walking. */}
      <Modal
        open={confirmDecline}
        onClose={() => setConfirmDecline(false)}
        title="Decline this job?"
        description="It will be offered to another provider and you won't be able to take it back."
        size="sm"
        footer={
          <>
            <Button
              variant="secondary"
              onClick={() => setConfirmDecline(false)}
              disabled={rejectMutation.isPending}
            >
              Keep it
            </Button>
            <Button
              variant="danger"
              loading={rejectMutation.isPending}
              onClick={() => rejectMutation.mutate()}
            >
              Yes, decline
            </Button>
          </>
        }
      >
        <dl className="flex flex-col gap-2 text-sm">
          <div className="flex items-baseline justify-between gap-4">
            <dt className="text-fg-muted">Customer</dt>
            <dd className="min-w-0 truncate text-right font-medium text-fg">
              {job.customerNameSnapshot}
            </dd>
          </div>
          <div className="flex items-baseline justify-between gap-4">
            <dt className="text-fg-muted">When</dt>
            <dd className="nums text-right text-fg">
              {formatIsoDate(job.slotDate)} · {formatTime(job.slotStartTimeSnapshot)}–
              {formatTime(job.slotEndTimeSnapshot)}
            </dd>
          </div>
          <div className="flex items-baseline justify-between gap-4">
            <dt className="text-fg-muted">Your payout</dt>
            <dd className="nums text-right text-fg">{formatInr(job.netAmountToProvider)}</dd>
          </div>
        </dl>
      </Modal>
    </Card>
  );
}
