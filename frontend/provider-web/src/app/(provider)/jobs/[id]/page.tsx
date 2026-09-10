"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useRef, useState } from "react";
import type { FormEvent, ReactNode } from "react";
import { ChatPanel } from "@/components/ChatPanel";
import { StickyActionBar } from "@/components/patterns";
import { ErrorState, NotYetAvailable } from "@/components/states";
import {
  Alert,
  Button,
  Card,
  Divider,
  Field,
  IconButton,
  Modal,
  PageHeading,
  Skeleton,
  SkeletonText,
  Spinner,
  Textarea,
  useToast,
} from "@/components/ui";
import { useJobStatusLive } from "@/hooks/useJobStatusLive";
import { isLocationShareable, useLocationSharing } from "@/hooks/useLocationSharing";
import { ApiError, describeError, isNotImplemented } from "@/lib/api";
import { formatDateTime, formatInr, formatIsoDate, formatSignedInr, formatTime } from "@/lib/format";
import {
  acceptJob,
  completeJob,
  extendJobResponseDeadline,
  getCompletionVerification,
  getCustomerRating,
  getCustomerRatingEligibility,
  getJobDetail,
  markJobArrived,
  markJobEnRoute,
  rejectJob,
  startJob,
  submitCompletionProof,
  submitCompletionVerification,
  submitCustomerRating,
  uploadCompletionPhoto,
} from "@/lib/jobs-api";
import { JobStatus, jobStatusLabel } from "@/lib/jobs-types";
import { JobStatusBadge } from "../_components/JobStatusBadge";
import type { BookingCompletionProofResponse, CompletionChecklistAnswer } from "@/lib/jobs-types";
import type { LocationSharingStatus } from "@/hooks/useLocationSharing";

/**
 * Statuses whose job can actually have completion evidence to read. The
 * backend's GET /jobs/{id}/completion-verification resolves the provider's
 * *accepted* assignment and 404s otherwise (ProviderJobService's
 * ResolveAcceptedAsync), and evidence only ever exists once work has started -
 * so anything earlier than InProgress has nothing to fetch, and the
 * non-accepted terminal states (Rejected/Reassigned/Withdrawn) 404 outright.
 * Completed is kept in: the evidence stays readable after the job is done.
 * Doubles as the render gate for the verification cards below, so the fetch
 * and the UI that consumes it can never drift apart.
 */
const VERIFIABLE_STATUSES: ReadonlySet<JobStatus> = new Set([
  JobStatus.InProgress,
  JobStatus.Completed,
]);

function hasCompletionVerification(status: JobStatus): boolean {
  return VERIFIABLE_STATUSES.has(status);
}

/**
 * Job detail (docs/PROVIDER.md's `booking_provider_assignment` bridge table):
 * accept/reject/start/complete actions plus completion proof submission.
 * Same 501 caveat as the list page - if the backend for this surface is not
 * deployed, this renders an explicit "not yet available" state instead of a
 * hard error.
 *
 * The accept/decline decision is the single most consequential thing a
 * provider does in this app and it is time-boxed by `responseDeadline`, so it
 * sits at the very top of the screen, above the detail: the answer must never
 * be something you have to scroll to find on a phone.
 */
