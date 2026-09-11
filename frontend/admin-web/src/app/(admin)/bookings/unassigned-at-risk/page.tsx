"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useEffect, useState } from "react";
import { Alert, Badge, Button, Modal, PageHeading, Select } from "@/components/ui";
import type { BadgeTone } from "@/components/ui";
import { DataTable, ExportCsvButton, Pagination } from "@/components/data-table";
import type { CsvColumn, DataTableColumn } from "@/components/data-table";
import { BookingStatusBadge } from "@/components/status-badges";
import { BookingsTabs } from "@/components/BookingsTabs";
import { describeError } from "@/lib/api";
import { getUnassignedAtRiskBookings } from "@/lib/bookings-api";
import type { AdminUnassignedAtRiskBooking } from "@/lib/bookings-types";
import { todayIsoDate } from "@/lib/date";
import { assignProviderToBooking, getEligibleProviders } from "@/lib/providers-api";
import type { EligibleProvider } from "@/lib/providers-types";
import { useAdminClaims } from "@/lib/use-admin-claims";

const QUEUE_CSV_COLUMNS: readonly CsvColumn<AdminUnassignedAtRiskBooking>[] = [
  { header: "Booking #", value: (booking) => booking.reference },
  { header: "Customer", value: (booking) => booking.customerName },
  { header: "Service", value: (booking) => booking.serviceName },
  { header: "Slot date", value: (booking) => booking.slotDate },
  { header: "Slot time", value: (booking) => booking.slotStartTime },
  { header: "City", value: (booking) => booking.city },
  { header: "Pincode", value: (booking) => booking.pincode },
  { header: "Status", value: (booking) => booking.statusLabel },
];

const PAGE_SIZE = 20;

/**
 * Refreshed periodically so "in 2h" doesn't quietly drift stale while an ops
 * user leaves this screen open - cheap because it only re-derives labels
 * from data already in cache, not a refetch.
 */
const URGENCY_TICK_MS = 60_000;

/** Under this many minutes to the slot, the row is red - the "about to be missed" end of the queue. */
const URGENT_THRESHOLD_MINUTES = 180;
/** Under this many minutes (and at or past {@link URGENT_THRESHOLD_MINUTES}), the row is amber. */
const WARNING_THRESHOLD_MINUTES = 24 * 60;

/**
 * Docs/OPEN-FIXES-FEATURES.csv "Provider performance": "expose the key
 * metrics inline in the assignment picker" - appended to the native
 * `<Select>` option text below, since a plain HTML `<option>` cannot render
 * a badge. Both null (no data yet) and a real value are handled the same
 * way the pincode/service flags above are: shown only when there is
 * something to show.
 */
function formatPerformanceSuffix(candidate: EligibleProvider): string {
  const parts: string[] = [];
  if (candidate.acceptanceRatePercent !== null) parts.push(`${candidate.acceptanceRatePercent}% accept`);
  if (candidate.averageRating !== null) parts.push(`★${candidate.averageRating}`);
  return parts.length > 0 ? ` · ${parts.join(" · ")}` : "";
}

function formatSlotDate(dateOnly: string): string {
  // dateOnly is a .NET DateOnly ("yyyy-MM-dd"), business-local - displayed as
  // a plain calendar date rather than reinterpreted through the browser's
  // own timezone, the same convention the booking detail page's reschedule
  // history uses for fromSlotDate/toSlotDate.
  const parsed = new Date(`${dateOnly}T00:00:00`);
  return Number.isNaN(parsed.getTime())
    ? dateOnly
    : parsed.toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" });
}

/** .NET TimeSpan serialises as "hh:mm:ss" - trimmed to "hh:mm" for display. */
function formatSlotTime(time: string): string {
  return time.slice(0, 5);
}

function formatDuration(totalMinutes: number): string {
  if (totalMinutes < 60) return `${totalMinutes}m`;
  const totalHours = Math.round(totalMinutes / 60);
  if (totalHours < 24) return `${totalHours}h`;
  return `${Math.round(totalHours / 24)}d`;
}

/**
 * "In 2h" / "overdue by 40m", plus a badge tone that reddens as the slot
 * approaches - the "sorted by how close the slot is" requirement made
 * visible, not just implied by row order. `slotDate`/`slotStartTime` are
 * combined as a browser-local instant: admin-web makes the same assumption
 * everywhere else a raw slot date/time is shown (see ReschedulePicker) that
 * ops runs in the same timezone as the business.
 */
