"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useState } from "react";
import { Alert, Badge, Button, Field, PageHeading, Select } from "@/components/ui";
import type { BadgeTone } from "@/components/ui";
import { useResetOnChange } from "@/hooks/useResetOnChange";
import {
  ConfirmDialog,
  DataTable,
  ExportCsvButton,
  Pagination,
  formatCurrency,
  formatDate,
} from "@/components/data-table";
import type { CsvColumn, DataTableColumn } from "@/components/data-table";
import { PaymentsTabs } from "@/components/PaymentsTabs";
import { describeError } from "@/lib/api";
import { todayIsoDate } from "@/lib/date";
import { payViaPayU, searchPayouts, updatePayoutStatus } from "@/lib/providers-api";
import { ProviderBankAccountVerificationStatus, ProviderPayoutChannel, ProviderPayoutStatus } from "@/lib/providers-types";
import type { ProviderPayout } from "@/lib/providers-types";
import { useAdminClaims } from "@/lib/use-admin-claims";

const PAGE_SIZE = 20;

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

const STATUS_OPTIONS: { value: string; label: string }[] = [
  { value: String(ProviderPayoutStatus.Pending), label: "Pending" },
  { value: String(ProviderPayoutStatus.Processing), label: "Processing" },
  { value: String(ProviderPayoutStatus.Paid), label: "Paid" },
  { value: String(ProviderPayoutStatus.Failed), label: "Failed" },
  { value: "", label: "Any status" },
];

const PAYOUT_CSV_COLUMNS: readonly CsvColumn<ProviderPayout>[] = [
  { header: "Provider", value: (payout) => payout.providerDisplayName },
  { header: "Period start", value: (payout) => payout.periodStart },
  { header: "Period end", value: (payout) => payout.periodEnd },
  { header: "Amount", value: (payout) => payout.totalAmount },
  { header: "Status", value: (payout) => PAYOUT_STATUS_LABELS[payout.status] },
  { header: "Reference", value: (payout) => payout.payoutReference ?? "" },
  { header: "Created", value: (payout) => payout.createdAt },
];

/**
 * Cross-provider payout queue (Payment Management UX pass: the same gap
 * shape the KYC verification queue used to have on the Provider side - a
 * real, fully-built backend capability with no way to reach it except one
 * provider at a time). `PayoutsController.Search` always supported an
 * optional `providerId`; this is simply the first caller that omits it.
 *
 * Defaults to the Pending bucket - the one a finance admin actually needs
 * to work through day to day - rather than "Any status", which would open
 * on a mix of already-settled history.
 *
 * Reuses the exact status-advance actions (Mark processing / Mark paid with
 * a bank reference / Mark failed) the provider detail page's own Payouts
 * card already has - this is the same admin action from a cross-provider
 * queue, not a second implementation of it. Running a NEW payout batch
 * still happens from that provider's own Earnings tab, where the earning
 * ledger it is computed from already lives.
 */
