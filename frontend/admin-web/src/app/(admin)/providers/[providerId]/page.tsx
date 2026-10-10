"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { motion } from "motion/react";
import { useParams } from "next/navigation";
import { useState } from "react";
import { Reveal, revealItem } from "@/components/motion";
import {
  Alert,
  Badge,
  Button,
  Card,
  cx,
  EmptyState,
  Field,
  PageHeading,
  Select,
  SkeletonText,
  StatTile,
  Tabs,
} from "@/components/ui";
import {
  Breadcrumbs,
  ConfirmDialog,
  FormActions,
  FormGrid,
  formatCurrency,
  formatDate,
  formatDateTime,
  RecordMetaRow,
} from "@/components/data-table";
import { DetailError, DetailSkeleton, SectionError } from "@/components/screen-states";
import { ProviderStatusBadge } from "@/components/status-badges";
import { describeError } from "@/lib/api";
import {
  activateProvider,
  approveBankAccount,
  approveKycDocument,
  approveProviderPhoto,
  createPayoutBatch,
  getProviderCapacity,
  getProviderDetail,
  getProviderEarnings,
  getProviderPerformance,
  payViaPayU,
  reactivateProvider,
  recordBackgroundCheck,
  recordEarningAdjustment,
  rejectBankAccount,
  rejectKycDocument,
  rejectProviderPhoto,
  searchPayouts,
  setProviderCapacity,
  suspendProvider,
  updateProvider,
  updatePayoutStatus,
} from "@/lib/providers-api";
import {
  ProviderBackgroundCheckStatus,
  ProviderBankAccountVerificationStatus,
  ProviderEarningEntryType,
  ProviderEarningSourceType,
  ProviderKycDocumentType,
  ProviderKycVerificationStatus,
  ProviderOnboardingStatus,
  ProviderPayoutChannel,
  ProviderPayoutStatus,
  ProviderPhotoModerationStatus,
  ProviderStatus,
} from "@/lib/providers-types";
import type { BadgeTone } from "@/components/ui";
import type { ProviderCapacity, ProviderDetail } from "@/lib/providers-types";
import { useAdminClaims } from "@/lib/use-admin-claims";

const STATUS_LABELS: Record<ProviderStatus, string> = {
  [ProviderStatus.PendingVerification]: "Pending verification",
  [ProviderStatus.Active]: "Active",
  [ProviderStatus.Suspended]: "Suspended",
  [ProviderStatus.Deactivated]: "Deactivated",
};

const ONBOARDING_LABELS: Record<ProviderOnboardingStatus, string> = {
  [ProviderOnboardingStatus.Registered]: "Registered",
  [ProviderOnboardingStatus.ProfileCompleted]: "Profile completed",
  [ProviderOnboardingStatus.KycSubmitted]: "KYC submitted",
  [ProviderOnboardingStatus.KycVerified]: "KYC verified",
  [ProviderOnboardingStatus.Completed]: "Onboarding complete",
};

const KYC_DOC_TYPE_LABELS: Record<ProviderKycDocumentType, string> = {
  [ProviderKycDocumentType.IdentityProof]: "Identity proof",
  [ProviderKycDocumentType.AddressProof]: "Address proof",
  [ProviderKycDocumentType.BankAccountProof]: "Bank account proof",
  [ProviderKycDocumentType.ProfessionalCertificate]: "Professional certificate",
  [ProviderKycDocumentType.Other]: "Other",
};

const KYC_STATUS_LABELS: Record<ProviderKycVerificationStatus, string> = {
  [ProviderKycVerificationStatus.Pending]: "Pending review",
  [ProviderKycVerificationStatus.Approved]: "Approved",
  [ProviderKycVerificationStatus.Rejected]: "Rejected",
  // Task 349: the provider submitted a newer document of the same type -
  // this one is retired history, not a queue item, so it never gets
  // Approve/Reject actions (gated below on Pending only).
  [ProviderKycVerificationStatus.Superseded]: "Replaced",
};

const KYC_STATUS_TONES: Record<ProviderKycVerificationStatus, BadgeTone> = {
  [ProviderKycVerificationStatus.Pending]: "warning",
  [ProviderKycVerificationStatus.Approved]: "success",
  [ProviderKycVerificationStatus.Rejected]: "danger",
  [ProviderKycVerificationStatus.Superseded]: "neutral",
};

/** KYC uploads are restricted server-side to JPEG/PNG/WebP or PDF (ProfileController.AllowedKycContentTypes) - the stored object keeps that extension, so it's a reliable way to tell an image from a PDF client-side without a HEAD request. */
function isImageFileRef(fileRef: string): boolean {
  return /\.(jpe?g|png|webp)$/i.test(new URL(fileRef).pathname);
}

const PHOTO_STATUS_LABELS: Record<ProviderPhotoModerationStatus, string> = {
  [ProviderPhotoModerationStatus.Pending]: "Pending review",
  [ProviderPhotoModerationStatus.Approved]: "Live to customers",
  [ProviderPhotoModerationStatus.Rejected]: "Rejected",
};

const PHOTO_STATUS_TONES: Record<ProviderPhotoModerationStatus, BadgeTone> = {
  [ProviderPhotoModerationStatus.Pending]: "warning",
  [ProviderPhotoModerationStatus.Approved]: "success",
  [ProviderPhotoModerationStatus.Rejected]: "danger",
};

const BACKGROUND_CHECK_STATUS_LABELS: Record<ProviderBackgroundCheckStatus, string> = {
  [ProviderBackgroundCheckStatus.Pending]: "Pending",
  [ProviderBackgroundCheckStatus.Passed]: "Passed",
  [ProviderBackgroundCheckStatus.Failed]: "Failed",
};

const BACKGROUND_CHECK_TONES: Record<ProviderBackgroundCheckStatus, BadgeTone> = {
  [ProviderBackgroundCheckStatus.Pending]: "warning",
  [ProviderBackgroundCheckStatus.Passed]: "success",
  [ProviderBackgroundCheckStatus.Failed]: "danger",
};

const PAYOUT_STATUS_LABELS: Record<ProviderPayoutStatus, string> = {
  [ProviderPayoutStatus.Pending]: "Pending",
  [ProviderPayoutStatus.Processing]: "Processing",
  [ProviderPayoutStatus.Paid]: "Paid",
  [ProviderPayoutStatus.Failed]: "Failed",
};

