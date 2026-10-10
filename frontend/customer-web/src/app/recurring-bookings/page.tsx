"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useState } from "react";
import {
  BannerBreadcrumb,
  DetailList,
  DetailRow,
  formatCalendarDate,
  formatTimeRange,
  inr,
  recurringFrequencyLabel,
  recurringPlanStatusTone,
} from "@/components/patterns";
import { Reveal, RevealItem } from "@/components/motion";
import { PageBanner } from "@/components/PageBanner";
import { RequireAuth } from "@/components/RequireAuth";
import { ChangePlanTimeDialog, SkipVisitsDialog } from "@/components/RecurringPlanDialogs";
import {
  Alert,
  Badge,
  Button,
  Card,
  EmptyState,
  LinkButton,
  Modal,
  Skeleton,
} from "@/components/ui";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import { todayIsoDate } from "@/lib/date";
import {
  WALLET_LOW_BALANCE_VISITS,
  cancellationChargeSentence,
  visitsLabel,
} from "@/lib/recurring-plan";
import {
  DAY_OF_WEEK_LABELS,
  RecurringBookingPauseReason,
  RecurringBookingPlanStatus,
  RecurringBookingRecurrenceFrequency,
} from "@/lib/types";
import type {
  RecurringBookingPlanResponse,
  UpcomingOccurrenceResponse,
  WalletBalanceResponse,
} from "@/lib/types";
import { useWalletTopUpConfig } from "@/lib/wallet-topup";

/**
 * Manage recurring booking plans (task 187): pause/resume/cancel and a
 * projected-upcoming-dates preview per plan. The create flow lives at
 * /recurring-bookings/new, reached from the booking summary and booking
 * detail pages.
 */
export default function RecurringBookingsPage() {
  return (
    <RequireAuth>
      <RecurringBookingsScreen />
    </RequireAuth>
  );
}

function RecurringBookingsScreen() {
  const query = useQuery({
    queryKey: ["recurring-booking-plans"],
    queryFn: () =>
      apiFetch<RecurringBookingPlanResponse[]>(`${API_V1}/recurring-booking-plans`, {
        authenticated: true,
      }),
  });

  return (
    <main className="flex w-full flex-col">
      <PageBanner
        title="Recurring bookings"
        description="Manage your standing service schedules."
        breadcrumb={<BannerBreadcrumb items={[{ label: "Home", href: "/" }, { label: "Recurring bookings" }]} />}
      />

      <div className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14">
      {query.isPending ? (
        <ul className="flex flex-col gap-4" aria-hidden>
          {[0, 1].map((row) => (
            <li key={row}>
              <Skeleton className="h-56 rounded-2xl" />
            </li>
          ))}
        </ul>
      ) : query.isError ? (
        <Alert
          tone="error"
          title="Couldn't load your recurring plans"
          action={
            <Button size="sm" variant="secondary" onClick={() => query.refetch()}>
              Retry
            </Button>
          }
        >
          {describeError(query.error)}
        </Alert>
      ) : query.data.length === 0 ? (
        <EmptyState
          title="No recurring bookings yet"
          description="Set one up from any service's booking summary and it will repeat on the schedule you choose."
          action={<LinkButton href="/categories">Browse services</LinkButton>}
        />
      ) : (
        <Reveal as="ul" className="flex flex-col gap-4">
          {query.data.map((plan) => (
            <RevealItem key={plan.id}>
              <PlanCard plan={plan} />
            </RevealItem>
          ))}
        </Reveal>
      )}
      </div>
    </main>
  );
}

function statusLabel(status: RecurringBookingPlanStatus, prepaid = false): string {
  // A prepaid plan is "Completed" the moment its last visit is booked - all of them are, at purchase -
  // which reads as "all done" to a customer whose visits are still ahead.
  if (prepaid && status === RecurringBookingPlanStatus.Completed) {
    return "All visits booked";
  }

  switch (status) {
    case RecurringBookingPlanStatus.Active:
      return "Active";
    case RecurringBookingPlanStatus.Paused:
      return "Paused";
    case RecurringBookingPlanStatus.Cancelled:
      return "Cancelled";
    case RecurringBookingPlanStatus.Completed:
      return "Completed";
    default:
      return "Unknown";
  }
}

