"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useEffect, useState } from "react";
import { Alert, Button, Card, Field, PageHeading, Select } from "@/components/ui";
import { describeError } from "@/lib/api";
import { getSessionClaims, subscribeToAuthChanges } from "@/lib/auth";
import {
  activateProvider,
  approveKycDocument,
  createPayoutBatch,
  getProviderDetail,
  getProviderEarnings,
  getProviderPerformance,
  reactivateProvider,
  recordBackgroundCheck,
  recordEarningAdjustment,
  rejectKycDocument,
  searchPayouts,
  suspendProvider,
  updateProvider,
  updatePayoutStatus,
} from "@/lib/providers-api";
import {
  ProviderBackgroundCheckStatus,
  ProviderEarningEntryType,
  ProviderEarningSourceType,
  ProviderKycDocumentType,
  ProviderKycVerificationStatus,
  ProviderOnboardingStatus,
  ProviderPayoutStatus,
  ProviderStatus,
} from "@/lib/providers-types";
import type { AdminSessionClaims } from "@/lib/types";

function useAdminClaims(): AdminSessionClaims | null {
  const [claims, setClaims] = useState<AdminSessionClaims | null>(null);
  useEffect(() => {
    const sync = () => setClaims(getSessionClaims());
    sync();
    return subscribeToAuthChanges(sync);
  }, []);
  return claims;
}

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
};

const BACKGROUND_CHECK_STATUS_LABELS: Record<ProviderBackgroundCheckStatus, string> = {
  [ProviderBackgroundCheckStatus.Pending]: "Pending",
  [ProviderBackgroundCheckStatus.Passed]: "Passed",
  [ProviderBackgroundCheckStatus.Failed]: "Failed",
};

