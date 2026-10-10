"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useState } from "react";
import { Alert, Badge, Button, Card, EmptyState, Field, PageHeading, SkeletonText, Tabs } from "@/components/ui";
import { Breadcrumbs, ConfirmDialog, formatDateTime } from "@/components/data-table";
import { SectionError } from "@/components/screen-states";
import { describeError } from "@/lib/api";
import {
  approveBankAccount,
  approveKycDocument,
  approveProviderPhoto,
  listPendingBankAccounts,
  listPendingKycDocuments,
  listPendingProviderPhotos,
  rejectBankAccount,
  rejectKycDocument,
  rejectProviderPhoto,
} from "@/lib/providers-api";
import { ProviderKycDocumentType } from "@/lib/providers-types";
import { useAdminClaims } from "@/lib/use-admin-claims";
import { ProvidersTabs } from "../_components/ProvidersTabs";

const KYC_DOC_TYPE_LABELS: Record<ProviderKycDocumentType, string> = {
  [ProviderKycDocumentType.IdentityProof]: "Identity proof",
  [ProviderKycDocumentType.AddressProof]: "Address proof",
  [ProviderKycDocumentType.BankAccountProof]: "Bank account proof",
  [ProviderKycDocumentType.ProfessionalCertificate]: "Professional certificate",
  [ProviderKycDocumentType.Other]: "Other",
};

/** KYC uploads are restricted server-side to JPEG/PNG/WebP or PDF - same check as the provider detail page's Verification tab. */
function isImageFileRef(fileRef: string): boolean {
  return /\.(jpe?g|png|webp)$/i.test(new URL(fileRef).pathname);
}

/**
 * The KYC verification queue (Provider Management UX pass, docs/OPEN-FIXES-FEATURES.csv):
 * every KYC document and profile photo across every provider still awaiting
 * a verdict, in one worklist, instead of the previous "search for a specific
 * provider, then check their Verification tab" flow. Documents and photos
 * are segmented by tab rather than merged into one undifferentiated list -
 * both are the same "view artifact, approve/reject with a reason" task
 * shape, but an admin working through one queue rarely wants the other
 * interleaved.
 *
 * Reuses the exact approve/reject mutations and card layout the provider
 * detail page's own Verification tab and Overview photo card already use -
 * this is the same review action from a cross-provider queue, not a second
 * implementation of it.
 */