export default function JobDetailPage() {
  const params = useParams<{ id: string }>();
  const jobId = params.id;
  const queryClient = useQueryClient();
  const toast = useToast();
  const [proofRef, setProofRef] = useState("");
  const [confirmDecline, setConfirmDecline] = useState(false);

  const query = useQuery({ queryKey: ["provider-job", jobId], queryFn: () => getJobDetail(jobId) });
  const jobStatus = query.data?.status;
  const verificationQuery = useQuery({
    queryKey: ["provider-job-completion-verification", jobId],
    queryFn: () => getCompletionVerification(jobId),
    // Only fetch where the endpoint can answer (see VERIFIABLE_STATUSES) -
    // firing it for every other status just 404s. Explicit `!== undefined`
    // because JobStatus.Assigned is 0 and would fail a truthiness check.
    enabled: jobStatus !== undefined && hasCompletionVerification(jobStatus),
  });

  useJobStatusLive(jobId);
  const { status: locationSharingStatus } = useLocationSharing(
    jobId,
    !!query.data && isLocationShareable(query.data.status),
  );

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: ["provider-job", jobId] });
    queryClient.invalidateQueries({ queryKey: ["provider-job-completion-verification", jobId] });
    // The list's status column and "needs a response" styling are now stale.
    queryClient.invalidateQueries({ queryKey: ["provider-jobs"] });
  };

  const acceptMutation = useMutation({
    mutationFn: () => acceptJob(jobId),
    onSuccess: () => {
      invalidate();
      toast("success", "Job accepted. It's yours.");
    },
    // Row 38, docs/OPEN-FIXES-FEATURES.csv: a 401 here is already retried
    // transparently by apiFetch (task d9f557c), so anything that still
    // surfaces here is either a real business rejection (4xx other than
    // 401 - AlreadyResponded, no outstanding assignment) or a genuine
    // non-provider-fault failure (a transient 5xx, or no ApiError at all -
    // a network error, since fetch throws a plain Error/TypeError for
    // those). Only the latter extends the response window - a business
    // rejection is accurate as-is and extending it would just delay the
    // provider learning the offer is gone.
    onError: (error) => {
      const isTransientFailure = !(error instanceof ApiError) || error.status >= 500;
      if (isTransientFailure) {
        extendJobResponseDeadline(jobId)
          .then(invalidate)
          .catch(() => {
            // Best-effort safety net - if this also fails there is nothing
            // more the client can do; the error banner below still tells
            // the provider to retry, and the original deadline still stands.
          });
      }
    },
  });
  const rejectMutation = useMutation({
    mutationFn: () => rejectJob(jobId),
    onSuccess: () => {
      setConfirmDecline(false);
      invalidate();
      toast("info", "Job declined. It will be offered to another provider.");
    },
  });
  const startMutation = useMutation({
    mutationFn: () => startJob(jobId),
    onSuccess: () => {
      invalidate();
      toast("success", "Job started.");
    },
  });
  const enRouteMutation = useMutation({
    mutationFn: () => markJobEnRoute(jobId),
    onSuccess: () => {
      invalidate();
      toast("success", "Customer notified you're on your way.");
    },
  });
  const arrivedMutation = useMutation({
    mutationFn: () => markJobArrived(jobId),
    onSuccess: () => {
      invalidate();
      toast("success", "Customer notified you've arrived.");
    },
  });
  const completeMutation = useMutation({
    mutationFn: () => completeJob(jobId),
    onSuccess: () => {
      invalidate();
      toast("success", "Job marked complete.");
    },
  });
  const proofMutation = useMutation({
    mutationFn: () => submitCompletionProof(jobId, { proofRef }),
    onSuccess: () => {
      setProofRef("");
      invalidate();
      toast("success", "Completion proof submitted.");
    },
  });

  const anyActionPending =
    acceptMutation.isPending ||
    rejectMutation.isPending ||
    startMutation.isPending ||
    enRouteMutation.isPending ||
    arrivedMutation.isPending ||
    completeMutation.isPending;

  const backLink = (
    <Link
      href="/jobs"
      className="inline-flex items-center gap-1.5 text-sm font-medium text-fg-muted transition-colors duration-fast ease-out hover:text-fg"
    >
      <svg
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinecap="round"
        strokeLinejoin="round"
        className="h-4 w-4"
        aria-hidden
      >
        <path d="m15 18-6-6 6-6" />
      </svg>
      All jobs
    </Link>
  );

  if (query.isPending) {
    return <JobDetailSkeleton backLink={backLink} />;
  }

  if (query.isError && isNotImplemented(query.error)) {
    return (
      <div>
        <PageHeading title="Job detail" breadcrumbs={backLink} />
        <NotYetAvailable
          title="This job isn't available yet"
          description="Job assignment is still being built on the platform side. Check back once your account can receive bookings."
          action={
            <Link href="/jobs">
              <Button variant="secondary">Back to jobs</Button>
            </Link>
          }
        />
      </div>
    );
  }

  if (query.isError) {
    return (
      <div>
        <PageHeading title="Job detail" breadcrumbs={backLink} />
        <ErrorState
          title="Couldn't load this job"
          error={query.error}
          onRetry={() => query.refetch()}
          isRetrying={query.isRefetching}
        />
      </div>
    );
  }

  const job = query.data;
  const actionError =
    acceptMutation.error ??
    rejectMutation.error ??
    startMutation.error ??
    enRouteMutation.error ??
    arrivedMutation.error ??
    completeMutation.error ??
    proofMutation.error;

  const showVerification = hasCompletionVerification(job.status);

  // Task #345: exactly one primary action is ever pinned to the bottom of
  // the screen at a time. InProgress renders both the "Finishing up" card
  // and the verification form, so these two are mutually exclusive - before
  // verification is submitted, submitting it is the thing blocking
  // progress; once it exists, completing the job is. Two simultaneous
  // `StickyActionBar`s would otherwise stack/overlap at the viewport's
  // bottom edge.
  const verificationIsPrimaryAction =
    job.status === JobStatus.InProgress && !verificationQuery.isPending && !verificationQuery.data;
  const completeIsPrimaryAction = job.status === JobStatus.InProgress && !!verificationQuery.data;

  return (
    <div className="animate-rise">
      <PageHeading
        breadcrumbs={backLink}
        title={job.customerNameSnapshot}
        subtitle={`${job.bookingReference} · ${formatIsoDate(job.slotDate)} · ${formatTime(job.slotStartTimeSnapshot)}–${formatTime(job.slotEndTimeSnapshot)}`}
        actions={<JobStatusBadge status={job.status} />}
      />

      {actionError ? (
        <div className="mb-4">
          <ErrorState title="That action didn't go through" error={actionError} />
        </div>
      ) : null}

      {/* The decision, before anything else on the page. */}
      {job.status === JobStatus.Assigned ? (
        <section className="mb-6 overflow-hidden rounded-2xl border border-warning/40 bg-surface shadow-sm">
          <div className="border-b border-warning/25 bg-warning-soft px-5 py-3">
            <p className="text-sm font-semibold text-warning">
              {job.responseDeadline
                ? `Respond by ${formatDateTime(job.responseDeadline)}`
                : "This job is waiting on your response"}
            </p>
            <p className="mt-0.5 text-xs text-warning/90">
              If you don&apos;t respond in time, this job goes to another provider.
            </p>
          </div>

          <div className="flex flex-col gap-2.5 p-5">
            <Button
              type="button"
              size="lg"
              fullWidth
              loading={acceptMutation.isPending}
              disabled={anyActionPending}
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
              Accept this job
            </Button>
            <Button
              type="button"
              variant="secondary"
              size="lg"
              fullWidth
              disabled={anyActionPending}
              onClick={() => setConfirmDecline(true)}
            >
              Decline
            </Button>
            <p className="nums text-center text-sm text-fg-muted">
              You&apos;ll earn{" "}
              <span className="font-semibold text-fg">{formatInr(job.netAmountToProvider)}</span>{" "}
              after platform commission
            </p>
          </div>
        </section>
      ) : null}

      {/* Declining is irreversible and hands the job to someone else, so it
          asks once rather than firing on a mis-tap - these buttons are used
          one-handed, often while walking. */}
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
          <DetailRow label="Customer">{job.customerNameSnapshot}</DetailRow>
          <DetailRow label="When">
            {formatIsoDate(job.slotDate)} · {formatTime(job.slotStartTimeSnapshot)}–
            {formatTime(job.slotEndTimeSnapshot)}
          </DetailRow>
          <DetailRow label="Your payout">
            <span className="nums">{formatInr(job.netAmountToProvider)}</span>
          </DetailRow>
        </dl>
      </Modal>

      <div className="flex flex-col gap-6">
        <Card title="Job details">
          <dl className="flex flex-col gap-4">
            <DetailRow label="Customer">
              <span className="font-medium text-fg">{job.customerNameSnapshot}</span>
              {job.status === JobStatus.Accepted ||
              job.status === JobStatus.EnRoute ||
              job.status === JobStatus.Arrived ||
              job.status === JobStatus.InProgress ||
              job.status === JobStatus.Completed ? (
                <a
                  href={`tel:${job.customerMobileSnapshot}`}
                  className="mt-1 flex w-fit items-center gap-1.5 rounded-lg bg-surface-2 px-2.5 py-1.5 text-sm font-medium text-brand-600 transition-colors duration-fast ease-out hover:bg-surface-3 dark:text-brand-400"
                >
                  <svg
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="2"
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    className="h-4 w-4"
                    aria-hidden
                  >
                    <path d="M5 4h4l2 5-2.5 1.5a11 11 0 0 0 5 5L15 13l5 2v4a1 1 0 0 1-1 1A16 16 0 0 1 4 5a1 1 0 0 1 1-1Z" />
                  </svg>
                  <span className="nums">{job.customerMobileSnapshot}</span>
                </a>
              ) : (
                // The number is masked server-side until the job is accepted
                // (see MaskMobileUntilAccepted in ProviderJobService) - this
                // just renders that placeholder as plain text instead of a
                // clickable tel: link, since there is nothing to call.
                <span className="mt-1 flex w-fit items-center gap-1.5 rounded-lg bg-surface-2 px-2.5 py-1.5 text-sm font-medium text-fg-muted">
                  <svg
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="2"
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    className="h-4 w-4"
                    aria-hidden
                  >
                    <path d="M3 3l18 18M5 4h4l2 5-2.5 1.5a11 11 0 0 0 5 5L15 13l5 2v4a1 1 0 0 1-1 1A16 16 0 0 1 4 5a1 1 0 0 1 1-1Z" />
                  </svg>
                  Visible after you accept
                </span>
              )}
            </DetailRow>

            <DetailRow label="Address">
              <span className="leading-relaxed">
                {job.addressLine1Snapshot}
                {job.addressLine2Snapshot ? `, ${job.addressLine2Snapshot}` : ""}
                {job.addressLandmarkSnapshot ? `, near ${job.addressLandmarkSnapshot}` : ""}
                <br />
                {job.addressCitySnapshot} {job.addressStateSnapshot}{" "}
                <span className="nums">{job.addressPincodeSnapshot}</span>
              </span>
            </DetailRow>

            <DetailRow label="Slot">
              <span className="nums">
                {formatIsoDate(job.slotDate)} · {formatTime(job.slotStartTimeSnapshot)}–
                {formatTime(job.slotEndTimeSnapshot)}
              </span>
            </DetailRow>

            <DetailRow label="Assigned">
              <span className="nums">{formatDateTime(job.assignedAt)}</span>
            </DetailRow>

            {/* Only while the offer is actually open. The deadline is what the
                provider has to accept or decline by, so once they have
                accepted it describes nothing - it used to keep counting down
                on screen through En route, Arrived and In progress, which
                reads as though the job could still be taken away. Same
                `Assigned` gate the response banner above uses. */}
            {job.status === JobStatus.Assigned && job.responseDeadline ? (
              <DetailRow label="Response deadline">
                <span className="nums">{formatDateTime(job.responseDeadline)}</span>
              </DetailRow>
            ) : null}

            {job.notes ? <DetailRow label="Notes">{job.notes}</DetailRow> : null}
          </dl>

          {job.items.length > 0 ? (
            <>
              <Divider className="my-5" />
              <p className="mb-3 text-xs font-semibold uppercase tracking-wide text-fg-muted">
                Items
              </p>
              <ul className="flex flex-col gap-2">
                {job.items.map((item, index) => (
                  <li key={index} className="flex items-baseline justify-between gap-4 text-sm">
                    <span className="min-w-0 text-fg">
                      {item.nameSnapshot}
                      <span className="nums text-fg-subtle"> × {item.quantity}</span>
                    </span>
                    <span className="nums shrink-0 text-fg-muted">
                      {formatInr(item.unitPriceSnapshot * item.quantity)}
                    </span>
                  </li>
                ))}
              </ul>
            </>
          ) : null}

          <Divider className="my-5" />
          {/* Payout breakdown (bug fix, docs/OPEN-FIXES-FEATURES.csv "Payout
              figure"): the booking total the customer paid (service value +
              tax + platform fee) is never the provider's earning - the
              platform commission (job.commissionAmount, backend's
              ICommissionService) comes out of it first. */}
          <div className="flex flex-col gap-2">
            <div className="flex items-baseline justify-between gap-4 text-sm">
              <span className="text-fg-muted">Booking total</span>
              <span className="nums text-fg-muted">{formatInr(job.totalPayableSnapshot)}</span>
            </div>
            <div className="flex items-baseline justify-between gap-4 text-sm">
              <span className="text-fg-muted">Platform commission</span>
              <span className="nums text-fg-muted">
                {formatSignedInr(job.commissionAmount, /* isDebit */ true)}
              </span>
            </div>
            <Divider />
            <div className="flex items-baseline justify-between gap-4">
              <span className="text-sm font-medium text-fg-muted">Your payout</span>
              <span className="nums text-xl font-semibold text-fg">
                {formatInr(job.netAmountToProvider)}
              </span>
            </div>
          </div>
        </Card>

        {/* Available while this provider is the booking's live assignment
            (Assigned or Accepted) - exactly the window ChatController's
            ownership check allows. Hidden once the assignment is no longer
            live (Rejected/Reassigned/Withdrawn), where the thread would 404. */}
        {job.status !== JobStatus.Rejected &&
        job.status !== JobStatus.Reassigned &&
        job.status !== JobStatus.Withdrawn ? (
          <ChatPanel bookingId={job.bookingId} />
        ) : null}

        {isLocationShareable(job.status) ? (
          <LocationSharingCard status={locationSharingStatus} />
        ) : null}

        {job.status === JobStatus.Accepted ||
        job.status === JobStatus.EnRoute ||
        job.status === JobStatus.Arrived ? (
          <Card
            title="Ready to go?"
            description="Start the job when you arrive and begin work."
          >
            {/* StickyActionBar, not a plain div: this is the screen's only
                primary action while the job is Accepted/EnRoute/Arrived, and
                it must stay reachable regardless of scroll position (#345) -
                fixed to the viewport bottom below `md`, inline here from
                `md` up. Safe to nest inside `Card`: see StickyActionBar's
                own comment on why its `overflow-hidden` doesn't clip it. */}
            <StickyActionBar>
              {/* Neither is mandatory before Start - Accepted -> InProgress is
                  a legal transition on its own (BookingLifecycle.cs). Once en
                  route, though, Arrived is the only way forward: the
                  lifecycle has no ProviderEnRoute -> InProgress edge, so Start
                  is hidden rather than left to fail with a 422. */}
              {job.status === JobStatus.Accepted ? (
                <Button
                  type="button"
                  variant="secondary"
                  size="lg"
                  fullWidth
                  loading={enRouteMutation.isPending}
                  disabled={anyActionPending}
                  onClick={() => enRouteMutation.mutate()}
                >
                  On my way — notifies the customer
                </Button>
              ) : null}

              {job.status === JobStatus.EnRoute ? (
                <Button
                  type="button"
                  variant="secondary"
                  size="lg"
                  fullWidth
                  loading={arrivedMutation.isPending}
                  disabled={anyActionPending}
                  onClick={() => arrivedMutation.mutate()}
                >
                  I&apos;ve arrived — notifies the customer
                </Button>
              ) : null}

              {job.status === JobStatus.Accepted || job.status === JobStatus.Arrived ? (
                <Button
                  type="button"
                  size="lg"
                  fullWidth
                  loading={startMutation.isPending}
                  disabled={anyActionPending}
                  onClick={() => startMutation.mutate()}
                >
                  Start job
                </Button>
              ) : null}
            </StickyActionBar>
          </Card>
        ) : null}

        {job.status === JobStatus.InProgress ? (
          <Card
            title="Finishing up"
            description="Submit the photos and checklist below, then mark the job complete."
          >
            {/* Sticky only once it is actually the actionable next step
                (verification submitted) - while it's still disabled,
                pinning a button the provider can't yet press would just
                occupy their thumb-reach real estate for nothing (#345). */}
            {completeIsPrimaryAction ? (
              <StickyActionBar>
                <Button
                  type="button"
                  size="lg"
                  fullWidth
                  loading={completeMutation.isPending}
                  disabled={anyActionPending}
                  onClick={() => completeMutation.mutate()}
                >
                  Mark complete
                </Button>
              </StickyActionBar>
            ) : (
              <Button
                type="button"
                size="lg"
                fullWidth
                loading={completeMutation.isPending}
                disabled={anyActionPending || !verificationQuery.data}
                onClick={() => completeMutation.mutate()}
              >
                Mark complete
              </Button>
            )}
            {!verificationQuery.data && !verificationQuery.isPending ? (
              <p className="mt-3 text-center text-sm text-fg-muted">
                Submit the completion verification below first.
              </p>
            ) : null}
          </Card>
        ) : null}

        {showVerification ? (
          <>
            <CompletionVerificationCard
              jobId={jobId}
              existing={verificationQuery.data}
              // `isLoading`, not `isPending`: a disabled query stays "pending"
              // forever in React Query v5, so keying the skeleton off it would
              // hang this card if it ever rendered outside VERIFIABLE_STATUSES.
              isLoading={verificationQuery.isLoading}
              onSubmitted={invalidate}
              stickySubmit={verificationIsPrimaryAction}
            />

            <Card
              title="Completion proof reference"
              description="A link or reference to the paperwork for this job."
            >
              {/* Same "no upload backend yet" caveat as the KYC document form -
                  this is a text reference, not a real file upload. */}
              <form
                onSubmit={(e) => {
                  e.preventDefault();
                  proofMutation.mutate();
                }}
                className="flex flex-col gap-3 sm:flex-row sm:items-end"
              >
                <div className="flex-1">
                  <Field
                    label="File reference or URL"
                    value={proofRef}
                    onChange={(e) => setProofRef(e.target.value)}
                    placeholder="https://…"
                  />
                </div>
                <Button
                  type="submit"
                  loading={proofMutation.isPending}
                  disabled={proofRef.trim() === ""}
                >
                  Submit
                </Button>
              </form>
            </Card>
          </>
        ) : null}

        {job.status === JobStatus.Completed ? <RateCustomerCard jobId={jobId} /> : null}

        {job.status === JobStatus.Rejected ||
        job.status === JobStatus.Reassigned ||
        job.status === JobStatus.Withdrawn ? (
          <Alert tone="info" title={jobStatusLabel(job.status)}>
            There is nothing left to do on this job.
          </Alert>
        ) : null}
      </div>
    </div>
  );
}