const PAYOUT_STATUS_LABELS: Record<ProviderPayoutStatus, string> = {
  [ProviderPayoutStatus.Pending]: "Pending",
  [ProviderPayoutStatus.Processing]: "Processing",
  [ProviderPayoutStatus.Paid]: "Paid",
  [ProviderPayoutStatus.Failed]: "Failed",
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
    queryFn: () => searchPayouts(providerId),
  });

  const [actionError, setActionError] = useState<string | null>(null);
  const [actionNotice, setActionNotice] = useState<string | null>(null);

  const [editLegalName, setEditLegalName] = useState("");
  const [editDisplayName, setEditDisplayName] = useState("");
  const [editEmail, setEditEmail] = useState("");
  const [suspendReason, setSuspendReason] = useState("");

  const [rejectReasonByDoc, setRejectReasonByDoc] = useState<Record<string, string>>({});

  const [bgStatus, setBgStatus] = useState(String(ProviderBackgroundCheckStatus.Passed));
  const [bgNotes, setBgNotes] = useState("");

  const [adjustmentType, setAdjustmentType] = useState(String(ProviderEarningEntryType.Credit));
  const [adjustmentAmount, setAdjustmentAmount] = useState("");
  const [adjustmentDescription, setAdjustmentDescription] = useState("");

  const [payoutPeriodStart, setPayoutPeriodStart] = useState("");
  const [payoutPeriodEnd, setPayoutPeriodEnd] = useState("");
  const [payoutReferenceByPayout, setPayoutReferenceByPayout] = useState<Record<string, string>>({});

  const invalidateAll = () => {
    queryClient.invalidateQueries({ queryKey: ["admin-provider-detail", providerId] });
    queryClient.invalidateQueries({ queryKey: ["admin-provider-performance", providerId] });
    queryClient.invalidateQueries({ queryKey: ["admin-provider-earnings", providerId] });
    queryClient.invalidateQueries({ queryKey: ["admin-provider-payouts", providerId] });
  };

  const onError = (err: unknown) => setActionError(describeError(err));
  const onSuccess = (notice: string) => {
    setActionError(null);
    setActionNotice(notice);
    invalidateAll();
  };

  const updateMutation = useMutation({
    mutationFn: () => updateProvider(providerId, { legalName: editLegalName, displayName: editDisplayName, email: editEmail || undefined }),
    onSuccess: () => onSuccess("Profile updated."),
    onError,
  });

  const suspendMutation = useMutation({
    mutationFn: () => suspendProvider(providerId, { reason: suspendReason }),
    onSuccess: () => {
      setSuspendReason("");
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

  const rejectKycMutation = useMutation({
    mutationFn: ({ documentId, reason }: { documentId: string; reason: string }) => rejectKycDocument(documentId, { reason }),
    onSuccess: () => onSuccess("KYC document rejected."),
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
    onSuccess: () => onSuccess("Payout status updated."),
    onError,
  });

  if (detailQuery.isPending) {
    return <p className="text-sm text-neutral-500">Loading provider…</p>;
  }

  if (detailQuery.isError) {
    return <Alert>{describeError(detailQuery.error)}</Alert>;
  }

  const provider = detailQuery.data;
  const canActivate =
    provider.status === ProviderStatus.PendingVerification &&
    (provider.onboardingStatus === ProviderOnboardingStatus.KycVerified || provider.onboardingStatus === ProviderOnboardingStatus.Completed);

  return (
    <div className="mx-auto flex w-full max-w-4xl flex-col gap-6">
      <div className="flex items-center justify-between">
        <PageHeading title={provider.displayName} subtitle={`${provider.phone}${provider.email ? ` · ${provider.email}` : ""}`} />
        <Link href="/providers" className="text-sm underline-offset-2 hover:underline">
          Back to providers
        </Link>
      </div>

      {actionError ? <Alert>{actionError}</Alert> : null}
      {actionNotice ? <Alert tone="success">{actionNotice}</Alert> : null}

      <Card
        title="Profile"
        description={`Status: ${STATUS_LABELS[provider.status]} · Onboarding: ${ONBOARDING_LABELS[provider.onboardingStatus]}`}
      >
        {canWriteProvider ? (
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <Field label="Legal name" defaultValue={provider.legalName} onChange={(e) => setEditLegalName(e.target.value)} />
            <Field label="Display name" defaultValue={provider.displayName} onChange={(e) => setEditDisplayName(e.target.value)} />
            <Field label="Email" defaultValue={provider.email ?? ""} onChange={(e) => setEditEmail(e.target.value)} />
          </div>
        ) : null}

        <div className="mt-4 flex flex-wrap items-center gap-3">
          {canWriteProvider ? (
            <Button
              variant="secondary"
              disabled={updateMutation.isPending}
              onClick={() => {
                updateMutation.mutate();
              }}
            >
              {updateMutation.isPending ? "Saving…" : "Save profile"}
            </Button>
          ) : null}

          {canWriteProvider && provider.status === ProviderStatus.PendingVerification && canActivate ? (
            <Button disabled={activateMutation.isPending} onClick={() => activateMutation.mutate()}>
              {activateMutation.isPending ? "Activating…" : "Activate provider"}
            </Button>
          ) : null}

          {canWriteProvider && provider.status === ProviderStatus.Suspended ? (
            <Button disabled={reactivateMutation.isPending} onClick={() => reactivateMutation.mutate()}>
              {reactivateMutation.isPending ? "Reactivating…" : "Reactivate"}
            </Button>
          ) : null}
        </div>

        {canWriteProvider && provider.status !== ProviderStatus.Suspended ? (
          <div className="mt-4 flex flex-col gap-2 border-t border-black/10 pt-4 dark:border-white/15 sm:flex-row sm:items-end">
            <div className="flex-1">
              <Field label="Suspend reason" value={suspendReason} onChange={(e) => setSuspendReason(e.target.value)} />
            </div>
            <Button variant="danger" disabled={!suspendReason.trim() || suspendMutation.isPending} onClick={() => suspendMutation.mutate()}>
              {suspendMutation.isPending ? "Suspending…" : "Suspend provider"}
            </Button>
          </div>
        ) : null}
      </Card>

      <Card title="KYC documents" description="Approve or reject each submitted document (task 150b)">
        {provider.kycDocuments.length === 0 ? (
          <p className="text-sm text-neutral-500">No KYC documents submitted yet.</p>
        ) : (
          <ul className="flex flex-col gap-3 text-sm">
            {provider.kycDocuments.map((doc) => (
              <li key={doc.id} className="rounded-lg border border-black/10 p-3 dark:border-white/15">
                <div className="flex items-center justify-between">
                  <span className="font-medium">{KYC_DOC_TYPE_LABELS[doc.docType]}</span>
                  <span>{KYC_STATUS_LABELS[doc.verificationStatus]}</span>
                </div>
                <p className="mt-1 text-xs text-neutral-500">
                  {doc.docNumber ? `Doc #${doc.docNumber} · ` : ""}Submitted {new Date(doc.submittedAt).toLocaleString()}
                </p>

                {canWriteProvider && doc.verificationStatus === ProviderKycVerificationStatus.Pending ? (
                  <div className="mt-3 flex flex-col gap-2 sm:flex-row sm:items-end">
                    <Button
                      variant="secondary"
                      disabled={approveKycMutation.isPending}
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
                      disabled={!(rejectReasonByDoc[doc.id] ?? "").trim() || rejectKycMutation.isPending}
                      onClick={() => rejectKycMutation.mutate({ documentId: doc.id, reason: (rejectReasonByDoc[doc.id] ?? "").trim() })}
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

      <Card title="Background check" description="Distinct post-KYC step; required before activation (task 160)">
        {provider.backgroundChecks.length === 0 ? (
          <p className="text-sm text-neutral-500">No background check recorded yet.</p>
        ) : (
          <ul className="flex flex-col gap-2 text-sm">
            {provider.backgroundChecks.map((check) => (
              <li key={check.id} className="rounded-lg border border-black/10 p-3 dark:border-white/15">
                <div className="flex items-center justify-between">
                  <span className="font-medium">{BACKGROUND_CHECK_STATUS_LABELS[check.status]}</span>
                  <span className="text-xs text-neutral-500">{new Date(check.checkedAt).toLocaleString()}</span>
                </div>
                {check.notes ? <p className="mt-1 text-xs text-neutral-600 dark:text-neutral-400">{check.notes}</p> : null}
              </li>
            ))}
          </ul>
        )}

        {canWriteProvider ? (
          <div className="mt-4 flex flex-col gap-3 border-t border-black/10 pt-4 dark:border-white/15 sm:flex-row sm:items-end">
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
            <Button disabled={backgroundCheckMutation.isPending} onClick={() => backgroundCheckMutation.mutate()}>
              {backgroundCheckMutation.isPending ? "Recording…" : "Record outcome"}
            </Button>
          </div>
        ) : null}
      </Card>

      <Card title="Performance" description="Job-fulfilment summary (task 150c)">
        {performanceQuery.isPending ? (
          <p className="text-sm text-neutral-500">Loading…</p>
        ) : performanceQuery.isError ? (
          <Alert>{describeError(performanceQuery.error)}</Alert>
        ) : (
          <dl className="grid grid-cols-2 gap-x-4 gap-y-2 text-sm sm:grid-cols-3">
            <div>
              <dt className="text-neutral-500">Total assignments</dt>
              <dd>{performanceQuery.data.totalAssignments}</dd>
            </div>
            <div>
              <dt className="text-neutral-500">Accepted</dt>
              <dd>{performanceQuery.data.acceptedAssignments}</dd>
            </div>
            <div>
              <dt className="text-neutral-500">Rejected</dt>
              <dd>{performanceQuery.data.rejectedAssignments}</dd>
            </div>
            <div>
              <dt className="text-neutral-500">Completed jobs</dt>
              <dd>{performanceQuery.data.completedJobs}</dd>
            </div>
            <div>
              <dt className="text-neutral-500">In-progress jobs</dt>
              <dd>{performanceQuery.data.inProgressJobs}</dd>
            </div>
            <div>
              <dt className="text-neutral-500">Lifetime earnings</dt>
              <dd>₹{performanceQuery.data.lifetimeEarnings.toFixed(2)}</dd>
            </div>
          </dl>
        )}
      </Card>

      <Card
        title="Earnings ledger"
        description={earningsQuery.data ? `Current balance: ₹${earningsQuery.data.currentBalance.toFixed(2)}` : "Append-only ledger (task 148)"}
      >
        {earningsQuery.isPending ? (
          <p className="text-sm text-neutral-500">Loading…</p>
        ) : earningsQuery.isError ? (
          <Alert>{describeError(earningsQuery.error)}</Alert>
        ) : earningsQuery.data.entries.length === 0 ? (
          <p className="text-sm text-neutral-500">No earning activity yet.</p>
        ) : (
          <ul className="flex flex-col gap-2 text-sm">
            {earningsQuery.data.entries.map((entry) => (
              <li key={entry.id} className="flex items-center justify-between rounded-lg border border-black/10 p-3 dark:border-white/15">
                <span>{entry.description}</span>
                <span>{new Date(entry.createdAtUtc).toLocaleDateString()}</span>
                <span className={entry.entryType === ProviderEarningEntryType.Credit ? "text-green-700 dark:text-green-400" : "text-red-700 dark:text-red-400"}>
                  {entry.entryType === ProviderEarningEntryType.Credit ? "+" : "-"}₹{entry.amount.toFixed(2)}
                </span>
              </li>
            ))}
          </ul>
        )}

        {canWritePayout ? (
          <div className="mt-4 flex flex-col gap-3 border-t border-black/10 pt-4 dark:border-white/15 sm:flex-row sm:items-end">
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
              value={adjustmentAmount}
              onChange={(e) => setAdjustmentAmount(e.target.value)}
            />
            <div className="flex-1">
              <Field label="Description" value={adjustmentDescription} onChange={(e) => setAdjustmentDescription(e.target.value)} />
            </div>
            <Button
              disabled={!adjustmentAmount || !adjustmentDescription.trim() || adjustmentMutation.isPending}
              onClick={() => adjustmentMutation.mutate()}
            >
              {adjustmentMutation.isPending ? "Recording…" : "Record adjustment"}
            </Button>
          </div>
        ) : null}
      </Card>

      <Card title="Payouts" description="Manual bank-transfer payout batches (OPEN DECISIONS #3, task 148)">
        {payoutsQuery.isPending ? (
          <p className="text-sm text-neutral-500">Loading…</p>
        ) : payoutsQuery.isError ? (
          <Alert>{describeError(payoutsQuery.error)}</Alert>
        ) : payoutsQuery.data.items.length === 0 ? (
          <p className="text-sm text-neutral-500">No payout batches yet.</p>
        ) : (
          <ul className="flex flex-col gap-3 text-sm">
            {payoutsQuery.data.items.map((payout) => (
              <li key={payout.id} className="rounded-lg border border-black/10 p-3 dark:border-white/15">
                <div className="flex items-center justify-between">
                  <span className="font-medium">
                    {payout.periodStart} → {payout.periodEnd}
                  </span>
                  <span>{PAYOUT_STATUS_LABELS[payout.status]}</span>
                </div>
                <p className="mt-1 text-neutral-600 dark:text-neutral-400">₹{payout.totalAmount.toFixed(2)}</p>
                {payout.payoutReference ? <p className="mt-1 text-xs text-neutral-500">Reference: {payout.payoutReference}</p> : null}

                {canWritePayout && payout.status === ProviderPayoutStatus.Pending ? (
                  <Button
                    variant="secondary"
                    className="mt-2"
                    disabled={payoutStatusMutation.isPending}
                    onClick={() => payoutStatusMutation.mutate({ payoutId: payout.id, status: ProviderPayoutStatus.Processing })}
                  >
                    Mark processing
                  </Button>
                ) : null}

                {canWritePayout && payout.status === ProviderPayoutStatus.Processing ? (
                  <div className="mt-2 flex flex-col gap-2 sm:flex-row sm:items-end">
                    <div className="flex-1">
                      <Field
                        label="Bank transfer reference"
                        value={payoutReferenceByPayout[payout.id] ?? ""}
                        onChange={(e) => setPayoutReferenceByPayout((m) => ({ ...m, [payout.id]: e.target.value }))}
                      />
                    </div>
                    <Button
                      disabled={!(payoutReferenceByPayout[payout.id] ?? "").trim() || payoutStatusMutation.isPending}
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
                    <Button
                      variant="danger"
                      disabled={payoutStatusMutation.isPending}
                      onClick={() => payoutStatusMutation.mutate({ payoutId: payout.id, status: ProviderPayoutStatus.Failed })}
                    >
                      Mark failed
                    </Button>
                  </div>
                ) : null}
              </li>
            ))}
          </ul>
        )}

        {canWritePayout ? (
          <div className="mt-4 flex flex-col gap-3 border-t border-black/10 pt-4 dark:border-white/15 sm:flex-row sm:items-end">
            <Field label="Period start" type="date" value={payoutPeriodStart} onChange={(e) => setPayoutPeriodStart(e.target.value)} />
            <Field label="Period end" type="date" value={payoutPeriodEnd} onChange={(e) => setPayoutPeriodEnd(e.target.value)} />
            <Button
              disabled={!payoutPeriodStart || !payoutPeriodEnd || createPayoutMutation.isPending}
              onClick={() => createPayoutMutation.mutate()}
            >
              {createPayoutMutation.isPending ? "Running…" : "Run payout batch"}
            </Button>
          </div>
        ) : null}
      </Card>
    </div>
  );
}