function slotUrgency(slotDate: string, slotStartTime: string, now: Date): { label: string; tone: BadgeTone } {
  const target = new Date(`${slotDate}T${slotStartTime}`);
  if (Number.isNaN(target.getTime())) {
    return { label: "—", tone: "neutral" };
  }

  const diffMinutes = Math.round((target.getTime() - now.getTime()) / 60_000);

  if (diffMinutes <= 0) {
    return { label: `overdue by ${formatDuration(-diffMinutes)}`, tone: "danger" };
  }
  if (diffMinutes < URGENT_THRESHOLD_MINUTES) {
    return { label: `in ${formatDuration(diffMinutes)}`, tone: "danger" };
  }
  if (diffMinutes < WARNING_THRESHOLD_MINUTES) {
    return { label: `in ${formatDuration(diffMinutes)}`, tone: "warning" };
  }
  return { label: `in ${formatDuration(diffMinutes)}`, tone: "neutral" };
}

/**
 * Row "Unassigned and at-risk queue", docs/OPEN-FIXES-FEATURES.csv: paid
 * bookings (Confirmed/Preparing Service/Professional Assigned) with no live
 * provider on them yet, soonest slot first - the gap auto-assignment (which
 * only runs as the slot approaches) and admin's own manual search left
 * invisible until now. Sort/filter and the "no live provider" definition
 * live entirely server-side (`BookingRepository.ListUnassignedAtRiskAsync`);
 * this page only renders the page it's given and re-labels urgency locally
 * so the countdown stays live between refetches.
 *
 * Assignment reuses the exact `assignProviderToBooking`/`getEligibleProviders`
 * calls the booking detail page's "Provider assignment" panel uses, in a
 * `Modal` rather than a full navigate-to-detail round trip - the booking
 * detail panel itself is not a standalone component (it's woven through that
 * page's own history/KYC/double-booking state), so lifting the two API calls
 * into a focused modal here was the simpler reuse than extracting it.
 */
export default function UnassignedAtRiskQueuePage() {
  const claims = useAdminClaims();
  const canWrite = claims?.permissions.includes("bookings.write") ?? false;
  const queryClient = useQueryClient();

  const [page, setPage] = useState(1);
  const [now, setNow] = useState(() => new Date());
  const [assigningBooking, setAssigningBooking] = useState<AdminUnassignedAtRiskBooking | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  useEffect(() => {
    const handle = window.setInterval(() => setNow(new Date()), URGENCY_TICK_MS);
    return () => window.clearInterval(handle);
  }, []);

  const query = useQuery({
    queryKey: ["admin-bookings-unassigned-at-risk", page] as const,
    queryFn: () => getUnassignedAtRiskBookings(page, PAGE_SIZE),
    placeholderData: keepPreviousData,
  });

  const columns: DataTableColumn<AdminUnassignedAtRiskBooking>[] = [
    {
      key: "reference",
      header: "Booking #",
      cell: (booking) => (
        <Link
          href={`/bookings/${booking.id}`}
          className="nums font-medium text-fg underline-offset-4 hover:text-brand-600 hover:underline dark:hover:text-brand-400"
        >
          {booking.reference}
        </Link>
      ),
    },
    { key: "customer", header: "Customer", cell: (booking) => booking.customerName },
    { key: "service", header: "Service", cell: (booking) => booking.serviceName },
    {
      key: "slot",
      header: "Slot",
      cell: (booking) => {
        const urgency = slotUrgency(booking.slotDate, booking.slotStartTime, now);
        return (
          <div>
            <div className="nums text-fg">
              {formatSlotDate(booking.slotDate)}, {formatSlotTime(booking.slotStartTime)}
            </div>
            <Badge tone={urgency.tone} className="mt-1">
              {urgency.label}
            </Badge>
          </div>
        );
      },
    },
    {
      key: "locality",
      header: "Locality",
      cell: (booking) => (
        <>
          <span className="text-fg">{booking.city}</span>
          <div className="nums text-xs text-fg-subtle">{booking.pincode}</div>
        </>
      ),
    },
    {
      key: "status",
      header: "Status",
      cell: (booking) => <BookingStatusBadge status={booking.status} label={booking.statusLabel} />,
    },
    {
      key: "actions",
      header: "",
      cell: (booking) =>
        canWrite ? (
          <Button size="sm" onClick={() => setAssigningBooking(booking)}>
            Assign provider
          </Button>
        ) : null,
    },
  ];

  return (
    <div className="w-full max-w-7xl">
      <PageHeading
        title="Unassigned & At-Risk Queue"
        subtitle="Paid bookings with no live provider, soonest slot first - assign one before the slot goes unfulfilled."
      />

      <BookingsTabs />

      {notice ? (
        <div className="mt-4">
          <Alert tone="success">{notice}</Alert>
        </div>
      ) : null}
      {!canWrite ? (
        <div className="mt-4">
          <Alert tone="info">
            You can review this queue but not assign providers - that needs the &quot;bookings.write&quot;
            permission.
          </Alert>
        </div>
      ) : null}

      <div className="mt-6">
        <DataTable
          title="Queue"
          actions={
            <ExportCsvButton
              rows={query.data?.items}
              columns={QUEUE_CSV_COLUMNS}
              fileName={`unassigned-at-risk-export-${todayIsoDate()}.csv`}
            />
          }
          columns={columns}
          rows={query.data?.items}
          rowKey={(booking) => booking.id}
          isLoading={query.isPending}
          isFetching={query.isFetching}
          error={query.error}
          onRetry={() => query.refetch()}
          skeletonRows={8}
          minWidth="980px"
          caption="Paid bookings with no live provider assignment, ordered by how soon the slot starts"
          emptyTitle="Nothing at risk right now"
          emptyDescription="Every paid, assignable booking currently has a live provider on it."
          footer={
            query.data ? (
              <Pagination
                page={page}
                pageSize={PAGE_SIZE}
                totalCount={query.data.totalCount}
                onPageChange={setPage}
                busy={query.isFetching}
                itemLabel="booking"
              />
            ) : null
          }
        />
      </div>

      <AssignProviderModal
        booking={assigningBooking}
        onClose={() => setAssigningBooking(null)}
        onAssigned={(reference) => {
          setAssigningBooking(null);
          setNotice(`Provider assigned to ${reference}.`);
          queryClient.invalidateQueries({ queryKey: ["admin-bookings-unassigned-at-risk"] });
        }}
      />
    </div>
  );
}

