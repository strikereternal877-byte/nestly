"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useState } from "react";
import { Alert, Badge, Button, Card, EmptyState, Field, PageHeading, Select, Tabs } from "@/components/ui";
import {
  Breadcrumbs,
  ConfirmDialog,
  DescriptionList,
  FormActions,
  formatCurrency,
  formatDate,
  formatDateTime,
  RecordMetaRow,
} from "@/components/data-table";
import { DetailError, DetailSkeleton } from "@/components/screen-states";
import { BookingStatusBadge, CustomerStatusBadge, TicketStatusBadge } from "@/components/status-badges";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import { useAdminClaims } from "@/lib/use-admin-claims";
import { BookingStatus, CustomerStatus, SupportTicketStatus, WALLET_SOURCE_LABELS, WalletEntryType } from "@/lib/types";
import type { CustomerDetail, CustomerNote } from "@/lib/types";

const BOOKING_STATUS_LABELS: Record<BookingStatus, string> = {
  [BookingStatus.Initiated]: "Booking Started",
  [BookingStatus.PaymentPending]: "Awaiting Payment",
  [BookingStatus.PaymentFailed]: "Payment Failed",
  [BookingStatus.Confirmed]: "Confirmed",
  [BookingStatus.AwaitingFulfilment]: "Preparing Service",
  [BookingStatus.Assigned]: "Professional Assigned",
  [BookingStatus.ProviderEnRoute]: "Professional On the Way",
  [BookingStatus.ProviderArrived]: "Professional Arrived",
  [BookingStatus.InProgress]: "In Progress",
  [BookingStatus.Completed]: "Completed",
  [BookingStatus.CancelledByCustomer]: "Cancelled by Customer",
  [BookingStatus.CancelledByAdmin]: "Cancelled by Admin",
  [BookingStatus.Rescheduled]: "Rescheduled",
  [BookingStatus.RefundPending]: "Refund in Progress",
  [BookingStatus.Refunded]: "Refunded",
  [BookingStatus.Expired]: "Expired",
};

/** Same amber rating convention as the reviews moderation screen's StarRating. */
function StarRating({ rating }: { rating: number }) {
  return (
    <span aria-label={`${rating} out of 5 stars`} className="nums text-accent-500">
      {"★".repeat(rating)}
      <span className="text-fg-subtle">{"★".repeat(Math.max(0, 5 - rating))}</span>
    </span>
  );
}

const SUPPORT_STATUS_LABELS: Record<SupportTicketStatus, string> = {
  [SupportTicketStatus.Open]: "Open",
  [SupportTicketStatus.InProgress]: "In Progress",
  [SupportTicketStatus.WaitingForCustomer]: "Waiting for Customer",
  [SupportTicketStatus.Escalated]: "Escalated",
  [SupportTicketStatus.Resolved]: "Resolved",
  [SupportTicketStatus.Closed]: "Closed",
};

/**
 * Customer 360 view (SRS 12.4.2, task 102): profile, addresses, bookings,
 * wallet, coupons, support tickets and notes, plus block/unblock and
 * add-note actions (SRS 12.4.3, tasks 101c-101d). Mutating actions are only
 * shown to admins holding "customers.write" - the API enforces this
 * server-side regardless, this is purely to avoid showing controls that
 * would just 403.
 *
 * Blocking goes through `ConfirmDialog` (task 222): it locks the customer out
 * of the app entirely and was previously a single unconfirmed click.
 */