const PAYOUT_STATUS_TONES: Record<ProviderPayoutStatus, BadgeTone> = {
  [ProviderPayoutStatus.Pending]: "neutral",
  [ProviderPayoutStatus.Processing]: "info",
  [ProviderPayoutStatus.Paid]: "success",
  [ProviderPayoutStatus.Failed]: "danger",
};

const BANK_ACCOUNT_STATUS_LABELS: Record<ProviderBankAccountVerificationStatus, string> = {
  [ProviderBankAccountVerificationStatus.Pending]: "Pending review",
  [ProviderBankAccountVerificationStatus.Verified]: "Verified",
  [ProviderBankAccountVerificationStatus.Rejected]: "Rejected",
};

const BANK_ACCOUNT_STATUS_TONES: Record<ProviderBankAccountVerificationStatus, BadgeTone> = {
  [ProviderBankAccountVerificationStatus.Pending]: "warning",
  [ProviderBankAccountVerificationStatus.Verified]: "success",
  [ProviderBankAccountVerificationStatus.Rejected]: "danger",
};

/** Real PayU Payouts integration: admin-visibility label for which path processed a payout - shown once it has left Pending. */
const PROCESSED_VIA_LABELS: Record<ProviderPayoutChannel, string> = {
  [ProviderPayoutChannel.Manual]: "Manual",
  [ProviderPayoutChannel.PayUAutomated]: "PayU",
};

/**
 * Admin provider detail (PROVIDER.md; tasks 150a-150c, 160, and the 148
 * financial views): profile edit and suspend/reactivate (150a), KYC document
 * approve/reject and the background-check activation gate (150b, 160), the
 * performance summary (150c), and the earnings ledger / payout batches
 * (148). Mutating actions are only shown to admins holding the relevant
 * "provider.write"/"payout.write" permission - the API enforces this
 * server-side regardless, this purely avoids showing controls that would
 * just 403.
 *
 * Suspension, KYC rejection and marking a payout failed each go through
 * `ConfirmDialog` (task 222) — all three are irreversible from this screen and
 * were previously a single unconfirmed click.
 */