const RATING_HINTS = ["", "Poor", "Fair", "Good", "Great", "Excellent"] as const;

/**
 * Bidirectional reviews: the provider's private rating of the customer, the
 * reverse direction of customer-web's booking-review flow. Never shown to
 * the customer - admin-only, via the Customer 360 view. A separate step
 * after Complete (not bundled into it), so it's never a blocker on finishing
 * the job.
 */
function RateCustomerCard({ jobId }: { jobId: string }) {
  const toast = useToast();

  const eligibilityQuery = useQuery({
    queryKey: ["provider-job-rating-eligibility", jobId],
    queryFn: () => getCustomerRatingEligibility(jobId),
  });
  const ratingQuery = useQuery({
    queryKey: ["provider-job-rating", jobId],
    queryFn: () => getCustomerRating(jobId),
  });

  if (eligibilityQuery.isPending || ratingQuery.isPending) {
    return (
      <Card title="Rate the customer">
        <SkeletonText lines={2} />
      </Card>
    );
  }

  const existing = ratingQuery.data;
  if (existing) {
    return (
      <Card title="Rate the customer" description="Submitted - thanks for the feedback.">
        <div className="flex items-center gap-3">
          <RatingStars value={existing.rating} />
          <span className="nums text-sm font-medium text-fg-muted">{existing.rating}/5</span>
        </div>
        {existing.note ? <p className="mt-3 text-sm leading-relaxed text-fg">{existing.note}</p> : null}
      </Card>
    );
  }

  if (eligibilityQuery.isError || !eligibilityQuery.data?.isEligible) {
    // Not eligible (rating window closed, or something went wrong checking) -
    // no card at all rather than a dead-end form, same as how this page
    // hides Start once the lifecycle makes it illegal.
    return null;
  }

  return <RateCustomerForm jobId={jobId} toast={toast} />;
}