export default function PayoutsQueuePage() {
  const claims = useAdminClaims();
  const canWrite = claims?.permissions.includes("payout.write") ?? false;
  const queryClient = useQueryClient();

  const [status, setStatus] = useState<string>(String(ProviderPayoutStatus.Pending));
  const [page, setPage] = useState(1);
  const [payoutReferenceByPayout, setPayoutReferenceByPayout] = useState<Record<string, string>>({});
  const [pendingFailure, setPendingFailure] = useState<ProviderPayout | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [actionNotice, setActionNotice] = useState<string | null>(null);

  useResetOnChange([status], () => setPage(1));

  const query = useQuery({
    queryKey: ["admin-payouts-queue", status, page] as const,
    queryFn: () =>
      searchPayouts({
        status: status === "" ? undefined : (Number(status) as ProviderPayoutStatus),
        page,
        pageSize: PAGE_SIZE,
      }),
    placeholderData: keepPreviousData,
  });

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["admin-payouts-queue"] });

  const statusMutation = useMutation({
    mutationFn: ({ payoutId, newStatus, payoutReference }: { payoutId: string; newStatus: ProviderPayoutStatus; payoutReference?: string }) =>
      updatePayoutStatus(payoutId, { status: newStatus, payoutReference }),
    onSuccess: () => {
      setPendingFailure(null);
      setActionError(null);
      setActionNotice("Payout status updated.");
      invalidate();
    },
    onError: (err) => setActionError(describeError(err)),
  });

  const payViaPayUMutation = useMutation({
    mutationFn: (payoutId: string) => payViaPayU(payoutId),
    onSuccess: () => {
      setActionError(null);
      setActionNotice("PayU transfer initiated - the payout will settle once PayU's webhook confirms it.");
      invalidate();
    },
    onError: (err) => setActionError(describeError(err)),
  });

  const columns: DataTableColumn<ProviderPayout>[] = [
    {
      key: "provider",
      header: "Provider",
      cell: (payout) => (
        <Link
          href={`/providers/${payout.providerId}`}
          className="font-medium text-fg underline-offset-4 hover:text-brand-600 hover:underline dark:hover:text-brand-400"
        >
          {payout.providerDisplayName}
        </Link>
      ),
    },
    {
      key: "period",
      header: "Period",
      cell: (payout) => (
        <span className="nums">
          {formatDate(payout.periodStart)} → {formatDate(payout.periodEnd)}
        </span>
      ),
    },
    {
      key: "amount",
      header: "Amount",
      numeric: true,
      cell: (payout) => <span className="nums font-medium text-fg">{formatCurrency(payout.totalAmount)}</span>,
    },
    {
      key: "status",
      header: "Status",
      cell: (payout) => (
        <>
          <Badge tone={PAYOUT_STATUS_TONES[payout.status]}>{PAYOUT_STATUS_LABELS[payout.status]}</Badge>
          {payout.status !== ProviderPayoutStatus.Pending ? (
            <div className="mt-1 text-xs text-fg-subtle">Via: {PROCESSED_VIA_LABELS[payout.processedVia]}</div>
          ) : null}
          {payout.payoutReference ? <div className="mt-1 text-xs text-fg-subtle">Ref: {payout.payoutReference}</div> : null}
        </>
      ),
    },
    {
      key: "bankAccount",
      header: "Bank account",
      cell: (payout) =>
        payout.bankAccount ? (
          <div className="flex flex-col gap-1">
            <span className="nums text-xs text-fg-muted">
              {payout.bankAccount.accountHolderName} · {payout.bankAccount.accountNumber}
            </span>
            <span className="nums text-xs text-fg-subtle">
              {payout.bankAccount.ifscCode} · {payout.bankAccount.bankName}
            </span>
            <Badge tone={BANK_ACCOUNT_STATUS_TONES[payout.bankAccount.verificationStatus]}>
              {BANK_ACCOUNT_STATUS_LABELS[payout.bankAccount.verificationStatus]}
            </Badge>
          </div>
        ) : (
          <span className="text-xs text-danger">Not on file</span>
        ),
    },
    {
      key: "created",
      header: "Created",
      cell: (payout) => <span className="nums">{formatDate(payout.createdAt)}</span>,
    },
    {
      key: "actions",
      header: "",
      cell: (payout) => {
        if (!canWrite) {
          return null;
        }

        if (payout.status === ProviderPayoutStatus.Pending) {
          return (
            <div className="flex flex-col items-start gap-2">
              <Button
                size="sm"
                variant="secondary"
                loading={statusMutation.isPending && statusMutation.variables?.payoutId === payout.id}
                onClick={() => statusMutation.mutate({ payoutId: payout.id, newStatus: ProviderPayoutStatus.Processing })}
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
            </div>
          );
        }

        if (payout.status === ProviderPayoutStatus.Processing) {
          return (
            <div className="flex flex-col gap-2">
              <Field
                label="Bank reference"
                value={payoutReferenceByPayout[payout.id] ?? ""}
                onChange={(e) => setPayoutReferenceByPayout((m) => ({ ...m, [payout.id]: e.target.value }))}
              />
              <div className="flex gap-2">
                <Button
                  size="sm"
                  disabled={!(payoutReferenceByPayout[payout.id] ?? "").trim()}
                  loading={
                    statusMutation.isPending &&
                    statusMutation.variables?.payoutId === payout.id &&
                    statusMutation.variables?.newStatus === ProviderPayoutStatus.Paid
                  }
                  onClick={() =>
                    statusMutation.mutate({
                      payoutId: payout.id,
                      newStatus: ProviderPayoutStatus.Paid,
                      payoutReference: (payoutReferenceByPayout[payout.id] ?? "").trim(),
                    })
                  }
                >
                  Mark paid
                </Button>
                <Button size="sm" variant="danger" onClick={() => setPendingFailure(payout)}>
                  Mark failed
                </Button>
              </div>
            </div>
          );
        }

        return <span className="text-fg-subtle">—</span>;
      },
    },
  ];

  return (
    <div className="w-full max-w-7xl">
      <PageHeading
        title="Payouts"
        subtitle="Every provider payout batch across the platform, not just one provider at a time."
      />
      <PaymentsTabs />

      {actionError ? (
        <div className="mt-4">
          <Alert tone="error">{actionError}</Alert>
        </div>
      ) : null}
      {actionNotice ? (
        <div className="mt-4">
          <Alert tone="success">{actionNotice}</Alert>
        </div>
      ) : null}
      {!canWrite ? (
        <div className="mt-4">
          <Alert tone="info">
            You can review payouts but not advance their status - that needs the &quot;payout.write&quot; permission.
          </Alert>
        </div>
      ) : null}

      <div className="mt-4 max-w-xs">
        <Select label="Status" value={status} onChange={(e) => setStatus(e.target.value)} options={STATUS_OPTIONS} />
      </div>

      <div className="mt-4">
        <DataTable
          title="Payout batches"
          actions={
            <ExportCsvButton
              rows={query.data?.items}
              columns={PAYOUT_CSV_COLUMNS}
              fileName={`payouts-export-${todayIsoDate()}.csv`}
            />
          }
          columns={columns}
          rows={query.data?.items}
          rowKey={(payout) => payout.id}
          isLoading={query.isPending}
          isFetching={query.isFetching}
          error={query.error}
          onRetry={() => query.refetch()}
          skeletonRows={8}
          minWidth="1180px"
          caption="Payout batches matching the current filter"
          emptyTitle="Nothing here"
          emptyDescription="No payout batches match this status. Batches are created from a provider's own Earnings tab."
          footer={
            query.data ? (
              <Pagination
                page={page}
                pageSize={PAGE_SIZE}
                totalCount={query.data.totalCount}
                onPageChange={setPage}
                busy={query.isFetching}
                itemLabel="payout"
              />
            ) : null
          }
        />
      </div>

      <ConfirmDialog
        open={pendingFailure !== null}
        title="Mark this payout failed?"
        description="The batch is closed as failed and the amount stays owed to the provider."
        confirmLabel="Mark failed"
        cancelLabel="Keep processing"
        loading={statusMutation.isPending}
        error={statusMutation.isError ? describeError(statusMutation.error) : null}
        onCancel={() => setPendingFailure(null)}
        onConfirm={() => {
          if (!pendingFailure) return;
          statusMutation.mutate({ payoutId: pendingFailure.id, newStatus: ProviderPayoutStatus.Failed });
        }}
      >
        {pendingFailure ? (
          <p className="text-sm text-fg-muted">
            {pendingFailure.providerDisplayName} — {formatCurrency(pendingFailure.totalAmount)}
          </p>
        ) : null}
      </ConfirmDialog>
    </div>
  );
}