export default function ProviderDetailPage() {
  const params = useParams<{ providerId: string }>();
  const providerId = params.providerId;
  const claims = useAdminClaims();
  const canWriteProvider = claims?.permissions.includes("provider.write") ?? false;
  const canWritePayout = claims?.permissions.includes("payout.write") ?? false;
  const queryClient = useQueryClient();

  const detailQuery = useQuery({
    queryKey: ["admin-provider-detail", providerId],
    queryFn: () => getProviderDetail(providerId),
  });
  const performanceQuery = useQuery({
    queryKey: ["admin-provider-performance", providerId],
    queryFn: () => getProviderPerformance(providerId),
  });
  const earningsQuery = useQuery({
    queryKey: ["admin-provider-earnings", providerId],
    queryFn: () => getProviderEarnings(providerId),
  });
  const payoutsQuery = useQuery({
    queryKey: ["admin-provider-payouts", providerId],
    queryFn: () => searchPayouts({ providerId }),
  });
  const capacityQuery = useQuery({
    queryKey: ["admin-provider-capacity", providerId],
    queryFn: () => getProviderCapacity(providerId),
  });

  const [actionError, setActionError] = useState<string | null>(null);
  const [actionNotice, setActionNotice] = useState<string | null>(null);

  // Enterprise redesign pass (docs/OPEN-FIXES-FEATURES.csv, admin-web
  // information-density) - same pattern as the booking detail page: group the
  // 8 always-stacked cards (Profile, Capacity, Photo, KYC, Background check,
  // Performance, Earnings, Payouts) behind tabs instead of rendering all of
  // them at once.
  const [detailTab, setDetailTab] = useState<"overview" | "verification" | "performance" | "earnings">("overview");

  const [suspendReason, setSuspendReason] = useState("");
  const [confirmSuspend, setConfirmSuspend] = useState(false);

  const [rejectReasonByDoc, setRejectReasonByDoc] = useState<Record<string, string>>({});
  const [pendingKycRejection, setPendingKycRejection] = useState<{ id: string; label: string } | null>(null);

  const [bankAccountRejectReason, setBankAccountRejectReason] = useState("");
  const [isConfirmingBankAccountRejection, setIsConfirmingBankAccountRejection] = useState(false);

  const [photoRejectReason, setPhotoRejectReason] = useState("");
  const [isConfirmingPhotoRejection, setIsConfirmingPhotoRejection] = useState(false);

  const [bgStatus, setBgStatus] = useState(String(ProviderBackgroundCheckStatus.Passed));
  const [bgNotes, setBgNotes] = useState("");

  const [adjustmentType, setAdjustmentType] = useState(String(ProviderEarningEntryType.Credit));
  const [adjustmentAmount, setAdjustmentAmount] = useState("");
  const [adjustmentDescription, setAdjustmentDescription] = useState("");

  const [payoutPeriodStart, setPayoutPeriodStart] = useState("");
  const [payoutPeriodEnd, setPayoutPeriodEnd] = useState("");
  const [payoutReferenceByPayout, setPayoutReferenceByPayout] = useState<Record<string, string>>({});
  const [pendingPayoutFailure, setPendingPayoutFailure] = useState<string | null>(null);

  const invalidateAll = () => {
    queryClient.invalidateQueries({ queryKey: ["admin-provider-detail", providerId] });
    queryClient.invalidateQueries({ queryKey: ["admin-provider-performance", providerId] });
    queryClient.invalidateQueries({ queryKey: ["admin-provider-earnings", providerId] });
    queryClient.invalidateQueries({ queryKey: ["admin-provider-payouts", providerId] });
    queryClient.invalidateQueries({ queryKey: ["admin-provider-capacity", providerId] });
  };

  const onError = (err: unknown) => setActionError(describeError(err));
  const onSuccess = (notice: string) => {
    setActionError(null);
    setActionNotice(notice);
    invalidateAll();
  };

  const updateMutation = useMutation({
    mutationFn: (values: {
      legalName: string;
      displayName: string;
      email: string;
      latitude: number | null;
      longitude: number | null;
    }) =>
      updateProvider(providerId, {
        legalName: values.legalName,
        displayName: values.displayName,
        email: values.email || undefined,
        latitude: values.latitude,
        longitude: values.longitude,
      }),
    onSuccess: () => onSuccess("Profile updated."),
    onError,
  });

  const capacityMutation = useMutation({
    mutationFn: (values: { maxJobsPerDay: number | null; maxJobsPerSlot: number | null }) =>
      setProviderCapacity(providerId, values),
    onSuccess: () => onSuccess("Capacity limits updated."),
    onError,
  });

  const suspendMutation = useMutation({
    mutationFn: () => suspendProvider(providerId, { reason: suspendReason }),
    onSuccess: () => {
      setSuspendReason("");
      setConfirmSuspend(false);
      onSuccess("Provider suspended.");
    },
    onError,
  });

  const reactivateMutation = useMutation({
    mutationFn: () => reactivateProvider(providerId),
    onSuccess: () => onSuccess("Provider reactivated."),
    onError,
  });

  const activateMutation = useMutation({
    mutationFn: () => activateProvider(providerId),
    onSuccess: () => onSuccess("Provider activated."),
    onError,
  });

  const approveKycMutation = useMutation({
    mutationFn: (documentId: string) => approveKycDocument(documentId),
    onSuccess: () => onSuccess("KYC document approved."),
    onError,
  });

  const approvePhotoMutation = useMutation({
    mutationFn: () => approveProviderPhoto(providerId),
    onSuccess: () => onSuccess("Photo approved - customers can now see it."),
    onError,
  });

  const rejectPhotoMutation = useMutation({
    mutationFn: (reason: string) => rejectProviderPhoto(providerId, { reason }),
    onSuccess: () => {
      setIsConfirmingPhotoRejection(false);
      setPhotoRejectReason("");
      onSuccess("Photo rejected.");
    },
    onError,
  });

  const rejectKycMutation = useMutation({
    mutationFn: ({ documentId, reason }: { documentId: string; reason: string }) => rejectKycDocument(documentId, { reason }),
    onSuccess: () => {
      setPendingKycRejection(null);
      onSuccess("KYC document rejected.");
    },
    onError,
  });

  const approveBankAccountMutation = useMutation({
    mutationFn: (bankAccountId: string) => approveBankAccount(bankAccountId),
    onSuccess: () => onSuccess("Bank account details verified."),
    onError,
  });

  const rejectBankAccountMutation = useMutation({
    mutationFn: ({ bankAccountId, reason }: { bankAccountId: string; reason: string }) =>
      rejectBankAccount(bankAccountId, { reason }),
    onSuccess: () => {
      setIsConfirmingBankAccountRejection(false);
      setBankAccountRejectReason("");
      onSuccess("Bank account details rejected.");
    },
    onError,
  });

  const backgroundCheckMutation = useMutation({
    mutationFn: () =>
      recordBackgroundCheck(providerId, { status: Number(bgStatus) as ProviderBackgroundCheckStatus, notes: bgNotes || undefined }),
    onSuccess: () => {
      setBgNotes("");
      onSuccess("Background check recorded.");
    },
    onError,
  });

  const adjustmentMutation = useMutation({
    mutationFn: () =>
      recordEarningAdjustment(providerId, {
        entryType: Number(adjustmentType) as ProviderEarningEntryType,
        amount: Number(adjustmentAmount),
        sourceType: ProviderEarningSourceType.ManualAdjustment,
        description: adjustmentDescription,
      }),
    onSuccess: () => {
      setAdjustmentAmount("");
      setAdjustmentDescription("");
      onSuccess("Earning ledger adjustment recorded.");
    },
    onError,
  });

  const createPayoutMutation = useMutation({
    mutationFn: () => createPayoutBatch(providerId, { periodStart: payoutPeriodStart, periodEnd: payoutPeriodEnd }),
    onSuccess: () => {
      setPayoutPeriodStart("");
      setPayoutPeriodEnd("");
      onSuccess("Payout batch created.");
    },
    onError,
  });

  const payoutStatusMutation = useMutation({
    mutationFn: ({ payoutId, status, payoutReference }: { payoutId: string; status: ProviderPayoutStatus; payoutReference?: string }) =>
      updatePayoutStatus(payoutId, { status, payoutReference }),
    onSuccess: () => {
      setPendingPayoutFailure(null);
      onSuccess("Payout status updated.");
    },
    onError,
  });

  const payViaPayUMutation = useMutation({
    mutationFn: (payoutId: string) => payViaPayU(payoutId),
    onSuccess: () => onSuccess("PayU transfer initiated - the payout will settle once PayU's webhook confirms it."),
    onError,
  });

  const breadcrumbs = [
    { label: "Providers", href: "/providers/directory" },
    { label: detailQuery.data?.displayName ?? "Provider" },
  ];

  if (detailQuery.isPending) {
    return <DetailSkeleton cards={4} className="flex w-full max-w-7xl flex-col gap-6" />;
  }

  if (detailQuery.isError) {
    return (
      <DetailError
        title="Provider"
        breadcrumbs={breadcrumbs}
        error={detailQuery.error}
        onRetry={() => detailQuery.refetch()}
        className="w-full max-w-7xl"
      />
    );
  }

  const provider = detailQuery.data;
  const canActivate =
    provider.status === ProviderStatus.PendingVerification &&
    (provider.onboardingStatus === ProviderOnboardingStatus.KycVerified || provider.onboardingStatus === ProviderOnboardingStatus.Completed);

  return (
    <div className="flex w-full max-w-7xl flex-col gap-6">
      <PageHeading
        title={provider.displayName}
        subtitle={`${provider.phone}${provider.email ? ` · ${provider.email}` : ""}`}
        breadcrumbs={<Breadcrumbs items={breadcrumbs} />}
        actions={
          <div className="flex flex-wrap items-center gap-2">
            <ProviderStatusBadge status={provider.status} label={STATUS_LABELS[provider.status]} />
            <Badge
              tone={provider.onboardingStatus === ProviderOnboardingStatus.Completed ? "success" : "neutral"}
            >
              {ONBOARDING_LABELS[provider.onboardingStatus]}
            </Badge>
          </div>
        }
      />

      {actionError ? <Alert tone="error">{actionError}</Alert> : null}
      {actionNotice ? <Alert tone="success">{actionNotice}</Alert> : null}

      <RecordMetaRow
        className="border-b border-line pb-5"
        items={[
          { label: "Registered", value: <span className="nums">{formatDate(provider.createdAt)}</span> },
          {
            label: "Completed jobs",
            value: (
              <span className="nums">
                {performanceQuery.data ? performanceQuery.data.completedJobs : "—"}
              </span>
            ),
          },
          {
            label: "Current balance",
            value: (
              <span className="nums font-semibold">
                {earningsQuery.data ? formatCurrency(earningsQuery.data.currentBalance) : "—"}
              </span>
            ),
          },
        ]}
      />

      <Tabs
        label="Provider sections"
        value={detailTab}
        onChange={setDetailTab}
        tabs={[
          { value: "overview", label: "Overview" },
          { value: "verification", label: "Verification" },
          { value: "performance", label: "Performance" },
          { value: "earnings", label: "Earnings" },
        ]}
      />

      {detailTab === "overview" ? (
      <div className="flex flex-col gap-6">
      <Card
        title="Profile"
        description={
          canWriteProvider
            ? "Name and email save together; status changes below apply immediately."
            : "Read-only — you do not hold provider write access."
        }
      >
        {canWriteProvider ? (
          // Keyed on the provider id so the editor re-seeds from the server if
          // this screen is ever navigated to a different provider without
          // unmounting. It owns its own state (see ProfileEditor) rather than
          // writing into page state on change: the previous version tracked
          // each field in state initialised to "" and submitted all three, so
          // editing only the display name silently blanked the legal name.
          <ProfileEditor
            key={provider.id}
            provider={provider}
            saving={updateMutation.isPending}
            onSave={(values) => updateMutation.mutate(values)}
          />
        ) : (
          <p className="text-sm text-fg-muted">
            Legal name: <span className="text-fg">{provider.legalName}</span>
          </p>
        )}

        {canWriteProvider ? (
          <div className="mt-5 flex flex-col gap-4 border-t border-line pt-5">
            <FormActions align="start">
              {canActivate ? (
                <Button loading={activateMutation.isPending} onClick={() => activateMutation.mutate()}>
                  Activate provider
                </Button>
              ) : null}
              {provider.status === ProviderStatus.Suspended ? (
                <Button loading={reactivateMutation.isPending} onClick={() => reactivateMutation.mutate()}>
                  Reactivate
                </Button>
              ) : null}
            </FormActions>

            {provider.status !== ProviderStatus.Suspended ? (
              <div className="flex flex-col gap-3 sm:flex-row sm:items-end">
                <div className="flex-1">
                  <Field
                    label="Suspend reason"
                    required
                    value={suspendReason}
                    onChange={(e) => setSuspendReason(e.target.value)}
                    hint="Recorded to the audit trail and shown to the provider."
                  />
                </div>
                <Button variant="danger" disabled={!suspendReason.trim()} onClick={() => setConfirmSuspend(true)}>
                  Suspend provider
                </Button>
              </div>
            ) : null}
          </div>
        ) : null}
      </Card>

      <Card
        title="Dispatch capacity"
        description="Max jobs per day/slot (task 245, 308). Hard-enforced by automatic assignment; manual admin assignment only shows this as a load signal, not a block."
      >
        {capacityQuery.isPending ? (
          <SkeletonText lines={2} />
        ) : capacityQuery.isError ? (
          <SectionError error={capacityQuery.error} onRetry={() => capacityQuery.refetch()} />
        ) : canWriteProvider ? (
          <CapacityEditor
            key={providerId}
            capacity={capacityQuery.data}
            saving={capacityMutation.isPending}
            onSave={(values) => capacityMutation.mutate(values)}
          />
        ) : (
          <p className="text-sm text-fg-muted">
            Max/day: <span className="text-fg">{capacityQuery.data.maxJobsPerDay ?? "Unlimited"}</span> · Max/slot:{" "}
            <span className="text-fg">{capacityQuery.data.maxJobsPerSlot ?? "Unlimited"}</span>
          </p>
        )}
      </Card>

      <Card
        title="Profile photo"
        description="Customers see this on their booking and live-tracking screens - only once it is approved (task 293)"
      >
        {provider.photo.photoUrl === null ? (
          <EmptyState
            title="No photo submitted yet"
            description="The provider sets this from the provider app. Until then customers see a placeholder avatar."
          />
        ) : (
          <div className="flex flex-col gap-4">
            <div className="flex items-center gap-4">
              {/* next/image needs the host in next.config's allowlist and a
                  provider-supplied URL can point anywhere, so a plain img is
                  the only workable element here. */}
              {/* eslint-disable-next-line @next/next/no-img-element */}
              <img
                src={provider.photo.photoUrl}
                alt={`Submitted profile photo for ${provider.displayName}`}
                className="h-24 w-24 shrink-0 rounded-full border border-line object-cover"
              />
              <div className="min-w-0">
                {provider.photo.moderationStatus !== null ? (
                  <Badge tone={PHOTO_STATUS_TONES[provider.photo.moderationStatus]}>
                    {PHOTO_STATUS_LABELS[provider.photo.moderationStatus]}
                  </Badge>
                ) : null}
                {provider.photo.moderatedAtUtc ? (
                  <p className="mt-1.5 text-xs text-fg-subtle">
                    Reviewed {formatDateTime(provider.photo.moderatedAtUtc)}
                  </p>
                ) : null}
                {provider.photo.moderationNote ? (
                  <p className="mt-1 text-xs text-fg-muted">{provider.photo.moderationNote}</p>
                ) : null}
                <p className="mt-1 break-all text-xs text-fg-subtle">{provider.photo.photoUrl}</p>
              </div>
            </div>

            {canWriteProvider && provider.photo.moderationStatus === ProviderPhotoModerationStatus.Pending ? (
              <div className="flex flex-col gap-3 border-t border-line pt-3 sm:flex-row sm:items-end">
                <Button
                  variant="secondary"
                  loading={approvePhotoMutation.isPending}
                  onClick={() => approvePhotoMutation.mutate()}
                >
                  Approve
                </Button>
                <div className="flex-1">
                  <Field
                    label="Rejection reason"
                    value={photoRejectReason}
                    onChange={(e) => setPhotoRejectReason(e.target.value)}
                  />
                </div>
                <Button
                  variant="danger"
                  disabled={!photoRejectReason.trim()}
                  onClick={() => setIsConfirmingPhotoRejection(true)}
                >
                  Reject
                </Button>
              </div>
            ) : null}
          </div>
        )}
      </Card>
      </div>
      ) : null}

      {detailTab === "verification" ? (
      <div className="flex flex-col gap-6">
      <Card title="KYC documents" description="Approve or reject each submitted document (task 150b)">
        {provider.kycDocuments.length === 0 ? (
          <EmptyState
            title="No KYC documents submitted yet"
            description="The provider submits these from the provider app; activation is gated on them."
          />
        ) : (
          <ul className="flex flex-col gap-3 text-sm">
            {provider.kycDocuments.map((doc) => (
              <li
                key={doc.id}
                className={cx(
                  "rounded-xl border border-line p-3",
                  doc.verificationStatus === ProviderKycVerificationStatus.Superseded && "opacity-60",
                )}
              >
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <span className="font-medium text-fg">{KYC_DOC_TYPE_LABELS[doc.docType]}</span>
                  <Badge tone={KYC_STATUS_TONES[doc.verificationStatus]}>
                    {KYC_STATUS_LABELS[doc.verificationStatus]}
                  </Badge>
                </div>
                <p className="mt-1 text-xs text-fg-subtle">
                  {doc.docNumber ? (
                    <>
                      Doc <span className="nums">#{doc.docNumber}</span> ·{" "}
                    </>
                  ) : null}
                  Submitted {formatDateTime(doc.submittedAt)}
                </p>

                {/* The submitted file itself - approving/rejecting below was
                    previously the only thing this card offered, with no way
                    to actually see what was being approved. An image gets an
                    inline thumbnail (same pattern as the profile-photo card
                    below); a PDF gets a document icon - both link to the
                    full file, since a thumbnail alone isn't enough to read
                    an ID document's text. */}
                <div className="mt-3 flex items-center gap-3 border-t border-line pt-3">
                  {isImageFileRef(doc.fileRef) ? (
                    // next/image needs the host in next.config's allowlist and
                    // a provider-supplied URL can point anywhere (same
                    // reasoning as the profile-photo card below), so a plain
                    // img is the only workable element here.
                    // eslint-disable-next-line @next/next/no-img-element
                    <img
                      src={doc.fileRef}
                      alt={`${KYC_DOC_TYPE_LABELS[doc.docType]} submitted by ${provider.displayName}`}
                      className="h-14 w-14 shrink-0 rounded-lg border border-line object-cover"
                    />
                  ) : (
                    <span
                      aria-hidden
                      className="flex h-14 w-14 shrink-0 items-center justify-center rounded-lg border border-line bg-surface-2 text-xs font-medium text-fg-subtle"
                    >
                      PDF
                    </span>
                  )}
                  <a
                    href={doc.fileRef}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="text-sm font-medium text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
                  >
                    View document
                  </a>
                </div>

                {canWriteProvider && doc.verificationStatus === ProviderKycVerificationStatus.Pending ? (
                  <div className="mt-3 flex flex-col gap-3 border-t border-line pt-3 sm:flex-row sm:items-end">
                    <Button
                      variant="secondary"
                      loading={approveKycMutation.isPending && approveKycMutation.variables === doc.id}
                      onClick={() => approveKycMutation.mutate(doc.id)}
                    >
                      Approve
                    </Button>
                    <div className="flex-1">
                      <Field
                        label="Rejection reason"
                        value={rejectReasonByDoc[doc.id] ?? ""}
                        onChange={(e) => setRejectReasonByDoc((m) => ({ ...m, [doc.id]: e.target.value }))}
                      />
                    </div>
                    <Button
                      variant="danger"
                      disabled={!(rejectReasonByDoc[doc.id] ?? "").trim()}
                      onClick={() =>
                        setPendingKycRejection({ id: doc.id, label: KYC_DOC_TYPE_LABELS[doc.docType] })
                      }
                    >
                      Reject
                    </Button>
                  </div>
                ) : null}
              </li>
            ))}
          </ul>
        )}
      </Card>

      <Card
        title="Bank account"
        description="Structured payout details (docs/PROVIDER.md OPEN DECISIONS #3) - sits alongside the BankAccountProof KYC document above, which remains supporting evidence."
      >
        {provider.bankAccount === null ? (
          <EmptyState
            title="No bank account submitted yet"
            description="The provider submits these from the provider app; payouts are not gated on this, but it is used to process one."
          />
        ) : (
          <div className="rounded-xl border border-line p-3 text-sm">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <span className="font-medium text-fg">{provider.bankAccount.bankName}</span>
              <Badge tone={BANK_ACCOUNT_STATUS_TONES[provider.bankAccount.verificationStatus]}>
                {BANK_ACCOUNT_STATUS_LABELS[provider.bankAccount.verificationStatus]}
              </Badge>
            </div>
            <p className="nums mt-1 text-xs text-fg-subtle">
              {provider.bankAccount.accountHolderName} · {provider.bankAccount.accountNumber} · {provider.bankAccount.ifscCode}
            </p>
            <p className="mt-1 text-xs text-fg-subtle">
              Last updated {formatDateTime(provider.bankAccount.updatedAt)}
              {provider.bankAccount.verifiedAt ? ` · Reviewed ${formatDateTime(provider.bankAccount.verifiedAt)}` : ""}
            </p>
            {provider.bankAccount.rejectionReason ? (
              <p className="mt-1.5 text-xs text-danger">Rejected: {provider.bankAccount.rejectionReason}</p>
            ) : null}

            {canWriteProvider && provider.bankAccount.verificationStatus === ProviderBankAccountVerificationStatus.Pending ? (
              <div className="mt-3 flex flex-col gap-3 border-t border-line pt-3 sm:flex-row sm:items-end">
                <Button
                  variant="secondary"
                  loading={approveBankAccountMutation.isPending}
                  onClick={() => approveBankAccountMutation.mutate(provider.bankAccount!.id)}
                >
                  Approve
                </Button>
                <div className="flex-1">
                  <Field
                    label="Rejection reason"
                    value={bankAccountRejectReason}
                    onChange={(e) => setBankAccountRejectReason(e.target.value)}
                  />
                </div>
                <Button
                  variant="danger"
                  disabled={!bankAccountRejectReason.trim()}
                  onClick={() => setIsConfirmingBankAccountRejection(true)}
                >
                  Reject
                </Button>
              </div>
            ) : null}
          </div>
        )}
      </Card>

      <Card title="Background check" description="Distinct post-KYC step; required before activation (task 160)">
        {provider.backgroundChecks.length === 0 ? (
          <EmptyState
            title="No background check recorded yet"
            description={
              canWriteProvider
                ? "Record the outcome below — a provider cannot be activated without one."
                : "An admin with provider write access records this."
            }
          />
        ) : (
          <ul className="flex flex-col gap-2 text-sm">
            {provider.backgroundChecks.map((check) => (
              <li key={check.id} className="rounded-xl border border-line p-3">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <Badge tone={BACKGROUND_CHECK_TONES[check.status]}>
                    {BACKGROUND_CHECK_STATUS_LABELS[check.status]}
                  </Badge>
                  <span className="text-xs text-fg-subtle">{formatDateTime(check.checkedAt)}</span>
                </div>
                {check.notes ? <p className="mt-1.5 text-xs text-fg-muted">{check.notes}</p> : null}
              </li>
            ))}
          </ul>
        )}

        {canWriteProvider ? (
          <div className="mt-5 flex max-w-2xl flex-col gap-3 border-t border-line pt-5 sm:flex-row sm:items-end">
            <Select
              label="Outcome"
              value={bgStatus}
              onChange={(e) => setBgStatus(e.target.value)}
              options={[
                { value: String(ProviderBackgroundCheckStatus.Passed), label: "Passed" },
                { value: String(ProviderBackgroundCheckStatus.Failed), label: "Failed" },
              ]}
            />
            <div className="flex-1">
              <Field label="Notes (optional)" value={bgNotes} onChange={(e) => setBgNotes(e.target.value)} />
            </div>
            <Button loading={backgroundCheckMutation.isPending} onClick={() => backgroundCheckMutation.mutate()}>
              Record outcome
            </Button>
          </div>
        ) : null}
      </Card>
      </div>
      ) : null}

      {detailTab === "performance" ? (
      <Card title="Performance" description="Job-fulfilment summary (task 150c)">
        {performanceQuery.isPending ? (
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
            {Array.from({ length: 6 }, (_, index) => (
              <div key={index} className="rounded-2xl bg-surface p-5">
                <SkeletonText lines={2} />
              </div>
            ))}
          </div>
        ) : performanceQuery.isError ? (
          <SectionError error={performanceQuery.error} onRetry={() => performanceQuery.refetch()} />
        ) : (
          <Reveal className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
            <motion.div variants={revealItem}>
              <StatTile tone="brand" label="Total assignments" value={String(performanceQuery.data.totalAssignments)} />
            </motion.div>
            <motion.div variants={revealItem}>
              <StatTile tone="success" label="Accepted" value={String(performanceQuery.data.acceptedAssignments)} />
            </motion.div>
            <motion.div variants={revealItem}>
              <StatTile tone="danger" label="Rejected" value={String(performanceQuery.data.rejectedAssignments)} />
            </motion.div>
            <motion.div variants={revealItem}>
              <StatTile tone="success" label="Completed jobs" value={String(performanceQuery.data.completedJobs)} />
            </motion.div>
            <motion.div variants={revealItem}>
              <StatTile tone="info" label="In-progress jobs" value={String(performanceQuery.data.inProgressJobs)} />
            </motion.div>
            <motion.div variants={revealItem}>
              <StatTile tone="accent" label="Lifetime earnings" value={formatCurrency(performanceQuery.data.lifetimeEarnings)} />
            </motion.div>
          </Reveal>
        )}
      </Card>
      ) : null}

      {detailTab === "earnings" ? (
      <div className="flex flex-col gap-6">
      <Card
        title="Earnings ledger"
        description={
          earningsQuery.data
            ? `Current balance: ${formatCurrency(earningsQuery.data.currentBalance)}`
            : "Append-only ledger (task 148)"
        }
      >
        {earningsQuery.isPending ? (
          <SkeletonText lines={4} />
        ) : earningsQuery.isError ? (
          <SectionError error={earningsQuery.error} onRetry={() => earningsQuery.refetch()} />
        ) : earningsQuery.data.entries.length === 0 ? (
          <EmptyState title="No earning activity yet" description="Completed jobs credit this ledger automatically." />
        ) : (
          <ul className="flex flex-col gap-2 text-sm">
            {earningsQuery.data.entries.map((entry) => (
              <li
                key={entry.id}
                className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-line p-3"
              >
                <span className="min-w-0 flex-1 text-fg">{entry.description}</span>
                <span className="nums text-xs text-fg-subtle">{formatDate(entry.createdAtUtc)}</span>
                <span
                  className={
                    entry.entryType === ProviderEarningEntryType.Credit
                      ? "nums font-medium text-success"
                      : "nums font-medium text-danger"
                  }
                >
                  {entry.entryType === ProviderEarningEntryType.Credit ? "+" : "−"}
                  {formatCurrency(entry.amount)}
                </span>
              </li>
            ))}
          </ul>
        )}

        {canWritePayout ? (
          <div className="mt-5 flex max-w-2xl flex-col gap-3 border-t border-line pt-5 sm:flex-row sm:items-end">
            <Select
              label="Type"
              value={adjustmentType}
              onChange={(e) => setAdjustmentType(e.target.value)}
              options={[
                { value: String(ProviderEarningEntryType.Credit), label: "Credit" },
                { value: String(ProviderEarningEntryType.Debit), label: "Debit (penalty)" },
              ]}
            />
            <Field
              label="Amount"
              type="number"
              min="0.01"
              step="0.01"
              leading="₹"
              value={adjustmentAmount}
              onChange={(e) => setAdjustmentAmount(e.target.value)}
            />
            <div className="flex-1">
              <Field label="Description" value={adjustmentDescription} onChange={(e) => setAdjustmentDescription(e.target.value)} />
            </div>
            <Button
              disabled={!adjustmentAmount || !adjustmentDescription.trim()}
              loading={adjustmentMutation.isPending}
              onClick={() => adjustmentMutation.mutate()}
            >
              Record adjustment
            </Button>
          </div>
        ) : null}
      </Card>

      <Card title="Payouts" description="Manual bank-transfer payout batches (OPEN DECISIONS #3, task 148)">
        {payoutsQuery.isPending ? (
          <SkeletonText lines={4} />
        ) : payoutsQuery.isError ? (
          <SectionError error={payoutsQuery.error} onRetry={() => payoutsQuery.refetch()} />
        ) : payoutsQuery.data.items.length === 0 ? (
          <EmptyState
            title="No payout batches yet"
            description={
              canWritePayout
                ? "Run a batch below to settle this provider's outstanding balance."
                : "An admin with payout write access can run one."
            }
          />
        ) : (
          <ul className="flex flex-col gap-3 text-sm">
            {payoutsQuery.data.items.map((payout) => (
              <li key={payout.id} className="rounded-xl border border-line p-3">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <span className="nums font-medium text-fg">
                    {payout.periodStart} → {payout.periodEnd}
                  </span>
                  <Badge tone={PAYOUT_STATUS_TONES[payout.status]}>{PAYOUT_STATUS_LABELS[payout.status]}</Badge>
                </div>
                <p className="nums mt-1 text-fg">{formatCurrency(payout.totalAmount)}</p>
                {payout.status !== ProviderPayoutStatus.Pending ? (
                  <p className="mt-1 text-xs text-fg-subtle">Processed via: {PROCESSED_VIA_LABELS[payout.processedVia]}</p>
                ) : null}
                {payout.payoutReference ? (
                  <p className="mt-1 text-xs text-fg-subtle">Reference: {payout.payoutReference}</p>
                ) : null}

                {/* Product decision: the bank account an admin needs to process
                    this transfer, right here - no navigating to the
                    Verification tab. */}
                {payout.bankAccount ? (
                  <div className="mt-2 flex flex-wrap items-center gap-2 rounded-lg bg-surface-2 px-2.5 py-1.5 text-xs text-fg-muted">
                    <span className="nums">
                      {payout.bankAccount.accountHolderName} · {payout.bankAccount.accountNumber} · {payout.bankAccount.ifscCode} ·{" "}
                      {payout.bankAccount.bankName}
                    </span>
                    <Badge tone={BANK_ACCOUNT_STATUS_TONES[payout.bankAccount.verificationStatus]}>
                      {BANK_ACCOUNT_STATUS_LABELS[payout.bankAccount.verificationStatus]}
                    </Badge>
                  </div>
                ) : (
                  <p className="mt-2 text-xs text-danger">No bank account on file for this provider yet.</p>
                )}

                {canWritePayout && payout.status === ProviderPayoutStatus.Pending ? (
                  <FormActions align="start" className="mt-3">
                    <Button
                      size="sm"
                      variant="secondary"
                      loading={
                        payoutStatusMutation.isPending && payoutStatusMutation.variables?.payoutId === payout.id
                      }
                      onClick={() => payoutStatusMutation.mutate({ payoutId: payout.id, status: ProviderPayoutStatus.Processing })}
                    >
                      Mark processing
                    </Button>
                    {payout.isGatewayConfigured ? (
                      <Button
                        size="sm"
                        loading={payViaPayUMutation.isPending && payViaPayUMutation.variables === payout.id}
                        onClick={() => payViaPayUMutation.mutate(payout.id)}
                      >
                        Pay via PayU
                      </Button>
                    ) : null}
                  </FormActions>
                ) : null}

                {canWritePayout && payout.status === ProviderPayoutStatus.Processing ? (
                  <div className="mt-3 flex flex-col gap-3 border-t border-line pt-3 sm:flex-row sm:items-end">
                    <div className="flex-1">
                      <Field
                        label="Bank transfer reference"
                        value={payoutReferenceByPayout[payout.id] ?? ""}
                        onChange={(e) => setPayoutReferenceByPayout((m) => ({ ...m, [payout.id]: e.target.value }))}
                      />
                    </div>
                    <Button
                      disabled={!(payoutReferenceByPayout[payout.id] ?? "").trim()}
                      loading={
                        payoutStatusMutation.isPending &&
                        payoutStatusMutation.variables?.payoutId === payout.id &&
                        payoutStatusMutation.variables?.status === ProviderPayoutStatus.Paid
                      }
                      onClick={() =>
                        payoutStatusMutation.mutate({
                          payoutId: payout.id,
                          status: ProviderPayoutStatus.Paid,
                          payoutReference: (payoutReferenceByPayout[payout.id] ?? "").trim(),
                        })
                      }
                    >
                      Mark paid
                    </Button>
                    <Button variant="danger" onClick={() => setPendingPayoutFailure(payout.id)}>
                      Mark failed
                    </Button>
                  </div>
                ) : null}
              </li>
            ))}
          </ul>
        )}

        {canWritePayout ? (
          <div className="mt-5 flex max-w-2xl flex-col gap-3 border-t border-line pt-5 sm:flex-row sm:items-end">
            <Field label="Period start" type="date" value={payoutPeriodStart} onChange={(e) => setPayoutPeriodStart(e.target.value)} />
            <Field label="Period end" type="date" value={payoutPeriodEnd} onChange={(e) => setPayoutPeriodEnd(e.target.value)} />
            <Button
              disabled={!payoutPeriodStart || !payoutPeriodEnd}
              loading={createPayoutMutation.isPending}
              onClick={() => createPayoutMutation.mutate()}
            >
              Run payout batch
            </Button>
          </div>
        ) : null}
      </Card>
      </div>
      ) : null}

      <ConfirmDialog
        open={confirmSuspend}
        title="Suspend this provider?"
        description="They are removed from assignment immediately and cannot accept new jobs."
        confirmLabel="Suspend provider"
        cancelLabel="Keep active"
        loading={suspendMutation.isPending}
        error={suspendMutation.isError ? describeError(suspendMutation.error) : null}
        onCancel={() => setConfirmSuspend(false)}
        onConfirm={() => suspendMutation.mutate()}
      >
        <p className="text-sm text-fg-muted">
          Reason: <span className="font-medium text-fg">{suspendReason}</span>
        </p>
      </ConfirmDialog>

      <ConfirmDialog
        open={pendingKycRejection !== null}
        title="Reject this KYC document?"
        description="The provider must resubmit before they can be activated."
        confirmLabel="Reject document"
        cancelLabel="Keep pending"
        loading={rejectKycMutation.isPending}
        error={rejectKycMutation.isError ? describeError(rejectKycMutation.error) : null}
        onCancel={() => setPendingKycRejection(null)}
        onConfirm={() => {
          if (!pendingKycRejection) return;
          rejectKycMutation.mutate({
            documentId: pendingKycRejection.id,
            reason: (rejectReasonByDoc[pendingKycRejection.id] ?? "").trim(),
          });
        }}
      >
        {pendingKycRejection ? (
          <p className="text-sm text-fg-muted">
            {pendingKycRejection.label} —{" "}
            <span className="font-medium text-fg">{rejectReasonByDoc[pendingKycRejection.id] ?? ""}</span>
          </p>
        ) : null}
      </ConfirmDialog>

      <ConfirmDialog
        open={isConfirmingBankAccountRejection}
        title="Reject these bank account details?"
        description="The provider must resubmit before these details can be trusted for a payout."
        confirmLabel="Reject details"
        cancelLabel="Keep pending"
        loading={rejectBankAccountMutation.isPending}
        error={rejectBankAccountMutation.isError ? describeError(rejectBankAccountMutation.error) : null}
        onCancel={() => setIsConfirmingBankAccountRejection(false)}
        onConfirm={() => {
          if (!provider.bankAccount) return;
          rejectBankAccountMutation.mutate({ bankAccountId: provider.bankAccount.id, reason: bankAccountRejectReason.trim() });
        }}
      >
        <p className="text-sm text-fg-muted">
          Reason shown to the provider —{" "}
          <span className="font-medium text-fg">{bankAccountRejectReason}</span>
        </p>
      </ConfirmDialog>

      <ConfirmDialog
        open={isConfirmingPhotoRejection}
        title="Reject this profile photo?"
        description="Customers keep seeing the placeholder avatar until the provider submits a new one."
        confirmLabel="Reject photo"
        cancelLabel="Keep pending"
        loading={rejectPhotoMutation.isPending}
        error={rejectPhotoMutation.isError ? describeError(rejectPhotoMutation.error) : null}
        onCancel={() => setIsConfirmingPhotoRejection(false)}
        onConfirm={() => rejectPhotoMutation.mutate(photoRejectReason.trim())}
      >
        <p className="text-sm text-fg-muted">
          Reason shown to the provider —{" "}
          <span className="font-medium text-fg">{photoRejectReason}</span>
        </p>
      </ConfirmDialog>

      <ConfirmDialog
        open={pendingPayoutFailure !== null}
        title="Mark this payout failed?"
        description="The batch is closed as failed and the amount stays owed to the provider."
        confirmLabel="Mark failed"
        cancelLabel="Keep processing"
        loading={payoutStatusMutation.isPending}
        error={payoutStatusMutation.isError ? describeError(payoutStatusMutation.error) : null}
        onCancel={() => setPendingPayoutFailure(null)}
        onConfirm={() => {
          if (!pendingPayoutFailure) return;
          payoutStatusMutation.mutate({ payoutId: pendingPayoutFailure, status: ProviderPayoutStatus.Failed });
        }}
      />
    </div>
  );
}