function RateCustomerForm({
  jobId,
  toast,
}: {
  jobId: string;
  toast: ReturnType<typeof useToast>;
}) {
  const queryClient = useQueryClient();
  const [rating, setRating] = useState(0);
  const [note, setNote] = useState("");
  const [ratingError, setRatingError] = useState<string | null>(null);

  const mutation = useMutation({
    mutationFn: () => submitCustomerRating(jobId, { rating, note: note.trim() ? note.trim() : null }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["provider-job-rating", jobId] });
      toast("success", "Thanks for rating the customer.");
    },
  });

  function handleSubmit(e: FormEvent) {
    e.preventDefault();
    if (rating < 1 || rating > 5) {
      setRatingError("Please select a rating from 1 to 5.");
      return;
    }
    setRatingError(null);
    mutation.mutate();
  }

  return (
    <Card title="Rate the customer" description="Private - the customer never sees this.">
      <form onSubmit={handleSubmit} className="flex flex-col gap-4">
        {mutation.isError ? (
          <ErrorState title="Couldn't submit that rating" error={mutation.error} />
        ) : null}

        <div className="flex flex-col gap-2">
          <span className="text-sm font-medium text-fg">Rating</span>
          <RatingInput value={rating} onChange={setRating} />
          {ratingError ? <p className="text-xs font-medium text-danger">{ratingError}</p> : null}
        </div>

        <Textarea
          label="Note (optional)"
          rows={3}
          maxLength={500}
          value={note}
          onChange={(e) => setNote(e.target.value)}
          hint="Only visible internally - never shown to the customer."
        />

        <Button type="submit" loading={mutation.isPending} className="self-start">
          Submit rating
        </Button>
      </form>
    </Card>
  );
}

