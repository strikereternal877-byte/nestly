"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { UseQueryResult } from "@tanstack/react-query";
import { motion } from "motion/react";
import Link from "next/link";
import { useState } from "react";
import { Reveal, revealItem } from "@/components/motion";
import { useResetOnChange } from "@/hooks/useResetOnChange";
import { Alert, Badge, Button, Card, Field, Modal, PageHeading, Select, Skeleton, StatTile } from "@/components/ui";
import type { BadgeTone } from "@/components/ui";
import {
  ConfirmDialog,
  DataTable,
  DescriptionList,
  FilterBar,
  Pagination,
  countActiveFilters,
  formatCurrency,
  formatDate,
} from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { BookingsTabs } from "@/components/BookingsTabs";
import { BookingStatusBadge } from "@/components/status-badges";
import { describeError } from "@/lib/api";
import { todayIsoDate } from "@/lib/date";
import { useAdminClaims } from "@/lib/use-admin-claims";
import {
  FREQUENCY_LABELS,
  PAUSE_REASON_LABELS,
  PLAN_STATUS_LABELS,
  RecurrenceFrequency,
  RecurringPlanPauseReason,
  RecurringPlanStatus,
  cancelRecurringPlan,
  describeCadence,
  getRecurringPlan,
  getRecurringPlanReport,
  pauseRecurringPlan,
  resumeRecurringPlan,
  searchRecurringPlans,
} from "../_lib/recurring-plans-api";
import type { RecurringPlanDetail, RecurringPlanListItem } from "../_lib/recurring-plans-api";

const PAGE_SIZE = 20;

const STATUS_OPTIONS = [
  { value: "", label: "Any status" },
  { value: String(RecurringPlanStatus.Active), label: "Active" },
  { value: String(RecurringPlanStatus.Paused), label: "Paused" },
  { value: String(RecurringPlanStatus.Cancelled), label: "Cancelled" },
  { value: String(RecurringPlanStatus.Completed), label: "Completed" },
];

const FREQUENCY_OPTIONS = [
  { value: "", label: "Any cadence" },
  { value: String(RecurrenceFrequency.Weekly), label: "Weekly" },
  { value: String(RecurrenceFrequency.Biweekly), label: "Every 2 weeks" },
  { value: String(RecurrenceFrequency.Monthly), label: "Monthly" },
  { value: String(RecurrenceFrequency.Daily), label: "Every day" },
];

const PAUSE_REASON_OPTIONS = [
  { value: "", label: "Any reason" },
  { value: String(RecurringPlanPauseReason.UnpaidVisits), label: "Visits went unpaid" },
  { value: String(RecurringPlanPauseReason.PaymentFailure), label: "Auto-charge failed" },
  { value: String(RecurringPlanPauseReason.Customer), label: "Paused by the customer" },
  { value: String(RecurringPlanPauseReason.Admin), label: "Paused by support" },
];

const PAYMENT_OPTIONS = [
  { value: "", label: "Any payment" },
  { value: "true", label: "Prepaid (all visits up front)" },
  { value: "false", label: "Paid visit by visit" },
];

const STATUS_TONES: Record<RecurringPlanStatus, "success" | "warning" | "neutral"> = {
  [RecurringPlanStatus.Active]: "success",
  [RecurringPlanStatus.Paused]: "warning",
  [RecurringPlanStatus.Cancelled]: "neutral",
  [RecurringPlanStatus.Completed]: "neutral",
};

interface FilterFormState {
  status: string;
  frequency: string;
  pauseReason: string;
  prepaid: string;
}

const EMPTY_FILTERS: FilterFormState = { status: "", frequency: "", pauseReason: "", prepaid: "" };

type PlanAction = "pause" | "resume" | "cancel";