function dayDescription(plan: RecurringBookingPlanResponse): string {
  if (plan.frequency === RecurringBookingRecurrenceFrequency.Monthly) {
    return `on day ${plan.recurrenceDayOfMonth}`;
  }
  return plan.recurrenceDayOfWeek !== null
    ? `on ${DAY_OF_WEEK_LABELS[plan.recurrenceDayOfWeek]}s`
    : "";
}

/**
 * Says why a pay-as-you-go plan is paused and what to do about it. The reason matters: a plan the customer
 * paused themselves needs nothing but a tap on Resume, while one we paused because visits went unpaid needs the
 * money sorted first - otherwise resuming just produces more unpaid visits.
 */
function PausedNotice({
  plan,
  canAddMoney,
  bookedAhead,
}: {
  plan: RecurringBookingPlanResponse;
  canAddMoney: boolean;
  /** Visits already booked that the pause did not touch. */
  bookedAhead: number;
}) {
  if (plan.pauseReason === RecurringBookingPauseReason.UnpaidVisits) {
    return (
      <Alert
        tone="warning"
        title="Paused - recent visits weren't paid"
        action={
          canAddMoney ? (
            <LinkButton size="sm" href="/wallet?addMoney=1">
              Add money
            </LinkButton>
          ) : undefined
        }
      >
        A visit that isn&apos;t paid isn&apos;t carried out, and the last ones went unpaid, so we paused this
        plan. {canAddMoney ? "Add money to your wallet, then" : "Once your payment is sorted,"} tap Resume
        and we&apos;ll carry on from your next visit.
      </Alert>
    );
  }

  if (plan.pauseReason === RecurringBookingPauseReason.Admin) {
    return (
      <Alert tone="warning" title="Paused by our support team">
        No new visits are booked while it&apos;s paused.
        {bookedAhead > 0
          ? ` The ${visitsLabel(bookedAhead)} already booked still ${bookedAhead === 1 ? "goes" : "go"} ahead and ${bookedAhead === 1 ? "is" : "are"} still charged unless you cancel ${bookedAhead === 1 ? "it" : "them"} from My bookings.`
          : ""}{" "}
        Only our support team can resume it, so please contact support when you&apos;re ready.
      </Alert>
    );
  }

  if (plan.pauseReason === RecurringBookingPauseReason.PaymentFailure) {
    return (
      <Alert tone="warning" title="Paused - a payment didn't go through">
        We paused this plan after a payment failed. Check your payment details, then tap Resume.
      </Alert>
    );
  }

  return (
    <Alert tone="info" title="This plan is paused">
      No new visits are booked while it&apos;s paused.
      {bookedAhead > 0
        ? ` The ${visitsLabel(bookedAhead)} already booked still ${bookedAhead === 1 ? "goes" : "go"} ahead and ${bookedAhead === 1 ? "is" : "are"} still charged unless you cancel ${bookedAhead === 1 ? "it" : "them"} from My bookings.`
        : ""}{" "}
      Tap Resume and we&apos;ll carry on from your next visit.
    </Alert>
  );
}