function RatingInput({ value, onChange }: { value: number; onChange: (rating: number) => void }) {
  return (
    <div className="flex items-center gap-3">
      <div role="radiogroup" aria-label="Rating" className="flex gap-1">
        {[1, 2, 3, 4, 5].map((n) => (
          <button
            key={n}
            type="button"
            role="radio"
            aria-checked={value === n}
            aria-label={`${n} star${n === 1 ? "" : "s"}`}
            onClick={() => onChange(n)}
            className="rounded-lg p-1 text-3xl leading-none transition-transform duration-fast ease-out hover:scale-110 active:scale-95"
          >
            <span
              aria-hidden
              className={n <= value ? "block text-warning" : "block text-fg-subtle/40"}
            >
              ★
            </span>
          </button>
        ))}
      </div>
      <span className="text-sm font-medium text-fg-muted" aria-hidden>
        {RATING_HINTS[value] ?? ""}
      </span>
    </div>
  );
}

function RatingStars({ value }: { value: number }) {
  return (
    <span aria-label={`${value} out of 5 stars`} className="text-xl leading-none">
      {[1, 2, 3, 4, 5].map((n) => (
        <span key={n} aria-hidden className={n <= value ? "text-warning" : "text-fg-subtle/40"}>
          ★
        </span>
      ))}
    </span>
  );
}