/**
 * The one-click assign action (docs/OPEN-FIXES-FEATURES.csv "Add an
 * unassigned and at-risk queue ... allow assignment directly from it"),
 * calling the same `POST /admin/bookings/{id}/assign-provider` the booking
 * detail page's own assignment panel and the conflicts screen's reassignment
 * picker both post to - one write path, one set of validations (area/skill
 * eligibility, active-provider gate, task 288's double-booking check),
 * regardless of which screen the admin assigned from.
 */
function AssignProviderModal({
  booking,
  onClose,
  onAssigned,
}: {
  booking: AdminUnassignedAtRiskBooking | null;
  onClose: () => void;
  onAssigned: (reference: string) => void;
}) {
  const [providerId, setProviderId] = useState("");

  useEffect(() => {
    setProviderId("");
  }, [booking?.id]);

  const eligibleQuery = useQuery({
    queryKey: ["admin-booking-eligible-providers", booking?.id] as const,
    queryFn: () => getEligibleProviders(booking!.id),
    enabled: booking !== null,
  });

  const assignMutation = useMutation({
    mutationFn: () => assignProviderToBooking(booking!.id, { providerId }),
    onSuccess: () => onAssigned(booking!.reference),
  });

  return (
    <Modal
      open={booking !== null}
      onClose={onClose}
      title={booking ? `Assign provider — ${booking.reference}` : "Assign provider"}
      description={booking ? `${booking.serviceName} for ${booking.customerName} in ${booking.city}.` : undefined}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={assignMutation.isPending}>
            Cancel
          </Button>
          <Button
            disabled={!providerId || assignMutation.isPending}
            loading={assignMutation.isPending}
            onClick={() => assignMutation.mutate()}
          >
            Assign
          </Button>
        </>
      }
    >
      {assignMutation.isError ? <Alert tone="error">{describeError(assignMutation.error)}</Alert> : null}
      {eligibleQuery.isPending ? (
        <p className="text-sm text-fg-muted">Loading eligible providers…</p>
      ) : eligibleQuery.isError ? (
        <Alert tone="error">{describeError(eligibleQuery.error)}</Alert>
      ) : (eligibleQuery.data ?? []).length === 0 ? (
        <p className="text-sm text-fg-muted">No eligible provider matches this booking&apos;s area and service.</p>
      ) : (
        <Select
          label="Provider"
          value={providerId}
          onChange={(e) => setProviderId(e.target.value)}
          options={[
            { value: "", label: "Select a provider…" },
            ...(eligibleQuery.data ?? []).map((candidate) => ({
              value: candidate.providerId,
              label: `${candidate.displayName} — ${candidate.assignedJobsToday} jobs today${
                candidate.pincodeMatch ? " · pincode" : ""
              }${candidate.serviceMatch ? " · service" : ""}${formatPerformanceSuffix(candidate)}`,
            })),
          ]}
        />
      )}
    </Modal>
  );
}