/** What each reason-taking action says to the admin, and what it tells them afterwards. */
const ACTION_COPY: Record<
  PlanAction,
  {
    title: string;
    description: string;
    confirmLabel: string;
    cancelLabel: string;
    tone: "danger" | "primary";
    reasonLabel: string;
    reasonPlaceholder: string;
    notice: string;
  }
> = {
  pause: {
    title: "Pause this recurring plan?",
    description:
      "No new visits will be booked while it is paused. Visits already booked are unaffected. The customer is told that support paused it and cannot resume it themselves - only support can.",
    confirmLabel: "Pause plan",
    cancelLabel: "Keep running",
    tone: "primary",
    reasonLabel: "Reason for pausing",
    reasonPlaceholder: "Why this plan is being paused",
    notice: "Plan paused - no new visits will be booked, and the customer has been told.",
  },
  resume: {
    title: "Resume this recurring plan?",
    description:
      "Visits are booked again from the plan's next date. Works whether the customer, support or the system paused it. The customer is told.",
    confirmLabel: "Resume plan",
    cancelLabel: "Keep paused",
    tone: "primary",
    reasonLabel: "Reason for resuming",
    reasonPlaceholder: "Why this plan is being resumed (e.g. customer paid)",
    notice: "Plan resumed - visits will be booked again, and the customer has been told.",
  },
  cancel: {
    title: "Cancel this recurring plan?",
    description:
      "No further occurrences will ever be generated. Bookings already created from this plan are unaffected - cancel those individually from All bookings if needed. The customer is told.",
    confirmLabel: "Cancel plan",
    cancelLabel: "Keep plan",
    tone: "danger",
    reasonLabel: "Cancellation reason",
    reasonPlaceholder: "Why this plan is being cancelled",
    notice: "Plan cancelled - no further occurrences will be generated, and the customer has been told.",
  },
};

/**
 * "Why is a paused plan paused?" in a few words, or null for a plan that is not paused. The reason matters to an
 * admin: a plan the system paused because visits went unpaid needs the customer's money sorted before resuming,
 * while one the customer paused needs nothing.
 */
function pauseReasonText(plan: RecurringPlanListItem): string | null {
  if (plan.status !== RecurringPlanStatus.Paused) return null;
  return plan.pauseReason === null ? "Paused" : (PAUSE_REASON_LABELS[plan.pauseReason] ?? "Paused");
}

/** How the plan is paid for, as badges: prepaid (and whether payment is due) or visit by visit (wallet / auto-charge). */
function PaymentBadges({ plan }: { plan: RecurringPlanListItem }) {
  if (plan.prepaidUpfront) {
    return (
      <div className="flex flex-col items-start gap-1">
        <Badge tone="brand">Prepaid</Badge>
        {plan.isAwaitingPrepayment ? <Badge tone="warning">Payment due</Badge> : null}
        {plan.prepaidThroughDate ? (
          <span className="nums text-xs text-fg-subtle">paid through {formatDate(plan.prepaidThroughDate)}</span>
        ) : null}
      </div>
    );
  }

  return (
    <div className="flex flex-wrap gap-1">
      <Badge tone="neutral">Per visit</Badge>
      {plan.applyWalletCredit ? <Badge tone="info">Wallet</Badge> : null}
      {plan.autoChargeEnabled ? <Badge tone="info">Auto-charge</Badge> : null}
    </div>
  );
}

/**
 * Admin visibility into recurring booking plans (task 299,
 * PRODUCT-ENHANCEMENTS.md section 2), mirroring the config/report shape the
 * Coupon and Nestly Coins screens already use: aggregate tiles on top, the
 * per-record list underneath.
 *
 * Besides reading, an admin can act on a plan on the customer's behalf: pause
 * it, resume it (including one the system paused for unpaid visits, once the
 * customer has sorted payment), or cancel it outright. Each takes a reason for
 * the audit trail and tells the customer; none of them touches the bookings a
 * plan has already produced, which are still managed one at a time on the
 * "All bookings" tab. A plan support paused cannot be resumed by the customer.
 *
 * The list says how each plan is paid for (prepaid, wallet, auto-charge) and
 * why a paused one is paused; "Details" adds the customer's wallet balance and
 * the visits the plan has generated.
 *
 * The two volume tiles are separate on purpose and the wording says why: one
 * counts bookings that exist, the other counts plans the scheduler has not
 * reached yet. Adding them together would present a projection as a fact.
 */