/**
 * Dispatch capacity limits editor (task 308). Owns its own state, seeded
 * from the loaded capacity, same reason as `ProfileEditor` below - blank
 * means "unlimited" here (mirroring `ProviderCapacity`'s own null-is-
 * unlimited convention), not "leave unchanged".
 */
function CapacityEditor({
  capacity,
  saving,
  onSave,
}: {
  capacity: ProviderCapacity;
  saving: boolean;
  onSave: (values: { maxJobsPerDay: number | null; maxJobsPerSlot: number | null }) => void;
}) {
  const [maxJobsPerDay, setMaxJobsPerDay] = useState(capacity.maxJobsPerDay?.toString() ?? "");
  const [maxJobsPerSlot, setMaxJobsPerSlot] = useState(capacity.maxJobsPerSlot?.toString() ?? "");

  const invalid =
    (maxJobsPerDay.trim() !== "" && Number(maxJobsPerDay) <= 0) ||
    (maxJobsPerSlot.trim() !== "" && Number(maxJobsPerSlot) <= 0);

  return (
    <div className="flex flex-col gap-4">
      <FormGrid columns={2}>
        <Field
          label="Max jobs per day"
          type="number"
          min="1"
          step="1"
          hint="Leave blank for unlimited."
          value={maxJobsPerDay}
          onChange={(e) => setMaxJobsPerDay(e.target.value)}
        />
        <Field
          label="Max jobs per slot"
          type="number"
          min="1"
          step="1"
          hint="Leave blank for unlimited."
          value={maxJobsPerSlot}
          onChange={(e) => setMaxJobsPerSlot(e.target.value)}
        />
      </FormGrid>
      <FormActions align="start">
        <Button
          variant="secondary"
          disabled={invalid}
          loading={saving}
          onClick={() =>
            onSave({
              maxJobsPerDay: maxJobsPerDay.trim() === "" ? null : Number(maxJobsPerDay),
              maxJobsPerSlot: maxJobsPerSlot.trim() === "" ? null : Number(maxJobsPerSlot),
            })
          }
        >
          Save capacity
        </Button>
      </FormActions>
    </div>
  );
}