/**
 * Task 282's status panel for {@link useLocationSharing}. Every branch is
 * informational, never blocking: a provider who denies location access can
 * still Start/Complete the job exactly as before, which is the point of
 * "must handle permission-denied by degrading to a clear 'sharing off' state
 * rather than breaking the job flow" - nothing here ever disables another
 * card's button.
 */
function LocationSharingCard({ status }: { status: LocationSharingStatus }) {
  const copy: Record<LocationSharingStatus, { title: string; description: string; tone: "info" | "warning" }> = {
    idle: {
      title: "Starting location sharing…",
      description: "The customer sees your live position while this job is active.",
      tone: "info",
    },
    requesting: {
      title: "Waiting for location permission",
      description: "Allow location access so the customer can see you're on the way.",
      tone: "info",
    },
    sharing: {
      title: "Sharing your live location",
      description:
        "The customer can see your position while this job is active. Only works while this app is open and the screen is on - it does not run in the background.",
      tone: "info",
    },
    denied: {
      title: "Location sharing is off",
      description:
        "You denied location access, so the customer won't see your live position. You can still start and complete this job normally - turn it on from your browser's site settings if you change your mind.",
      tone: "warning",
    },
    unsupported: {
      title: "Location sharing isn't available",
      description: "This browser doesn't support location sharing. The job flow still works normally.",
      tone: "warning",
    },
    error: {
      title: "Location sharing paused",
      description: "We're having trouble getting a location fix. Retrying automatically.",
      tone: "warning",
    },
  };

  const { title, description, tone } = copy[status];

  return (
    <Card>
      <div className="flex items-start gap-3">
        <span
          aria-hidden
          className={
            "mt-0.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-full " +
            (tone === "warning" ? "bg-warning-soft text-warning" : "bg-brand-50 text-brand-700 dark:bg-brand-500/15 dark:text-brand-300")
          }
        >
          <svg
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            strokeLinecap="round"
            strokeLinejoin="round"
            className="h-4 w-4"
          >
            <path d="M12 21s7-6.4 7-11a7 7 0 1 0-14 0c0 4.6 7 11 7 11Z" />
            <circle cx="12" cy="10" r="2.5" />
          </svg>
        </span>
        <div className="min-w-0">
          <p className="flex items-center gap-2 text-sm font-medium text-fg">
            {status === "sharing" ? (
              <span aria-hidden className="h-1.5 w-1.5 animate-pulse rounded-full bg-success" />
            ) : null}
            {title}
          </p>
          <p className="mt-0.5 text-sm leading-relaxed text-fg-muted">{description}</p>
        </div>
      </div>
    </Card>
  );
}

/** One label/value pair in a definition list, stacked so it survives a narrow screen. */
function DetailRow({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-0.5">
      <dt className="text-xs font-semibold uppercase tracking-wide text-fg-muted">{label}</dt>
      <dd className="flex flex-col text-sm text-fg">{children}</dd>
    </div>
  );
}

/** Mirrors the real screen's shape so nothing jumps when the job lands. */
function JobDetailSkeleton({ backLink }: { backLink: ReactNode }) {
  return (
    <div>
      <div className="mb-6">
        <div className="mb-2">{backLink}</div>
        <Skeleton className="h-9 w-56" />
        <Skeleton className="mt-2 h-4 w-44" />
      </div>
      <div className="mb-6 rounded-2xl bg-surface p-5 shadow-sm">
        <Skeleton className="h-5 w-48" />
        <Skeleton className="mt-4 h-12 w-full rounded-lg" />
        <Skeleton className="mt-2.5 h-12 w-full rounded-lg" />
      </div>
      <div className="rounded-2xl bg-surface p-6 shadow-sm">
        <Skeleton className="h-5 w-28" />
        <SkeletonText lines={6} className="mt-5" />
      </div>
    </div>
  );
}