function PlanCard({ plan }: { plan: RecurringBookingPlanResponse }) {
  const queryClient = useQueryClient();
  const [showUpcoming, setShowUpcoming] = useState(false);
  const [confirmingCancel, setConfirmingCancel] = useState(false);
  const [confirmingPause, setConfirmingPause] = useState(false);
  const [skipOpen, setSkipOpen] = useState(false);
  const [timeOpen, setTimeOpen] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["recurring-booking-plans"] });

  const runAction = useMutation({
    mutationFn: (action: "pause" | "resume" | "cancel") =>
      apiFetch<RecurringBookingPlanResponse>(
        `${API_V1}/recurring-booking-plans/${plan.id}/${action}`,
        { method: "POST", authenticated: true },
      ),
    onSuccess: () => {
      setActionError(null);
      setConfirmingCancel(false);
      setConfirmingPause(false);
      invalidate();
    },
    onError: (err) => setActionError(describeError(err)),
  });

  const upcomingQuery = useQuery({
    queryKey: ["recurring-booking-plan-upcoming", plan.id],
    queryFn: () =>
      apiFetch<UpcomingOccurrenceResponse[]>(
        `${API_V1}/recurring-booking-plans/${plan.id}/occurrences/upcoming?count=5`,
        { authenticated: true },
      ),
    enabled: showUpcoming,
  });

  const isTerminal =
    plan.status === RecurringBookingPlanStatus.Cancelled ||
    plan.status === RecurringBookingPlanStatus.Completed;

  // A prepaid plan reads "Completed" as soon as its last visit is booked (they are all booked at
  // purchase), while most of them are still ahead - so it stays cancellable, and cancelling refunds
  // the visits that haven't happened.
  const canCancel =
    !isTerminal ||
    (plan.prepaidUpfront && plan.status === RecurringBookingPlanStatus.Completed);

  // "Skip visits" and "Change time" only make sense where visits are generated one at a time: a prepaid
  // plan's visits already exist (and are paid for), so those are rescheduled or cancelled individually.
  const isActive = plan.status === RecurringBookingPlanStatus.Active;
  const canAdjustVisits = isActive && !plan.prepaidUpfront;
  // Support paused it: the server refuses a customer's Resume, so the card does not offer one.
  const pausedBySupport =
    plan.status === RecurringBookingPlanStatus.Paused && plan.pauseReason === RecurringBookingPauseReason.Admin;
  const isSkipping = isActive && plan.skipUntilDate !== null && plan.skipUntilDate > todayIsoDate();

  // Visits already booked: a pause and a time change leave them exactly as they are, so the card says how many.
  const bookedAhead = plan.upcomingBookedVisitDates ?? [];
  const chargeSentence = cancellationChargeSentence(plan.cancellationFreeWindowHours, plan.lateCancellationFeePercentage);

  // A wallet-paid plan shows what the wallet holds against what a visit costs - "wallet exhausted" is a state the
  // customer can see coming, not only one they are told about afterwards.
  const usesWallet = plan.applyWalletCredit && !plan.prepaidUpfront && !isTerminal;
  const walletQuery = useQuery({
    queryKey: ["wallet-balance"],
    queryFn: () => apiFetch<WalletBalanceResponse>(`${API_V1}/wallet/balance`, { authenticated: true }),
    enabled: usesWallet,
  });
  const walletBalance = walletQuery.data?.balance ?? null;
  const walletCoversVisits =
    walletBalance !== null && plan.visitAmount !== null && plan.visitAmount > 0
      ? Math.floor(walletBalance / plan.visitAmount)
      : null;
  const walletIsLow = isActive && walletCoversVisits !== null && walletCoversVisits < WALLET_LOW_BALANCE_VISITS;

  // The "Add money" links are offered only when top-ups are switched on.
  const topUpConfigQuery = useWalletTopUpConfig(plan.status === RecurringBookingPlanStatus.Paused || usesWallet);
  const canAddMoney = topUpConfigQuery.data?.enabled === true;

  const progress =
    !plan.prepaidUpfront && plan.occurrenceCount && plan.occurrenceCount > 0
      ? Math.min(100, Math.round((plan.completedOccurrenceCount / plan.occurrenceCount) * 100))
      : null;

  return (
    <Card
      title={plan.serviceName}
      description={`${recurringFrequencyLabel(plan.frequency)} ${dayDescription(plan)}`.trim()}
      actions={
        <Badge tone={recurringPlanStatusTone(plan.status)}>{statusLabel(plan.status, plan.prepaidUpfront)}</Badge>
      }
    >
      <div className="flex flex-col gap-4">
        <DetailList>
          {!isTerminal ? (
            <DetailRow label="Next visit">
              {plan.slotWindowName && plan.slotStartTime && plan.slotEndTime
                ? `${formatCalendarDate(plan.nextOccurrenceDate)} · ${plan.slotWindowName} ${formatTimeRange(plan.slotStartTime, plan.slotEndTime)}`
                : formatCalendarDate(plan.nextOccurrenceDate)}
            </DetailRow>
          ) : null}
          {!isTerminal && !plan.prepaidUpfront && plan.visitAmount !== null ? (
            <DetailRow label="Price per visit" numeric>
              {inr(plan.visitAmount)}
            </DetailRow>
          ) : null}
          {!isTerminal && !plan.prepaidUpfront ? (
            <DetailRow label="Already booked">
              {bookedAhead.length > 0
                ? `${visitsLabel(bookedAhead.length)} - next ${formatCalendarDate(bookedAhead[0])}`
                : "None"}
            </DetailRow>
          ) : null}
          <DetailRow label={plan.prepaidUpfront ? "Visits booked" : "Visits completed"} numeric>
            {plan.completedOccurrenceCount}
            {plan.occurrenceCount ? ` of ${plan.occurrenceCount}` : ""}
          </DetailRow>
          <DetailRow label="Started">{formatCalendarDate(plan.startDate)}</DetailRow>
          {plan.prepaidUpfront ? (
            <DetailRow label="Payment">
              {plan.prepaidThroughDate
                ? `Paid in advance, through ${formatCalendarDate(plan.prepaidThroughDate)}`
                : "Paid in advance"}
            </DetailRow>
          ) : (
            <DetailRow label="Payment">
              {plan.applyWalletCredit ? "Each visit, from your wallet" : "Each visit, as it's booked"}
            </DetailRow>
          )}
          {usesWallet && walletBalance !== null ? (
            <DetailRow label="Wallet balance">
              {inr(walletBalance)}
              {walletCoversVisits !== null ? ` · covers ${visitsLabel(walletCoversVisits)}` : ""}
            </DetailRow>
          ) : null}
          {isSkipping && plan.skipUntilDate ? (
            <DetailRow label="Skipping visits until">{formatCalendarDate(plan.skipUntilDate)}</DetailRow>
          ) : null}
          {plan.endDate ? (
            <DetailRow label="Ends">{formatCalendarDate(plan.endDate)}</DetailRow>
          ) : plan.occurrenceCount === null ? (
            <DetailRow label="Ends">Runs until you cancel</DetailRow>
          ) : null}
        </DetailList>

        {progress !== null ? (
          <div>
            <div
              className="h-1.5 w-full overflow-hidden rounded-full bg-surface-3"
              role="progressbar"
              aria-valuemin={0}
              aria-valuemax={100}
              aria-valuenow={progress}
              aria-label={`${plan.serviceName} plan progress`}
            >
              <div
                className="h-full rounded-full bg-brand-600 transition-[width] duration-slow ease-out"
                style={{ width: `${progress}%` }}
              />
            </div>
            <p className="nums mt-1.5 text-xs text-fg-subtle">{progress}% complete</p>
          </div>
        ) : null}

        {!plan.prepaidUpfront && plan.visitAwaitingPayment ? (
          <Alert
            tone="warning"
            title={plan.applyWalletCredit ? "Your wallet couldn't cover this visit" : "A visit is waiting for payment"}
            action={
              <LinkButton size="sm" href={`/booking/payment/${plan.visitAwaitingPayment.bookingId}`}>
                Pay now
              </LinkButton>
            }
          >
            Your {formatCalendarDate(plan.visitAwaitingPayment.slotDate)} visit needs{" "}
            {inr(plan.visitAwaitingPayment.amountDue)}
            {plan.applyWalletCredit ? " more (the wallet covered the rest)" : ""}. Pay before it&apos;s released - an
            unpaid visit isn&apos;t carried out, and no professional is sent for it.
          </Alert>
        ) : null}

        {walletIsLow && !plan.visitAwaitingPayment ? (
          <Alert
            tone="warning"
            title={
              walletCoversVisits === 0
                ? "Your wallet can't cover the next visit"
                : `Your wallet covers only ${visitsLabel(walletCoversVisits ?? 0)}`
            }
            action={
              canAddMoney ? (
                <LinkButton size="sm" href="/wallet?addMoney=1">
                  Add money
                </LinkButton>
              ) : undefined
            }
          >
            Keep it topped up so each visit is paid automatically. A visit that isn&apos;t paid in time is released.
          </Alert>
        ) : null}

        {plan.pendingPrepaymentBookingId ? (
          <Alert
            tone="warning"
            title="Payment due for your next visits"
            action={
              <LinkButton size="sm" href={`/booking/payment/${plan.pendingPrepaymentBookingId}`}>
                Pay now
              </LinkButton>
            }
          >
            Your next visits are held for you until you pay for them in one payment. If you
            don&apos;t, they&apos;re released and this plan pauses.
          </Alert>
        ) : null}

        {plan.prepaidUpfront
          && plan.status === RecurringBookingPlanStatus.Paused
          && !plan.pendingPrepaymentBookingId
          && !pausedBySupport ? (
          <Alert tone="info" title="This plan is paused">
            Resume it and we&apos;ll set up your next visits, ready to pay for in one payment.
          </Alert>
        ) : null}

        {plan.status === RecurringBookingPlanStatus.Paused && (!plan.prepaidUpfront || pausedBySupport) ? (
          <PausedNotice plan={plan} canAddMoney={canAddMoney} bookedAhead={bookedAhead.length} />
        ) : null}

        {actionError ? (
          <Alert tone="error" title="That didn't work">
            {actionError}
          </Alert>
        ) : null}

        <div className="flex flex-wrap gap-2 border-t border-line pt-4">
          {plan.status === RecurringBookingPlanStatus.Active ? (
            <Button
              type="button"
              variant="secondary"
              onClick={() => setConfirmingPause(true)}
            >
              Pause
            </Button>
          ) : null}
          {plan.status === RecurringBookingPlanStatus.Paused && !pausedBySupport ? (
            <Button
              type="button"
              variant="secondary"
              loading={runAction.isPending && runAction.variables === "resume"}
              onClick={() => runAction.mutate("resume")}
            >
              Resume
            </Button>
          ) : null}
          {canAdjustVisits ? (
            <>
              <Button type="button" variant="secondary" onClick={() => setSkipOpen(true)}>
                Skip visits
              </Button>
              <Button type="button" variant="secondary" onClick={() => setTimeOpen(true)}>
                Change time
              </Button>
            </>
          ) : null}
          {!isTerminal && !plan.prepaidUpfront ? (
            <Button type="button" variant="secondary" onClick={() => setShowUpcoming((v) => !v)}>
              {showUpcoming ? "Hide upcoming" : "Show upcoming"}
            </Button>
          ) : null}
          {canCancel ? (
            <Button
              type="button"
              variant="ghost"
              className="text-danger hover:bg-danger-soft hover:text-danger"
              onClick={() => setConfirmingCancel(true)}
            >
              {plan.prepaidUpfront ? "Cancel remaining visits" : "Cancel plan"}
            </Button>
          ) : null}
        </div>

        {showUpcoming ? (
          <div className="border-t border-line pt-4">
            <p className="mb-2 text-xs font-semibold uppercase tracking-wide text-fg-subtle">
              Next visits
            </p>
            {upcomingQuery.isPending ? (
              <div className="flex flex-col gap-2" aria-hidden>
                <Skeleton className="h-3.5 w-40" />
                <Skeleton className="h-3.5 w-36" />
              </div>
            ) : upcomingQuery.isError ? (
              <Alert
                tone="error"
                action={
                  <Button size="sm" variant="secondary" onClick={() => upcomingQuery.refetch()}>
                    Retry
                  </Button>
                }
              >
                {describeError(upcomingQuery.error)}
              </Alert>
            ) : upcomingQuery.data.length === 0 ? (
              <p className="text-sm text-fg-muted">No more visits scheduled.</p>
            ) : (
              <ul className="flex flex-col gap-1.5">
                {upcomingQuery.data.map((occurrence) => (
                  <li
                    key={occurrence.scheduledDate}
                    className="flex items-center gap-2 text-sm text-fg-muted"
                  >
                    <span aria-hidden className="h-1.5 w-1.5 rounded-full bg-brand-600" />
                    <span className="nums">{formatCalendarDate(occurrence.scheduledDate)}</span>
                    {occurrence.isProjected ? (
                      <span className="text-xs text-fg-subtle">(projected)</span>
                    ) : null}
                  </li>
                ))}
              </ul>
            )}
          </div>
        ) : null}
      </div>

      {canAdjustVisits ? (
        <>
          <SkipVisitsDialog plan={plan} open={skipOpen} onClose={() => setSkipOpen(false)} />
          <ChangePlanTimeDialog plan={plan} open={timeOpen} onClose={() => setTimeOpen(false)} />
        </>
      ) : null}

      {/* Pausing looks harmless and is easy to misread as "stop everything", so it says what it does not do. */}
      <Modal
        open={confirmingPause}
        onClose={() => setConfirmingPause(false)}
        title="Pause this plan?"
        description="Pausing is free. No new visits are booked while it's paused, and you can resume any time."
        size="sm"
        footer={
          <>
            <Button type="button" variant="secondary" onClick={() => setConfirmingPause(false)}>
              Keep it running
            </Button>
            <Button
              type="button"
              loading={runAction.isPending && runAction.variables === "pause"}
              onClick={() => runAction.mutate("pause")}
            >
              Pause plan
            </Button>
          </>
        }
      >
        <div className="flex flex-col gap-3">
          {bookedAhead.length > 0 ? (
            <Alert tone="warning" title={`${visitsLabel(bookedAhead.length)} already booked`}>
              Pausing doesn&apos;t cancel {bookedAhead.length === 1 ? "it" : "them"} - {bookedAhead.length === 1 ? "it still goes" : "they still go"}{" "}
              ahead (next: {formatCalendarDate(bookedAhead[0])}) and {bookedAhead.length === 1 ? "is" : "are"} still charged. To stop{" "}
              {bookedAhead.length === 1 ? "it" : "one"}, cancel from{" "}
              <Link href="/bookings" className="font-medium underline underline-offset-4">
                My bookings
              </Link>
              . {chargeSentence}
            </Alert>
          ) : (
            <p className="text-sm leading-relaxed text-fg-muted">
              You have no visits booked right now, so nothing else is affected.
            </p>
          )}
          {plan.visitAwaitingPayment ? (
            <p className="text-sm leading-relaxed text-fg-muted">
              Your {formatCalendarDate(plan.visitAwaitingPayment.slotDate)} visit is still waiting for payment; if
              it isn&apos;t paid it is released.
            </p>
          ) : null}
        </div>
      </Modal>

      {/* Cancelling a plan stops every future visit at once, so it gets an
          explicit confirmation step rather than firing on the first click. */}
      <Modal
        open={confirmingCancel}
        onClose={() => setConfirmingCancel(false)}
        title={plan.prepaidUpfront ? "Cancel the remaining visits?" : "Cancel this recurring plan?"}
        description={
          plan.prepaidUpfront
            ? `Every ${plan.serviceName} visit in this plan that hasn't happened yet is cancelled, and you're refunded for it as per the cancellation policy. ${chargeSentence} The booking you placed together with this plan isn't cancelled.`
            : `No further ${plan.serviceName} visits will be booked. ${
                bookedAhead.length > 0
                  ? `${visitsLabel(bookedAhead.length)} already booked stay in place and are still charged - cancel ${bookedAhead.length === 1 ? "it" : "them"} from My bookings if you don't want ${bookedAhead.length === 1 ? "it" : "them"}. `
                  : ""
              }${chargeSentence}`
        }
        size="sm"
        footer={
          <>
            <Button type="button" variant="secondary" onClick={() => setConfirmingCancel(false)}>
              Keep plan
            </Button>
            <Button
              type="button"
              variant="danger"
              loading={runAction.isPending && runAction.variables === "cancel"}
              onClick={() => runAction.mutate("cancel")}
            >
              {plan.prepaidUpfront ? "Yes, cancel them" : "Yes, cancel plan"}
            </Button>
          </>
        }
      >
        <p className="text-sm leading-relaxed text-fg-muted">
          This can&apos;t be undone — you&apos;d need to set up a new plan to resume the schedule.
        </p>
      </Modal>
    </Card>
  );
}