export default function CustomerDetailPage() {
  const params = useParams<{ customerId: string }>();
  const customerId = params.customerId;
  const claims = useAdminClaims();
  const canWrite = claims?.permissions.includes("customers.write") ?? false;
  const queryClient = useQueryClient();

  const detailQuery = useQuery({
    queryKey: ["admin-customer-detail", customerId],
    queryFn: () => apiFetch<CustomerDetail>(`${API_V1}/customers/${customerId}`, { authenticated: true }),
  });

  const [blockReason, setBlockReason] = useState("");
  const [confirmBlock, setConfirmBlock] = useState(false);
  const [noteText, setNoteText] = useState("");
  const [actionError, setActionError] = useState<string | null>(null);
  const [actionNotice, setActionNotice] = useState<string | null>(null);

  // Right-to-erasure delete (SRS 12.4.3 gap: the backend endpoint existed
  // with no UI entry point at all). Two gates before the trigger even
  // enables, not just one like Block - this is terminal and irreversible,
  // unlike Block/Unblock: a reason for the audit trail, and typing the
  // customer's exact name so a stray click on the wrong row can't delete it.
  const [deleteReason, setDeleteReason] = useState("");
  const [deleteConfirmName, setDeleteConfirmName] = useState("");
  const [confirmDelete, setConfirmDelete] = useState(false);

  const [walletDirection, setWalletDirection] = useState<WalletEntryType>(WalletEntryType.Credit);
  const [walletAmount, setWalletAmount] = useState("");
  const [walletReason, setWalletReason] = useState("");

  // Enterprise redesign pass (docs/OPEN-FIXES-FEATURES.csv, admin-web
  // information-density) - same tabbed grouping as the booking/provider
  // detail pages: 8 always-stacked cards behind 4 tabs instead.
  const [detailTab, setDetailTab] = useState<"overview" | "activity" | "wallet" | "support">("overview");

  const invalidateDetail = () =>
    queryClient.invalidateQueries({ queryKey: ["admin-customer-detail", customerId] });

  const blockMutation = useMutation({
    mutationFn: (reason: string) =>
      apiFetch<CustomerDetail>(`${API_V1}/customers/${customerId}/block`, {
        method: "POST",
        authenticated: true,
        body: JSON.stringify({ reason }),
      }),
    onSuccess: () => {
      setBlockReason("");
      setConfirmBlock(false);
      setActionError(null);
      setActionNotice("Customer blocked.");
      invalidateDetail();
    },
    onError: (err) => setActionError(describeError(err)),
  });

  const unblockMutation = useMutation({
    mutationFn: () =>
      apiFetch<CustomerDetail>(`${API_V1}/customers/${customerId}/unblock`, {
        method: "POST",
        authenticated: true,
      }),
    onSuccess: () => {
      setActionError(null);
      setActionNotice("Customer unblocked.");
      invalidateDetail();
    },
    onError: (err) => setActionError(describeError(err)),
  });

  const deleteMutation = useMutation({
    mutationFn: (reason: string) =>
      apiFetch<CustomerDetail>(`${API_V1}/customers/${customerId}/delete`, {
        method: "POST",
        authenticated: true,
        body: JSON.stringify({ reason }),
      }),
    onSuccess: () => {
      setDeleteReason("");
      setDeleteConfirmName("");
      setConfirmDelete(false);
      setActionError(null);
      setActionNotice("Customer account deleted.");
      invalidateDetail();
    },
    onError: (err) => setActionError(describeError(err)),
  });

  const adjustWalletMutation = useMutation({
    mutationFn: () =>
      apiFetch<CustomerDetail>(`${API_V1}/customers/${customerId}/wallet/adjust`, {
        method: "POST",
        authenticated: true,
        body: JSON.stringify({ direction: walletDirection, amount: Number(walletAmount), reason: walletReason.trim() }),
      }),
    onSuccess: () => {
      setWalletAmount("");
      setWalletReason("");
      setActionError(null);
      setActionNotice("Wallet adjusted.");
      invalidateDetail();
    },
    onError: (err) => setActionError(describeError(err)),
  });

  const addNoteMutation = useMutation({
    mutationFn: (note: string) =>
      apiFetch<CustomerNote>(`${API_V1}/customers/${customerId}/notes`, {
        method: "POST",
        authenticated: true,
        body: JSON.stringify({ note }),
      }),
    onSuccess: () => {
      setNoteText("");
      setActionError(null);
      setActionNotice("Note added.");
      invalidateDetail();
    },
    onError: (err) => setActionError(describeError(err)),
  });

  const breadcrumbs = [
    { label: "Customers", href: "/customers/directory" },
    { label: detailQuery.data?.name ?? "Customer" },
  ];

  if (detailQuery.isPending) {
    return <DetailSkeleton cards={4} className="flex w-full max-w-7xl flex-col gap-6" />;
  }

  if (detailQuery.isError) {
    return (
      <DetailError
        title="Customer"
        breadcrumbs={breadcrumbs}
        error={detailQuery.error}
        onRetry={() => detailQuery.refetch()}
        className="w-full max-w-7xl"
      />
    );
  }

  const customer = detailQuery.data;

  return (
    <div className="flex w-full max-w-7xl flex-col gap-6">
      <PageHeading
        title={customer.name}
        subtitle={`${customer.mobile}${customer.email ? ` · ${customer.email}` : ""}`}
        breadcrumbs={<Breadcrumbs items={breadcrumbs} />}
        actions={<CustomerStatusBadge status={customer.status} />}
      />

      {actionError ? <Alert tone="error">{actionError}</Alert> : null}
      {actionNotice ? <Alert tone="success">{actionNotice}</Alert> : null}

      <RecordMetaRow
        className="border-b border-line pb-5"
        items={[
          { label: "Registered", value: <span className="nums">{formatDate(customer.createdAtUtc)}</span> },
          { label: "Bookings", value: <span className="nums">{customer.bookings.length}</span> },
          {
            label: "Wallet balance",
            value: <span className="nums font-semibold">{formatCurrency(customer.walletBalance)}</span>,
          },
        ]}
      />

      <Link
        href={`/reviews?customerId=${customer.id}`}
        className="-mt-2 text-sm font-medium text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
      >
        View reviews written by this customer →
      </Link>

      <Tabs
        label="Customer sections"
        value={detailTab}
        onChange={setDetailTab}
        tabs={[
          { value: "overview", label: "Overview" },
          { value: "activity", label: "Activity" },
          { value: "wallet", label: "Wallet & coupons" },
          { value: "support", label: "Support & notes" },
        ]}
      />

      {detailTab === "overview" ? (
      <div className="flex flex-col gap-6">
      <Card title="Profile" description="Account snapshot (SRS 12.4.2).">
        <DescriptionList
          columns={3}
          items={[
            { label: "City", value: customer.city ?? "—" },
            { label: "State", value: customer.state ?? "—" },
            { label: "Pincode", value: <span className="nums">{customer.pincode ?? "—"}</span> },
          ]}
        />

        {canWrite ? (
          <div className="mt-5 border-t border-line pt-5">
            {customer.status === CustomerStatus.Blocked ? (
              <FormActions align="start">
                <Button
                  variant="secondary"
                  loading={unblockMutation.isPending}
                  onClick={() => unblockMutation.mutate()}
                >
                  Unblock customer
                </Button>
              </FormActions>
            ) : (
              <div className="flex flex-col gap-3 sm:flex-row sm:items-end">
                <div className="flex-1">
                  <Field
                    label="Block reason"
                    required
                    value={blockReason}
                    onChange={(e) => setBlockReason(e.target.value)}
                    placeholder="Reason for blocking this account"
                    hint="Recorded to the audit trail."
                  />
                </div>
                <Button variant="danger" disabled={!blockReason.trim()} onClick={() => setConfirmBlock(true)}>
                  Block customer
                </Button>
              </div>
            )}
          </div>
        ) : null}

        {canWrite && customer.status !== CustomerStatus.SoftDeleted ? (
          <div className="mt-5 flex flex-col gap-3 border-t border-line pt-5">
            <p className="text-xs font-semibold uppercase tracking-wide text-danger">Danger zone</p>
            <Field
              label="Deletion reason"
              required
              value={deleteReason}
              onChange={(e) => setDeleteReason(e.target.value)}
              placeholder="Reason this account is being deleted (e.g. a right-to-erasure request)"
              hint="Recorded to the audit trail."
            />
            <div className="flex flex-col gap-3 sm:flex-row sm:items-end">
              <div className="flex-1">
                <Field
                  label={`Type "${customer.name}" to confirm`}
                  value={deleteConfirmName}
                  onChange={(e) => setDeleteConfirmName(e.target.value)}
                  placeholder={customer.name}
                  hint="Permanent - anonymizes this account and signs it out everywhere. There is no undelete."
                />
              </div>
              <Button
                variant="danger"
                disabled={!deleteReason.trim() || deleteConfirmName.trim() !== customer.name}
                onClick={() => setConfirmDelete(true)}
              >
                Delete customer
              </Button>
            </div>
          </div>
        ) : null}
      </Card>

      <Card
        title="Provider ratings"
        description="Private feedback providers have left about this customer - never shown to the customer."
      >
        {customer.providerRatings.recent.length === 0 ? (
          <EmptyState title="No ratings yet" description="Providers rate customers after completing a job." />
        ) : (
          <>
            <div className="mb-4 flex items-center gap-3">
              <StarRating rating={Math.round(customer.providerRatings.averageRating ?? 0)} />
              <span className="nums text-sm font-medium text-fg">
                {customer.providerRatings.averageRating?.toFixed(1)} average
              </span>
              <span className="nums text-sm text-fg-subtle">
                ({customer.providerRatings.ratingCount} rating{customer.providerRatings.ratingCount === 1 ? "" : "s"})
              </span>
            </div>
            <ul className="flex flex-col gap-2 text-sm">
              {customer.providerRatings.recent.map((rating) => (
                <li key={rating.id} className="rounded-xl border border-line p-3">
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <StarRating rating={rating.rating} />
                    <span className="nums text-xs text-fg-subtle">{formatDate(rating.createdAtUtc)}</span>
                  </div>
                  <p className="mt-1 text-xs text-fg-subtle">by {rating.providerDisplayName}</p>
                  {rating.note ? <p className="mt-1.5 text-fg">{rating.note}</p> : null}
                </li>
              ))}
            </ul>
          </>
        )}
      </Card>

      <Card title="Addresses" description="SRS 12.4.2">
        {customer.addresses.length === 0 ? (
          <EmptyState title="No saved addresses" description="This customer has not saved a service address yet." />
        ) : (
          <ul className="flex flex-col gap-2 text-sm">
            {customer.addresses.map((address) => (
              <li key={address.id} className="rounded-xl border border-line p-3">
                <p className="flex flex-wrap items-center gap-2 font-medium text-fg">
                  {address.label}
                  {address.isDefault ? <Badge tone="brand">Default</Badge> : null}
                </p>
                <p className="mt-1 text-fg-muted">
                  {[address.line1, address.line2, address.landmark, address.city, address.state, address.pincode]
                    .filter(Boolean)
                    .join(", ")}
                </p>
              </li>
            ))}
          </ul>
        )}
      </Card>
      </div>
      ) : null}

      {detailTab === "activity" ? (
      <Card title="Booking history" description="SRS 12.4.2">
        {customer.bookings.length === 0 ? (
          <EmptyState title="No bookings yet" description="This customer has not completed a booking." />
        ) : (
          <ul className="flex flex-col gap-2 text-sm">
            {customer.bookings.map((booking) => (
              <li key={booking.id}>
                <Link
                  href={`/bookings/${booking.id}`}
                  className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-line p-3 hover:border-line-strong hover:bg-surface-2"
                >
                  <span className="nums text-fg">{booking.slotDate}</span>
                  <BookingStatusBadge status={booking.status} label={BOOKING_STATUS_LABELS[booking.status]} />
                  <span className="nums font-medium text-fg">{formatCurrency(booking.totalPayableSnapshot)}</span>
                </Link>
              </li>
            ))}
          </ul>
        )}
      </Card>
      ) : null}

      {detailTab === "wallet" ? (
      <div className="flex flex-col gap-6">
      <Card
        title="Wallet history"
        description={`Current balance: ${formatCurrency(customer.walletBalance)}`}
      >
        {canWrite ? (
          <div className="mb-5 flex flex-col gap-3 border-b border-line pb-5 sm:flex-row sm:items-end">
            <Select
              label="Direction"
              value={String(walletDirection)}
              onChange={(e) => setWalletDirection(Number(e.target.value) as WalletEntryType)}
              options={[
                { value: String(WalletEntryType.Credit), label: "Credit (add funds)" },
                { value: String(WalletEntryType.Debit), label: "Debit (remove funds)" },
              ]}
              className="sm:w-48"
            />
            <Field
              label="Amount"
              type="number"
              min={0.01}
              step="0.01"
              value={walletAmount}
              onChange={(e) => setWalletAmount(e.target.value)}
              className="sm:w-32"
            />
            <div className="flex-1">
              <Field
                label="Reason"
                value={walletReason}
                onChange={(e) => setWalletReason(e.target.value)}
                placeholder="Why this adjustment is being made"
                hint="Recorded to the audit trail. Use for a goodwill credit or a correction - not for refunds, coupons or referral rewards, which post here automatically."
              />
            </div>
            <Button
              disabled={!walletReason.trim() || !walletAmount || Number(walletAmount) <= 0}
              loading={adjustWalletMutation.isPending}
              onClick={() => adjustWalletMutation.mutate()}
            >
              Apply
            </Button>
          </div>
        ) : null}

        {customer.walletEntries.length === 0 ? (
          <EmptyState title="No wallet activity" description="Top-ups, refunds, credits and what the wallet paid for will appear here." />
        ) : (
          <ul className="flex flex-col gap-2 text-sm">
            {customer.walletEntries.map((entry) => (
              <li
                key={entry.id}
                className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-line p-3"
              >
                <span className="min-w-0 flex-1 text-fg">
                  {entry.description}
                  <span className="mt-0.5 block text-xs text-fg-subtle">
                    {WALLET_SOURCE_LABELS[entry.sourceType] ?? "Other"}
                  </span>
                </span>
                <span className="nums text-xs text-fg-subtle">{formatDateTime(entry.createdAtUtc)}</span>
                <span
                  className={
                    entry.entryType === WalletEntryType.Credit
                      ? "nums font-medium text-success"
                      : "nums font-medium text-danger"
                  }
                >
                  {entry.entryType === WalletEntryType.Credit ? "+" : "−"}
                  {formatCurrency(entry.amount)}
                </span>
                <span className="nums w-32 text-right text-xs text-fg-subtle">Balance {formatCurrency(entry.balanceAfter)}</span>
              </li>
            ))}
          </ul>
        )}
      </Card>

      <Card title="Coupons used" description="SRS 12.4.2">
        {customer.coupons.length === 0 ? (
          <EmptyState title="No coupons redeemed" description="Discounts this customer has claimed appear here." />
        ) : (
          <ul className="flex flex-col gap-2 text-sm">
            {customer.coupons.map((coupon, index) => (
              <li
                key={`${coupon.couponId}-${coupon.bookingId}-${index}`}
                className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-line p-3"
              >
                <span className="font-mono text-fg">{coupon.couponCode}</span>
                <span className="nums text-xs text-fg-subtle">{formatDate(coupon.redeemedAtUtc)}</span>
                <span className="nums font-medium text-fg">−{formatCurrency(coupon.discountAmount)}</span>
              </li>
            ))}
          </ul>
        )}
      </Card>
      </div>
      ) : null}

      {detailTab === "support" ? (
      <div className="flex flex-col gap-6">
      <Card title="Support tickets" description="SRS 12.4.2">
        {customer.supportTickets.length === 0 ? (
          <EmptyState title="No support tickets" description="This customer has never raised a ticket." />
        ) : (
          <ul className="flex flex-col gap-2 text-sm">
            {customer.supportTickets.map((ticket) => (
              <li key={ticket.id}>
                <Link
                  href={`/support/${ticket.id}`}
                  className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-line p-3 hover:border-line-strong hover:bg-surface-2"
                >
                  <span className="min-w-0 flex-1 text-fg">{ticket.subject}</span>
                  <TicketStatusBadge status={ticket.status} label={SUPPORT_STATUS_LABELS[ticket.status]} />
                </Link>
              </li>
            ))}
          </ul>
        )}
      </Card>

      <Card title="Internal notes" description="SRS 12.4.2, 12.4.3">
        {canWrite ? (
          <div className="mb-5 flex flex-col gap-3 sm:flex-row sm:items-end">
            <div className="flex-1">
              <Field
                label="Add a note"
                value={noteText}
                onChange={(e) => setNoteText(e.target.value)}
                hint="Visible to admins only, and attributed to you."
              />
            </div>
            <Button
              disabled={!noteText.trim()}
              loading={addNoteMutation.isPending}
              onClick={() => addNoteMutation.mutate(noteText.trim())}
            >
              Add note
            </Button>
          </div>
        ) : null}

        {customer.notes.length === 0 ? (
          <EmptyState
            title="No notes yet"
            description={
              canWrite
                ? "Record context here so the next admin picking up this account has it."
                : "An admin with customer write access can add one."
            }
          />
        ) : (
          <ul className="flex flex-col gap-2 text-sm">
            {customer.notes.map((note) => (
              <li key={note.id} className="rounded-xl border border-line p-3">
                <p className="text-fg">{note.note}</p>
                <p className="mt-1 text-xs text-fg-subtle">{formatDateTime(note.createdAtUtc)}</p>
              </li>
            ))}
          </ul>
        )}
      </Card>
      </div>
      ) : null}

      <ConfirmDialog
        open={confirmBlock}
        title="Block this customer?"
        description="They are signed out and cannot book, sign in or raise a ticket until unblocked."
        confirmLabel="Block customer"
        cancelLabel="Keep active"
        loading={blockMutation.isPending}
        error={blockMutation.isError ? describeError(blockMutation.error) : null}
        onCancel={() => setConfirmBlock(false)}
        onConfirm={() => blockMutation.mutate(blockReason.trim())}
      >
        <p className="text-sm text-fg-muted">
          Reason: <span className="font-medium text-fg">{blockReason}</span>
        </p>
      </ConfirmDialog>

      <ConfirmDialog
        open={confirmDelete}
        title="Delete this customer account?"
        description="This is permanent - the account is anonymized and signed out everywhere. There is no undelete."
        confirmLabel="Delete customer"
        cancelLabel="Keep account"
        loading={deleteMutation.isPending}
        error={deleteMutation.isError ? describeError(deleteMutation.error) : null}
        onCancel={() => setConfirmDelete(false)}
        onConfirm={() => deleteMutation.mutate(deleteReason.trim())}
      >
        <p className="text-sm text-fg-muted">
          Reason: <span className="font-medium text-fg">{deleteReason}</span>
        </p>
      </ConfirmDialog>
    </div>
  );
}