/**
 * One photo in flight through `CompletionVerificationCard`'s upload flow.
 * `previewUrl` (an object URL over the local file) renders instantly so the
 * provider sees their photo before the network round trip finishes; `ref`
 * only exists once the server has actually stored it and is what gets
 * submitted - an in-flight or failed upload can never be silently included.
 */
interface CompletionPhoto {
  localId: string;
  previewUrl: string;
  status: "uploading" | "done";
  ref: string | null;
}

/**
 * Photos + checklist evidence required before `completeJob` succeeds
 * (tasks 195-197) - distinct from the single legacy proof-ref field above.
 * Resubmitting replaces the previous evidence (same behavior as the backend).
 */
function CompletionVerificationCard({
  jobId,
  existing,
  isLoading,
  onSubmitted,
  stickySubmit,
}: {
  jobId: string;
  existing: BookingCompletionProofResponse | null | undefined;
  isLoading: boolean;
  onSubmitted: () => void;
  /** Pin the submit button to the viewport bottom (#345) - true only while
   *  submitting this is the job's actual next step; see JobDetailPage's
   *  `verificationIsPrimaryAction` for the full precedence. */
  stickySubmit: boolean;
}) {
  const toast = useToast();
  const [photos, setPhotos] = useState<CompletionPhoto[]>([]);
  const [checklist, setChecklist] = useState<CompletionChecklistAnswer[]>([
    { item: "", completed: false, notes: null },
  ]);
  const fileInputRef = useRef<HTMLInputElement>(null);

  const uploading = photos.some((p) => p.status === "uploading");
  const readyRefs = photos.filter((p) => p.status === "done").map((p) => p.ref!);

  async function handleFilesSelected(fileList: FileList | null) {
    if (!fileList || fileList.length === 0) return;

    for (const file of Array.from(fileList)) {
      const localId = crypto.randomUUID();
      const previewUrl = URL.createObjectURL(file);
      setPhotos((prev) => [...prev, { localId, previewUrl, status: "uploading", ref: null }]);

      try {
        const { photoRef } = await uploadCompletionPhoto(jobId, file);
        setPhotos((prev) =>
          prev.map((p) => (p.localId === localId ? { ...p, status: "done", ref: photoRef } : p)),
        );
      } catch (err) {
        setPhotos((prev) => prev.filter((p) => p.localId !== localId));
        toast("error", describeError(err));
      }
    }
  }

  function removePhoto(localId: string) {
    setPhotos((prev) => {
      const target = prev.find((p) => p.localId === localId);
      if (target) URL.revokeObjectURL(target.previewUrl);
      return prev.filter((p) => p.localId !== localId);
    });
  }

  const mutation = useMutation({
    mutationFn: () =>
      submitCompletionVerification(jobId, {
        photoRefs: readyRefs,
        checklistAnswers: checklist.filter((a) => a.item.trim() !== ""),
      }),
    onSuccess: () => {
      onSubmitted();
      toast("success", "Completion verification submitted.");
    },
  });

  function updateChecklistItem(index: number, patch: Partial<CompletionChecklistAnswer>) {
    setChecklist((prev) => prev.map((a, i) => (i === index ? { ...a, ...patch } : a)));
  }

  function removeChecklistItem(index: number) {
    setChecklist((prev) => prev.filter((_, i) => i !== index));
  }

  if (isLoading) {
    return (
      <Card title="Completion verification">
        <SkeletonText lines={4} />
      </Card>
    );
  }

  return (
    <Card
      title="Completion verification"
      description="Photos and a checklist of what you did. Required before this job can be marked complete."
    >
      {existing ? (
        <div className="mb-5 rounded-xl border border-success/25 bg-success-soft p-4 text-sm">
          <p className="font-semibold text-success">
            Submitted {formatDateTime(existing.submittedAtUtc)}
          </p>
          <p className="nums mt-1 text-success/90">
            {existing.photoRefs.length} photo{existing.photoRefs.length === 1 ? "" : "s"}
          </p>
          {existing.checklistAnswers.length > 0 ? (
            <ul className="mt-3 flex flex-col gap-1.5 text-success/90">
              {existing.checklistAnswers.map((a, i) => (
                <li key={i} className="flex items-start gap-2">
                  <span aria-hidden className="mt-px">
                    {a.completed ? "✓" : "○"}
                  </span>
                  <span>
                    {a.item}
                    {a.notes ? ` — ${a.notes}` : ""}
                  </span>
                </li>
              ))}
            </ul>
          ) : null}
        </div>
      ) : null}

      {mutation.isError ? (
        <div className="mb-4">
          <ErrorState title="Couldn't submit that" error={mutation.error} />
        </div>
      ) : null}

      <form
        onSubmit={(e) => {
          e.preventDefault();
          mutation.mutate();
        }}
        className="flex flex-col gap-5"
      >
        <div className="flex flex-col gap-2.5">
          <span className="text-sm font-medium text-fg">Photos</span>

          {/* `capture="environment"` opens the rear camera directly on a
              phone browser instead of the generic file picker; desktop
              browsers that have no camera concept just fall back to a file
              picker, so this never blocks a non-mobile provider. `multiple`
              lets one tap add several shots in one go. */}
          <input
            ref={fileInputRef}
            type="file"
            accept="image/jpeg,image/png,image/webp"
            capture="environment"
            multiple
            hidden
            onChange={(e) => {
              void handleFilesSelected(e.target.files);
              e.target.value = "";
            }}
          />

          {photos.length > 0 ? (
            <div className="grid grid-cols-3 gap-2 sm:grid-cols-4">
              {photos.map((photo) => (
                <div
                  key={photo.localId}
                  className="relative aspect-square overflow-hidden rounded-xl border border-line bg-surface-2"
                >
                  <img
                    src={photo.previewUrl}
                    alt="Completion evidence"
                    className="h-full w-full object-cover"
                  />
                  {photo.status === "uploading" ? (
                    <div className="absolute inset-0 flex items-center justify-center bg-surface/70">
                      <Spinner />
                    </div>
                  ) : (
                    <div className="absolute right-1 top-1">
                      <IconButton
                        label="Remove photo"
                        onClick={() => removePhoto(photo.localId)}
                      >
                        <svg
                          viewBox="0 0 24 24"
                          fill="none"
                          stroke="currentColor"
                          strokeWidth="2"
                          strokeLinecap="round"
                          className="h-4 w-4"
                          aria-hidden
                        >
                          <path d="M18 6 6 18M6 6l12 12" />
                        </svg>
                      </IconButton>
                    </div>
                  )}
                </div>
              ))}
            </div>
          ) : null}

          <Button
            type="button"
            variant="secondary"
            className="w-fit"
            onClick={() => fileInputRef.current?.click()}
          >
            Take or choose photo
          </Button>
        </div>

        <div className="flex flex-col gap-2.5">
          <span className="text-sm font-medium text-fg">Checklist</span>
          {checklist.map((answer, index) => (
            <div
              key={index}
              className="flex items-center gap-3 rounded-xl border border-line bg-surface-2 p-3"
            >
              {/* A raw input rather than the kit's `Checkbox`: that primitive
                  derives its id from its visible label, and these rows have
                  none to derive from - every row would collide on one id. */}
              <input
                type="checkbox"
                aria-label={`Mark checklist item ${index + 1} as done`}
                checked={answer.completed}
                onChange={(e) => updateChecklistItem(index, { completed: e.target.checked })}
                className="h-5 w-5 shrink-0 cursor-pointer rounded border-line-strong accent-brand-600"
              />
              <input
                type="text"
                value={answer.item}
                onChange={(e) => updateChecklistItem(index, { item: e.target.value })}
                placeholder="What did you do?"
                aria-label={`Checklist item ${index + 1}`}
                className="min-w-0 flex-1 rounded-lg border border-line bg-surface px-3 py-2 text-sm text-fg shadow-xs outline-none transition duration-fast ease-out placeholder:text-fg-subtle hover:border-line-strong focus:border-brand-600 focus:ring-2 focus:ring-brand-600/25"
              />
              {checklist.length > 1 ? (
                <IconButton
                  label={`Remove checklist item ${index + 1}`}
                  onClick={() => removeChecklistItem(index)}
                >
                  <svg
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="2"
                    strokeLinecap="round"
                    className="h-4 w-4"
                    aria-hidden
                  >
                    <path d="M18 6 6 18M6 6l12 12" />
                  </svg>
                </IconButton>
              ) : null}
            </div>
          ))}
          <Button
            type="button"
            variant="secondary"
            className="w-fit"
            onClick={() =>
              setChecklist((prev) => [...prev, { item: "", completed: false, notes: null }])
            }
          >
            Add checklist item
          </Button>
        </div>

        {/* Why the submit below is disabled. Without this the provider sees a
            greyed-out primary action and no reason for it - worst of all on
            the sticky bar, where the photo picker it depends on has usually
            scrolled out of view, so the button just reads as broken. */}
        {readyRefs.length === 0 || uploading ? (
          <p className="text-sm text-fg-muted">
            {uploading
              ? "Waiting for the photos to finish uploading…"
              : "Add at least one photo to submit this verification."}
          </p>
        ) : null}

        {stickySubmit ? (
          // Fixed to the viewport bottom below `md` (#345) - still a normal
          // descendant of this <form>, so `type="submit"` keeps working
          // exactly as it did inline; DOM membership governs form
          // association, not visual position.
          <StickyActionBar>
            <Button
              type="submit"
              size="lg"
              fullWidth
              loading={mutation.isPending}
              disabled={readyRefs.length === 0 || uploading}
            >
              {existing ? "Resubmit verification" : "Submit verification"}
            </Button>
          </StickyActionBar>
        ) : (
          <Button
            type="submit"
            size="lg"
            fullWidth
            loading={mutation.isPending}
            disabled={readyRefs.length === 0 || uploading}
          >
            {existing ? "Resubmit verification" : "Submit verification"}
          </Button>
        )}
      </form>
    </Card>
  );
}