export default function RecurringPlansPage() {
  const claims = useAdminClaims();
  const canWrite = claims?.permissions.includes("bookings.write") ?? false;
  const queryClient = useQueryClient();

  const [filters, setFilters] = useState<FilterFormState>(EMPTY_FILTERS);
  const [page, setPage] = useState(1);
  const [fromDate, setFromDate] = useState("");
  const [toDate, setToDate] = useState("");
  const [actionReason, setActionReason] = useState("");
  const [pendingAction, setPendingAction] = useState<{ action: PlanAction; plan: RecurringPlanListItem } | null>(null);
  const [detailPlanId, setDetailPlanId] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [actionNotice, setActionNotice] = useState<string | null>(null);

  const actionMutation = useMutation({
    mutationFn: ({ action, planId, reason }: { action: PlanAction; planId: string; reason: string }) => {
      if (action === "pause") return pauseRecurringPlan(planId, reason);
      if (action === "resume") return resumeRecurringPlan(planId, reason);
      return cancelRecurringPlan(planId, reason);
    },
    onSuccess: (_plan, { action }) => {
      setPendingAction(null);
      setActionReason("");
      setActionError(null);
      setActionNotice(ACTION_COPY[action].notice);
      queryClient.invalidateQueries({ queryKey: ["recurring-plans"] });
    },
    onError: (err) => setActionError(describeError(err)),
  });

  const closeAction = () => {
    setPendingAction(null);
    setActionReason("");
    actionMutation.reset();
  };

  const reportQuery = useQuery({
    queryKey: ["recurring-plans", "report", fromDate, toDate] as const,
    queryFn: () => getRecurringPlanReport(fromDate || undefined, toDate || undefined),
  });

  // Live filtering (no Search button): every field is a dropdown, so there is
  // nothing to debounce - a change applies immediately, same as the Account
  // status field in customers/page.tsx.
  useResetOnChange([filters.status, filters.frequency, filters.pauseReason, filters.prepaid], () => setPage(1));

  const listQuery = useQuery({
    queryKey: ["recurring-plans", "list", filters, page] as const,
    queryFn: () =>
      searchRecurringPlans({
        status: filters.status || undefined,
        frequency: filters.frequency || undefined,
        pauseReason: filters.pauseReason || undefined,
        prepaidUpfront: filters.prepaid || undefined,
        page,
        pageSize: PAGE_SIZE,
      }),
    placeholderData: keepPreviousData,
  });

  const detailQuery = useQuery({
    queryKey: ["recurring-plans", "detail", detailPlanId] as const,
    queryFn: () => getRecurringPlan(detailPlanId!),
    enabled: detailPlanId !== null,
  });

  const onClear = () => {
    setFilters(EMPTY_FILTERS);
    setPage(1);
  };

  const report = reportQuery.data;
  const countFor = (status: RecurringPlanStatus) =>
    report?.byStatus.find((row) => row.status === status)?.planCount ?? 0;

  const columns: DataTableColumn<RecurringPlanListItem>[] = [
    {
      key: "customer",
      header: "Customer",
      cell: (plan) => (
        <Link
          href={`/customers/${plan.customerId}`}
          className="font-medium text-fg underline-offset-4 hover:text-brand-600 hover:underline dark:hover:text-brand-400"
        >
          {plan.customerName}
        </Link>
      ),
    },
    {
      key: "service",
      header: "Service",
      cell: (plan) => plan.serviceName,
    },
    {
      key: "cadence",
      header: "Cadence",
      cell: (plan) => describeCadence(plan),
    },
    {
      key: "payment",
      header: "Payment",
      cell: (plan) => <PaymentBadges plan={plan} />,
    },
    {
      key: "status",
      header: "Status",
      cell: (plan) => {
        const reason = pauseReasonText(plan);
        const skipping =
          plan.status === RecurringPlanStatus.Active && plan.skipUntilDate !== null && plan.skipUntilDate > todayIsoDate();

        return (
          <>
            <Badge tone={STATUS_TONES[plan.status] ?? "neutral"}>
              {PLAN_STATUS_LABELS[plan.status] ?? String(plan.status)}
            </Badge>
            {reason ? <p className="mt-1 max-w-[12rem] text-xs text-fg-subtle">{reason}</p> : null}
            {skipping ? (
              <p className="nums mt-1 max-w-[12rem] text-xs text-fg-subtle">skipping until {formatDate(plan.skipUntilDate)}</p>
            ) : null}
          </>
        );
      },
    },
    {
      key: "progress",
      header: "Delivered",
      numeric: true,
      sortValue: (plan) => plan.completedOccurrenceCount,
      cell: (plan) => (
        <span className="nums">
          {plan.completedOccurrenceCount}
          {plan.occurrenceCount === null ? "" : ` / ${plan.occurrenceCount}`}
        </span>
      ),
    },
    {
      key: "next",
      header: "Next occurrence",
      sortValue: (plan) => plan.nextOccurrenceDate,
      cell: (plan) =>
        // Only an active plan actually has a next visit. Paused, cancelled and
        // completed plans still carry the column in the database, and showing
        // it would read as a commitment nobody is going to keep.
        plan.status === RecurringPlanStatus.Active ? (
          <span className="nums">{formatDate(plan.nextOccurrenceDate)}</span>
        ) : (
          <span className="text-fg-subtle">—</span>
        ),
    },
    {
      key: "created",
      header: "Created",
      sortValue: (plan) => plan.createdAtUtc,
      cell: (plan) => <span className="nums">{formatDate(plan.createdAtUtc)}</span>,
    },
    {
      key: "actions",
      header: "Actions",
      cell: (plan) => (
        <div className="flex flex-wrap gap-2">
          <Button size="sm" variant="secondary" onClick={() => setDetailPlanId(plan.id)}>
            Details
          </Button>
          {canWrite && plan.status === RecurringPlanStatus.Active ? (
            <Button size="sm" variant="secondary" onClick={() => setPendingAction({ action: "pause", plan })}>
              Pause
            </Button>
          ) : null}
          {canWrite && plan.status === RecurringPlanStatus.Paused ? (
            <Button size="sm" variant="secondary" onClick={() => setPendingAction({ action: "resume", plan })}>
              Resume
            </Button>
          ) : null}
          {canWrite && (plan.status === RecurringPlanStatus.Active || plan.status === RecurringPlanStatus.Paused) ? (
            <Button size="sm" variant="danger" onClick={() => setPendingAction({ action: "cancel", plan })}>
              Cancel plan
            </Button>
          ) : null}
        </div>
      ),
    },
  ];

  const copy = pendingAction ? ACTION_COPY[pendingAction.action] : null;

  return (
    <div className="w-full max-w-7xl">
      <PageHeading
        title="Bookings"
        subtitle="Recurring plans: the standing instructions behind repeat jobs, and the work they are about to generate."
      />

      <BookingsTabs />

      {actionError && pendingAction === null ? <Alert tone="error">{actionError}</Alert> : null}
      {actionNotice ? <Alert tone="success">{actionNotice}</Alert> : null}

      <div className="flex flex-col gap-6">
        {report ? (
          <Reveal className="grid grid-cols-2 gap-4 lg:grid-cols-4">
            <motion.div variants={revealItem}>
              <StatTile tone="success" label="Active plans" value={countFor(RecurringPlanStatus.Active).toLocaleString("en-IN")} />
            </motion.div>
            <motion.div variants={revealItem}>
              <StatTile tone="warning" label="Paused plans" value={countFor(RecurringPlanStatus.Paused).toLocaleString("en-IN")} />
            </motion.div>
            <motion.div variants={revealItem}>
              <StatTile
                tone="danger"
                label="Cancelled plans"
                value={countFor(RecurringPlanStatus.Cancelled).toLocaleString("en-IN")}
              />
            </motion.div>
            <motion.div variants={revealItem}>
              <StatTile
                tone="brand"
                label="Bookings already scheduled"
                value={report.upcomingOccurrenceVolume.toLocaleString("en-IN")}
                hint={`Generated by a plan, with a slot between ${formatDate(report.horizonFromDate)} and ${formatDate(report.horizonToDate)}.`}
              />
            </motion.div>
          </Reveal>
        ) : (
          <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
            {Array.from({ length: 4 }, (_, index) => (
              <StatTileSkeleton key={index} />
            ))}
          </div>
        )}

        <Card
          title="Upcoming occurrence volume"
          description="Scope the horizon. Leave both dates empty for the next four weeks."
        >
          <div className="flex flex-wrap items-end gap-4">
            <div className="w-44">
              <Field
                label="From"
                type="date"
                max={toDate || undefined}
                value={fromDate}
                onChange={(event) => setFromDate(event.target.value)}
              />
            </div>
            <div className="w-44">
              <Field
                label="To"
                type="date"
                min={fromDate || undefined}
                value={toDate}
                onChange={(event) => setToDate(event.target.value)}
              />
            </div>
            {fromDate || toDate ? (
              <Button
                type="button"
                variant="secondary"
                onClick={() => {
                  setFromDate("");
                  setToDate("");
                }}
              >
                Reset horizon
              </Button>
            ) : null}
          </div>

          {report ? (
            <div className="mt-5 flex flex-col gap-4">
              <p className="text-sm text-fg-muted">
                <span className="nums font-medium text-fg">
                  {report.plansDueInHorizon.toLocaleString("en-IN")}
                </span>{" "}
                active {report.plansDueInHorizon === 1 ? "plan is" : "plans are"} due in this window but
                have not been generated into bookings yet — the scheduler reaches them closer to the date,
                and an occurrence can still be skipped if no slot is available.
              </p>

              <div className="flex flex-wrap gap-2">
                {report.activeByFrequency.map((row) => (
                  <Badge key={row.frequency} tone="neutral">
                    {FREQUENCY_LABELS[row.frequency] ?? String(row.frequency)}:{" "}
                    <span className="nums">{row.planCount}</span>
                  </Badge>
                ))}
              </div>

              {report.upcomingVolumeByDate.length > 0 ? (
                <ul className="flex flex-wrap gap-2">
                  {report.upcomingVolumeByDate.map((row) => (
                    <li
                      key={row.slotDate}
                      className="rounded-lg border border-line bg-surface-2 px-3 py-1.5 text-sm text-fg-muted"
                    >
                      <span className="nums">{formatDate(row.slotDate)}</span>{" "}
                      <span className="nums font-medium text-fg">{row.bookingCount}</span>
                    </li>
                  ))}
                </ul>
              ) : (
                <p className="text-sm text-fg-subtle">
                  No recurring bookings are scheduled inside this window yet.
                </p>
              )}
            </div>
          ) : (
            <div className="mt-5 flex flex-col gap-3">
              <Skeleton className="h-4 w-3/4" />
              <Skeleton className="h-4 w-1/2" />
            </div>
          )}
        </Card>

        <div>
          <FilterBar
            onClear={onClear}
            activeCount={countActiveFilters(filters)}
            busy={listQuery.isFetching}
          >
            <Select
              label="Status"
              options={STATUS_OPTIONS}
              value={filters.status}
              onChange={(e) => setFilters((f) => ({ ...f, status: e.target.value }))}
            />
            <Select
              label="Cadence"
              options={FREQUENCY_OPTIONS}
              value={filters.frequency}
              onChange={(e) => setFilters((f) => ({ ...f, frequency: e.target.value }))}
            />
            <Select
              label="Paused because"
              options={PAUSE_REASON_OPTIONS}
              value={filters.pauseReason}
              onChange={(e) => setFilters((f) => ({ ...f, pauseReason: e.target.value }))}
            />
            <Select
              label="Payment"
              options={PAYMENT_OPTIONS}
              value={filters.prepaid}
              onChange={(e) => setFilters((f) => ({ ...f, prepaid: e.target.value }))}
            />
          </FilterBar>

          <div className="mt-6 flex flex-col gap-4">
            <DataTable
              title="Recurring plans"
              description="Every standing instruction on the platform, newest first."
              columns={columns}
              rows={listQuery.data?.items}
              rowKey={(plan) => plan.id}
              isLoading={listQuery.isPending}
              isFetching={listQuery.isFetching}
              error={listQuery.error}
              onRetry={() => listQuery.refetch()}
              skeletonRows={8}
              minWidth="1080px"
              caption="Recurring booking plans matching the current filters"
              emptyTitle="No recurring plans match these filters"
              emptyDescription="Clear the filters to see every plan on the platform."
              emptyAction={
                <Button variant="secondary" onClick={onClear}>
                  Clear filters
                </Button>
              }
            />

            {listQuery.data && listQuery.data.totalCount > 0 ? (
              <Pagination
                page={listQuery.data.page}
                pageSize={listQuery.data.pageSize}
                totalCount={listQuery.data.totalCount}
                onPageChange={setPage}
                itemLabel="plan"
                busy={listQuery.isFetching}
              />
            ) : null}
          </div>
        </div>
      </div>

      <ConfirmDialog
        open={pendingAction !== null}
        title={copy?.title ?? ""}
        description={copy?.description}
        confirmLabel={copy?.confirmLabel}
        cancelLabel={copy?.cancelLabel}
        tone={copy?.tone}
        loading={actionMutation.isPending}
        error={actionMutation.isError ? describeError(actionMutation.error) : null}
        onCancel={closeAction}
        onConfirm={() => {
          if (pendingAction && actionReason.trim()) {
            actionMutation.mutate({ action: pendingAction.action, planId: pendingAction.plan.id, reason: actionReason.trim() });
          }
        }}
      >
        {pendingAction ? (
          <div className="flex flex-col gap-3">
            <p className="text-sm text-fg-muted">
              {pendingAction.plan.customerName} — {pendingAction.plan.serviceName}
            </p>
            {pendingAction.action === "resume" && pauseReasonText(pendingAction.plan) ? (
              <p className="text-sm text-fg-subtle">Currently: {pauseReasonText(pendingAction.plan)}.</p>
            ) : null}
            <Field
              label={copy?.reasonLabel ?? "Reason"}
              required
              value={actionReason}
              onChange={(e) => setActionReason(e.target.value)}
              placeholder={copy?.reasonPlaceholder}
              hint="Recorded to the audit trail. Not sent to the customer."
            />
          </div>
        ) : null}
      </ConfirmDialog>

      <Modal
        open={detailPlanId !== null}
        onClose={() => setDetailPlanId(null)}
        title="Recurring plan"
        description="How the plan is paid for, the customer's wallet, and the visits it has produced."
        size="lg"
        footer={
          <Button type="button" variant="secondary" onClick={() => setDetailPlanId(null)}>
            Close
          </Button>
        }
      >
        <PlanDetail query={detailQuery} />
      </Modal>
    </div>
  );
}