export default function VerificationQueuePage() {
  const claims = useAdminClaims();
  const canWriteProvider = claims?.permissions.includes("provider.write") ?? false;
  const queryClient = useQueryClient();

  const [queueTab, setQueueTab] = useState<"documents" | "photos" | "bankAccounts">("documents");
  const [actionError, setActionError] = useState<string | null>(null);
  const [actionNotice, setActionNotice] = useState<string | null>(null);

  const documentsQuery = useQuery({
    queryKey: ["admin-provider-kyc-queue"],
    queryFn: () => listPendingKycDocuments(),
  });
  const photosQuery = useQuery({
    queryKey: ["admin-provider-photo-queue"],
    queryFn: () => listPendingProviderPhotos(),
  });
  const bankAccountsQuery = useQuery({
    queryKey: ["admin-provider-bank-account-queue"],
    queryFn: () => listPendingBankAccounts(),
  });

  const [rejectReasonByDoc, setRejectReasonByDoc] = useState<Record<string, string>>({});
  const [pendingKycRejection, setPendingKycRejection] = useState<{ id: string; label: string } | null>(null);

  const [photoRejectReasonByProvider, setPhotoRejectReasonByProvider] = useState<Record<string, string>>({});
  const [pendingPhotoRejection, setPendingPhotoRejection] = useState<{ providerId: string; label: string } | null>(null);

  const [bankAccountRejectReasonById, setBankAccountRejectReasonById] = useState<Record<string, string>>({});
  const [pendingBankAccountRejection, setPendingBankAccountRejection] = useState<{ id: string; label: string } | null>(null);

  const onError = (err: unknown) => setActionError(describeError(err));
  const onSuccess = (notice: string) => {
    setActionError(null);
    setActionNotice(notice);
    queryClient.invalidateQueries({ queryKey: ["admin-provider-kyc-queue"] });
    queryClient.invalidateQueries({ queryKey: ["admin-provider-photo-queue"] });
    queryClient.invalidateQueries({ queryKey: ["admin-provider-bank-account-queue"] });
  };

  const approveKycMutation = useMutation({
    mutationFn: (documentId: string) => approveKycDocument(documentId),
    onSuccess: () => onSuccess("KYC document approved."),
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

  const approvePhotoMutation = useMutation({
    mutationFn: (providerId: string) => approveProviderPhoto(providerId),
    onSuccess: () => onSuccess("Photo approved - customers can now see it."),
    onError,
  });

  const rejectPhotoMutation = useMutation({
    mutationFn: ({ providerId, reason }: { providerId: string; reason: string }) => rejectProviderPhoto(providerId, { reason }),
    onSuccess: () => {
      setPendingPhotoRejection(null);
      onSuccess("Photo rejected.");
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
      setPendingBankAccountRejection(null);
      onSuccess("Bank account details rejected.");
    },
    onError,
  });

  const documentCount = documentsQuery.data?.length ?? 0;
  const photoCount = photosQuery.data?.length ?? 0;
  const bankAccountCount = bankAccountsQuery.data?.length ?? 0;

  return (
    <div className="flex w-full max-w-5xl flex-col gap-6">
      <PageHeading
        title="Verification queue"
        subtitle="Every KYC document and profile photo still awaiting a verdict, oldest first."
        breadcrumbs={<Breadcrumbs items={[{ label: "Providers", href: "/providers/directory" }, { label: "Verification queue" }]} />}
      />
      <ProvidersTabs />

      {actionError ? <Alert tone="error">{actionError}</Alert> : null}
      {actionNotice ? <Alert tone="success">{actionNotice}</Alert> : null}

      <Tabs
        label="Verification queue sections"
        value={queueTab}
        onChange={setQueueTab}
        tabs={[
          { value: "documents", label: `Documents${documentCount > 0 ? ` (${documentCount})` : ""}` },
          { value: "photos", label: `Photos${photoCount > 0 ? ` (${photoCount})` : ""}` },
          { value: "bankAccounts", label: `Bank accounts${bankAccountCount > 0 ? ` (${bankAccountCount})` : ""}` },
        ]}
      />

      {queueTab === "documents" ? (
        <Card title="Pending KYC documents" description="Approve or reject each submission - activation is gated on at least one approved document.">
          {documentsQuery.isPending ? (
            <SkeletonText lines={4} />
          ) : documentsQuery.isError ? (
            <SectionError error={documentsQuery.error} onRetry={() => documentsQuery.refetch()} />
          ) : documentsQuery.data.length === 0 ? (
            <EmptyState title="Nothing pending" description="Every submitted KYC document has been reviewed." />
          ) : (
            <ul className="flex flex-col gap-3 text-sm">
              {documentsQuery.data.map((doc) => (
                <li key={doc.id} className="rounded-xl border border-line p-3">
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <Link
                      href={`/providers/${doc.providerId}`}
                      className="font-medium text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
                    >
                      {doc.providerDisplayName}
                    </Link>
                    <Badge tone="warning">{KYC_DOC_TYPE_LABELS[doc.docType]}</Badge>
                  </div>
                  <p className="mt-1 text-xs text-fg-subtle">
                    {doc.docNumber ? (
                      <>
                        Doc <span className="nums">#{doc.docNumber}</span> ·{" "}
                      </>
                    ) : null}
                    Submitted {formatDateTime(doc.submittedAt)}
                  </p>

                  <div className="mt-3 flex items-center gap-3 border-t border-line pt-3">
                    {isImageFileRef(doc.fileRef) ? (
                      // eslint-disable-next-line @next/next/no-img-element
                      <img
                        src={doc.fileRef}
                        alt={`${KYC_DOC_TYPE_LABELS[doc.docType]} submitted by ${doc.providerDisplayName}`}
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

                  {canWriteProvider ? (
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
                        onClick={() => setPendingKycRejection({ id: doc.id, label: `${doc.providerDisplayName} — ${KYC_DOC_TYPE_LABELS[doc.docType]}` })}
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
      ) : null}

      {queueTab === "photos" ? (
        <Card title="Pending profile photos" description="Approving is the only thing that makes a photo visible to customers.">
          {photosQuery.isPending ? (
            <SkeletonText lines={4} />
          ) : photosQuery.isError ? (
            <SectionError error={photosQuery.error} onRetry={() => photosQuery.refetch()} />
          ) : photosQuery.data.length === 0 ? (
            <EmptyState title="Nothing pending" description="Every submitted profile photo has been reviewed." />
          ) : (
            <ul className="flex flex-col gap-3 text-sm">
              {photosQuery.data.map((photo) => (
                <li key={photo.providerId} className="rounded-xl border border-line p-3">
                  <div className="flex items-center gap-4">
                    {/* eslint-disable-next-line @next/next/no-img-element */}
                    <img
                      src={photo.photoUrl ?? ""}
                      alt={`Submitted profile photo for ${photo.displayName}`}
                      className="h-16 w-16 shrink-0 rounded-full border border-line object-cover"
                    />
                    <div className="min-w-0 flex-1">
                      <Link
                        href={`/providers/${photo.providerId}`}
                        className="font-medium text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
                      >
                        {photo.displayName}
                      </Link>
                      {photo.moderatedAtUtc ? (
                        <p className="mt-1 text-xs text-fg-subtle">Reviewed {formatDateTime(photo.moderatedAtUtc)}</p>
                      ) : null}
                    </div>
                  </div>

                  {canWriteProvider ? (
                    <div className="mt-3 flex flex-col gap-3 border-t border-line pt-3 sm:flex-row sm:items-end">
                      <Button
                        variant="secondary"
                        loading={approvePhotoMutation.isPending && approvePhotoMutation.variables === photo.providerId}
                        onClick={() => approvePhotoMutation.mutate(photo.providerId)}
                      >
                        Approve
                      </Button>
                      <div className="flex-1">
                        <Field
                          label="Rejection reason"
                          value={photoRejectReasonByProvider[photo.providerId] ?? ""}
                          onChange={(e) =>
                            setPhotoRejectReasonByProvider((m) => ({ ...m, [photo.providerId]: e.target.value }))
                          }
                        />
                      </div>
                      <Button
                        variant="danger"
                        disabled={!(photoRejectReasonByProvider[photo.providerId] ?? "").trim()}
                        onClick={() => setPendingPhotoRejection({ providerId: photo.providerId, label: photo.displayName })}
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
      ) : null}

      {queueTab === "bankAccounts" ? (
        <Card
          title="Pending bank account details"
          description="Structured payout details (docs/PROVIDER.md OPEN DECISIONS #3) - not a payout gate, but an admin should verify these before trusting them for a transfer."
        >
          {bankAccountsQuery.isPending ? (
            <SkeletonText lines={4} />
          ) : bankAccountsQuery.isError ? (
            <SectionError error={bankAccountsQuery.error} onRetry={() => bankAccountsQuery.refetch()} />
          ) : bankAccountsQuery.data.length === 0 ? (
            <EmptyState title="Nothing pending" description="Every submitted bank account has been reviewed." />
          ) : (
            <ul className="flex flex-col gap-3 text-sm">
              {bankAccountsQuery.data.map((bankAccount) => (
                <li key={bankAccount.id} className="rounded-xl border border-line p-3">
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <Link
                      href={`/providers/${bankAccount.providerId}`}
                      className="font-medium text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
                    >
                      {bankAccount.providerDisplayName}
                    </Link>
                    <Badge tone="warning">{bankAccount.bankName}</Badge>
                  </div>
                  <p className="nums mt-1 text-xs text-fg-subtle">
                    {bankAccount.accountHolderName} · {bankAccount.maskedAccountNumber} · {bankAccount.ifscCode}
                  </p>
                  <p className="mt-1 text-xs text-fg-subtle">Updated {formatDateTime(bankAccount.updatedAt)}</p>

                  {canWriteProvider ? (
                    <div className="mt-3 flex flex-col gap-3 border-t border-line pt-3 sm:flex-row sm:items-end">
                      <Button
                        variant="secondary"
                        loading={approveBankAccountMutation.isPending && approveBankAccountMutation.variables === bankAccount.id}
                        onClick={() => approveBankAccountMutation.mutate(bankAccount.id)}
                      >
                        Approve
                      </Button>
                      <div className="flex-1">
                        <Field
                          label="Rejection reason"
                          value={bankAccountRejectReasonById[bankAccount.id] ?? ""}
                          onChange={(e) =>
                            setBankAccountRejectReasonById((m) => ({ ...m, [bankAccount.id]: e.target.value }))
                          }
                        />
                      </div>
                      <Button
                        variant="danger"
                        disabled={!(bankAccountRejectReasonById[bankAccount.id] ?? "").trim()}
                        onClick={() =>
                          setPendingBankAccountRejection({ id: bankAccount.id, label: bankAccount.providerDisplayName })
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
      ) : null}

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
        open={pendingPhotoRejection !== null}
        title="Reject this profile photo?"
        description="Customers keep seeing the placeholder avatar until the provider submits a new one."
        confirmLabel="Reject photo"
        cancelLabel="Keep pending"
        loading={rejectPhotoMutation.isPending}
        error={rejectPhotoMutation.isError ? describeError(rejectPhotoMutation.error) : null}
        onCancel={() => setPendingPhotoRejection(null)}
        onConfirm={() => {
          if (!pendingPhotoRejection) return;
          rejectPhotoMutation.mutate({
            providerId: pendingPhotoRejection.providerId,
            reason: (photoRejectReasonByProvider[pendingPhotoRejection.providerId] ?? "").trim(),
          });
        }}
      >
        {pendingPhotoRejection ? (
          <p className="text-sm text-fg-muted">
            {pendingPhotoRejection.label} —{" "}
            <span className="font-medium text-fg">{photoRejectReasonByProvider[pendingPhotoRejection.providerId] ?? ""}</span>
          </p>
        ) : null}
      </ConfirmDialog>

      <ConfirmDialog
        open={pendingBankAccountRejection !== null}
        title="Reject these bank account details?"
        description="The provider must resubmit before these details can be trusted for a payout."
        confirmLabel="Reject details"
        cancelLabel="Keep pending"
        loading={rejectBankAccountMutation.isPending}
        error={rejectBankAccountMutation.isError ? describeError(rejectBankAccountMutation.error) : null}
        onCancel={() => setPendingBankAccountRejection(null)}
        onConfirm={() => {
          if (!pendingBankAccountRejection) return;
          rejectBankAccountMutation.mutate({
            bankAccountId: pendingBankAccountRejection.id,
            reason: (bankAccountRejectReasonById[pendingBankAccountRejection.id] ?? "").trim(),
          });
        }}
      >
        {pendingBankAccountRejection ? (
          <p className="text-sm text-fg-muted">
            {pendingBankAccountRejection.label} —{" "}
            <span className="font-medium text-fg">{bankAccountRejectReasonById[pendingBankAccountRejection.id] ?? ""}</span>
          </p>
        ) : null}
      </ConfirmDialog>
    </div>
  );
}