/**
 * Profile name/email editor.
 *
 * Owns its own state, seeded from the loaded provider. The previous version
 * kept the three fields in page state initialised to `""` and rendered them
 * with `defaultValue`, then submitted all three on save — so an admin who
 * changed only the display name saved an empty legal name and email over the
 * real ones.
 */
function ProfileEditor({
  provider,
  saving,
  onSave,
}: {
  provider: ProviderDetail;
  saving: boolean;
  onSave: (values: {
    legalName: string;
    displayName: string;
    email: string;
    latitude: number | null;
    longitude: number | null;
  }) => void;
}) {
  const [legalName, setLegalName] = useState(provider.legalName);
  const [displayName, setDisplayName] = useState(provider.displayName);
  const [email, setEmail] = useState(provider.email ?? "");
  const [latitude, setLatitude] = useState(provider.latitude?.toString() ?? "");
  const [longitude, setLongitude] = useState(provider.longitude?.toString() ?? "");

  // Both-or-neither, mirroring Provider.UpdateLocation's own guard - caught
  // here too so the button disables instead of round-tripping a 400.
  const hasLatitude = latitude.trim() !== "";
  const hasLongitude = longitude.trim() !== "";
  const locationIncomplete = hasLatitude !== hasLongitude;

  return (
    <div className="flex flex-col gap-4">
      <FormGrid columns={3}>
        <Field label="Legal name" required value={legalName} onChange={(e) => setLegalName(e.target.value)} />
        <Field label="Display name" required value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
        <Field label="Email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} />
      </FormGrid>
      <FormGrid columns={3}>
        <Field
          label="Latitude"
          type="number"
          step="any"
          hint="Feeds automatic job assignment by nearest provider. Leave both blank if unknown."
          value={latitude}
          onChange={(e) => setLatitude(e.target.value)}
        />
        <Field
          label="Longitude"
          type="number"
          step="any"
          value={longitude}
          onChange={(e) => setLongitude(e.target.value)}
        />
      </FormGrid>
      <FormActions align="start">
        <Button
          variant="secondary"
          disabled={!legalName.trim() || !displayName.trim() || locationIncomplete}
          loading={saving}
          onClick={() =>
            onSave({
              legalName,
              displayName,
              email,
              latitude: latitude.trim() === "" ? null : Number(latitude),
              longitude: longitude.trim() === "" ? null : Number(longitude),
            })
          }
        >
          Save profile
        </Button>
      </FormActions>
    </div>
  );
}