/** The plan detail body: the facts an admin needs to answer "why is this plan stuck?", then the visits. */
function PlanDetail({ query }: { query: UseQueryResult<RecurringPlanDetail> }) {
  if (query.isPending) {
    return (
      <div className="flex flex-col gap-3">
        <Skeleton className="h-4 w-3/4" />
        <Skeleton className="h-4 w-1/2" />
        <Skeleton className="h-24 w-full" />
      </div>
    );
  }

  if (query.isError || !query.data) {
    return <Alert tone="error">{describeError(query.error)}</Alert>;
  }

  const { plan, customerMobile, walletBalance, visits } = query.data;
  const reason = pauseReasonText(plan);

  const tone: BadgeTone = STATUS_TONES[plan.status] ?? "neutral";

  return (
    <div className="flex flex-col gap-5">
      <DescriptionList
        columns={2}
        items={[
          {
            label: "Customer",
            value: (
              <>
                <Link href={`/customers/${plan.customerId}`} className="font-medium text-brand-600 hover:underline dark:text-brand-400">
                  {plan.customerName}
                </Link>
                <div className="nums text-xs text-fg-subtle">{customerMobile}</div>
              </>
            ),
          },
          { label: "Service", value: plan.serviceName },
          { label: "Cadence", value: describeCadence(plan) },
          {
            label: "Status",
            value: (
              <>
                <Badge tone={tone}>{PLAN_STATUS_LABELS[plan.status] ?? String(plan.status)}</Badge>
                {reason ? <div className="mt-1 text-xs text-fg-subtle">{reason}</div> : null}
              </>
            ),
          },
          { label: "Payment", value: <PaymentBadges plan={plan} /> },
          {
            label: "Wallet balance",
            value: (
              <>
                <span className="nums font-medium">{formatCurrency(walletBalance)}</span>
                <div className="text-xs text-fg-subtle">
                  {plan.applyWalletCredit ? "Each visit is paid from the wallet as it is booked." : "The wallet is not used for this plan's visits."}
                </div>
              </>
            ),
          },
          { label: "Next occurrence", value: plan.status === RecurringPlanStatus.Active ? formatDate(plan.nextOccurrenceDate) : "—" },
          {
            label: "Delivered",
            value: `${plan.completedOccurrenceCount}${plan.occurrenceCount === null ? "" : ` / ${plan.occurrenceCount}`}`,
          },
          { label: "Skipping until", value: plan.skipUntilDate ? formatDate(plan.skipUntilDate) : "—" },
          { label: "Created", value: formatDate(plan.createdAtUtc) },
        ]}
      />

      <div>
        <h3 className="text-sm font-semibold text-fg">Visits</h3>
        <p className="mt-0.5 text-xs text-fg-subtle">Upcoming first, then the most recent past ones (up to 10 of each).</p>
        {visits.length === 0 ? (
          <p className="mt-3 text-sm text-fg-subtle">This plan has not generated any visits yet.</p>
        ) : (
          <ul className="mt-3 flex flex-col gap-2 text-sm">
            {visits.map((visit) => (
              <li
                key={visit.bookingId}
                className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-line p-3"
              >
                <Link href={`/bookings/${visit.bookingId}`} className="nums font-medium text-fg underline-offset-4 hover:text-brand-600 hover:underline dark:hover:text-brand-400">
                  {visit.bookingReference}
                </Link>
                <span className="nums text-fg-muted">{formatDate(visit.slotDate)}</span>
                <BookingStatusBadge status={visit.status} label={visit.statusLabel} />
                <span className="nums text-fg">{formatCurrency(visit.totalPayable)}</span>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}

/** Matches a `StatTile`'s height so the tiles do not jump when the report lands. */
function StatTileSkeleton() {
  return (
    <div className="rounded-2xl bg-surface p-5 shadow-sm">
      <Skeleton className="h-4 w-28" />
      <Skeleton className="mt-3 h-9 w-16" />
    </div>
  );
}
