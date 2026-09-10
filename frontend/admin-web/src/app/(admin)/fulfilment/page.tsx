"use client";

import Link from "next/link";
import { useEffect, useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Alert, Badge, Button, Card, Field, PageHeading, Skeleton } from "@/components/ui";
import type { BadgeTone } from "@/components/ui";
import { describeError } from "@/lib/api";
import { getFulfilmentBoard } from "@/lib/bookings-api";
import type { AdminFulfilmentBoardBooking } from "@/lib/bookings-types";
import { todayIsoDate } from "@/lib/date";
import { BookingStatus } from "@/lib/types";

/**
 * Fulfilment control room (docs/OPEN-FIXES-FEATURES.csv "Admin Web, Proposed
 * new page, Fulfilment control room"): "No single operational view of what
 * is happening today ... the only way to see the job's real state was to
 * open the booking detail and read the timeline." This is that single view -
 * a kanban board of one day's bookings grouped by live fulfilment state, each
 * card drilling through to the existing booking detail page
 * (`/bookings/[bookingId]`).
 *
 * This is a NEW page, not an edit to `/dashboard` - that screen stays the
 * date-filtered aggregate of counts it already is (see its own doc comment);
 * this one is the live, actionable, per-booking view the CSV row asked for.
 *
 * Backend: one endpoint (`GET /bookings/fulfilment-board?date=`) returns every
 * operationally live booking for the day, flat - see
 * `AdminFulfilmentBoardBookingResponse`'s doc comment for exactly which
 * statuses. Bucketing into columns, and the "overdue/at-risk" read layered on
 * top, both happen here rather than server-side, the same split the
 * unassigned-at-risk queue page already uses for its own countdown labels:
 * "now" only means something at render time, not at response time.
 *
 * Nav: added as its own sidebar entry (`/fulfilment`, "bookings.read"-gated)
 * rather than replacing `/dashboard` as the default landing route - ops teams
 * already have `/dashboard` as a muscle-memory habit, and swapping the
 * default out from under them is a bigger call than this task needs to make.
 * The new page is fully built and one click away for whoever wants it as
 * their real morning view.
 */

/** Mirrors the unassigned-at-risk queue page's own threshold - "about to be missed" reads the same red everywhere. */
const URGENT_THRESHOLD_MINUTES = 180;

type ColumnKey = "unassigned" | "assigned" | "enroute" | "inprogress" | "completed" | "atrisk";

const COLUMNS: { key: ColumnKey; label: string; tone: BadgeTone }[] = [
  { key: "atrisk", label: "Overdue / At risk", tone: "danger" },
  { key: "unassigned", label: "Unassigned", tone: "warning" },
  { key: "assigned", label: "Assigned", tone: "info" },
  { key: "enroute", label: "En route", tone: "brand" },
  { key: "inprogress", label: "In progress", tone: "brand" },
  { key: "completed", label: "Completed", tone: "success" },
];

function formatSlotTime(time: string): string {
  // .NET TimeSpan serialises as "hh:mm:ss" - trimmed to "hh:mm", matching the
  // unassigned-at-risk queue page's own formatter.
  return time.slice(0, 5);
}

/**
 * Whether a card counts as overdue/at-risk right now - past its slot start
 * with no completion, or unassigned with the slot imminent. Both halves of
 * the CSV row's own definition ("slot within some threshold with no active
 * provider, or already past slot start with no completion").
 */
function isAtRisk(booking: AdminFulfilmentBoardBooking, now: Date): boolean {
  if (booking.status === BookingStatus.Completed) return false;

  const target = new Date(`${booking.slotDate}T${booking.slotStartTime}`);
  if (Number.isNaN(target.getTime())) return false;
  const diffMinutes = Math.round((target.getTime() - now.getTime()) / 60_000);

  if (diffMinutes <= 0) return true; // past slot start, still not done
  if (!booking.assignedProviderId) {
    const unassignedStatus =
      booking.status === BookingStatus.Confirmed || booking.status === BookingStatus.AwaitingFulfilment;
    return unassignedStatus && diffMinutes < URGENT_THRESHOLD_MINUTES;
  }
  return false;
}

/** Columns are mutually exclusive - an at-risk card shows once, in "Overdue / At risk", not twice. */
function columnFor(booking: AdminFulfilmentBoardBooking, now: Date): ColumnKey | null {
  if (isAtRisk(booking, now)) return "atrisk";

  switch (booking.status) {
    case BookingStatus.Confirmed:
    case BookingStatus.AwaitingFulfilment:
      return "unassigned";
    case BookingStatus.Assigned:
      return "assigned";
    case BookingStatus.ProviderEnRoute:
      return "enroute";
    case BookingStatus.ProviderArrived:
    case BookingStatus.InProgress:
      return "inprogress";
    case BookingStatus.Completed:
      return "completed";
    default:
      return null; // not a live fulfilment state - shouldn't reach here given the endpoint's own filter, but stay defensive.
  }
}

function slotUrgencyTone(booking: AdminFulfilmentBoardBooking, now: Date): BadgeTone {
  if (isAtRisk(booking, now)) return "danger";
  const target = new Date(`${booking.slotDate}T${booking.slotStartTime}`);
  if (Number.isNaN(target.getTime())) return "neutral";
  const diffMinutes = Math.round((target.getTime() - now.getTime()) / 60_000);
  if (diffMinutes < URGENT_THRESHOLD_MINUTES) return "warning";
  return "neutral";
}

export default function FulfilmentBoardPage() {
  const [date, setDate] = useState(() => todayIsoDate());
  const [now, setNow] = useState(() => new Date());

  // Re-derives "in 2h"/"overdue" labels and at-risk bucketing without a
  // refetch, matching the unassigned-at-risk queue page's own tick.
  useEffect(() => {
    const handle = window.setInterval(() => setNow(new Date()), 60_000);
    return () => window.clearInterval(handle);
  }, []);

  const query = useQuery({
    queryKey: ["admin-fulfilment-board", date] as const,
    queryFn: () => getFulfilmentBoard(date),
  });

  const grouped = useMemo(() => {
    const groups: Record<ColumnKey, AdminFulfilmentBoardBooking[]> = {
      unassigned: [],
      assigned: [],
      enroute: [],
      inprogress: [],
      completed: [],
      atrisk: [],
    };
    for (const booking of query.data?.items ?? []) {
      const key = columnFor(booking, now);
      if (key) groups[key].push(booking);
    }
    for (const key of Object.keys(groups) as ColumnKey[]) {
      groups[key].sort(
        (a, b) =>
          new Date(`${a.slotDate}T${a.slotStartTime}`).getTime() -
          new Date(`${b.slotDate}T${b.slotStartTime}`).getTime(),
      );
    }
    return groups;
  }, [query.data, now]);

  const totalCount = query.data?.items.length ?? 0;

  return (
    <div className="w-full max-w-[1400px]">
      <PageHeading
        title="Fulfilment Control Room"
        subtitle="Every job happening today, grouped by live state - click a card to open its booking."
      />

      <div className="mb-6 flex flex-wrap items-end justify-between gap-4">
        <Field
          label="Date"
          type="date"
          value={date}
          onChange={(event) => setDate(event.target.value)}
          className="w-48"
        />
        <div className="flex items-center gap-3">
          {query.data ? (
            <span className="text-sm text-fg-muted">
              {totalCount} {totalCount === 1 ? "booking" : "bookings"}
            </span>
          ) : null}
          <Button variant="secondary" onClick={() => query.refetch()} loading={query.isFetching}>
            Refresh
          </Button>
        </div>
      </div>

      {query.isError ? (
        <Alert tone="error" action={<Button size="sm" onClick={() => query.refetch()}>Retry</Button>}>
          {describeError(query.error)}
        </Alert>
      ) : null}

      {query.isPending ? (
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-6">
          {COLUMNS.map((column) => (
            <div key={column.key} className="flex flex-col gap-3">
              <Skeleton className="h-6 w-32" />
              <Skeleton className="h-24 w-full" />
              <Skeleton className="h-24 w-full" />
            </div>
          ))}
        </div>
      ) : (
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-6">
          {COLUMNS.map((column) => (
            <BoardColumn key={column.key} label={column.label} tone={column.tone} bookings={grouped[column.key]} now={now} />
          ))}
        </div>
      )}
    </div>
  );
}

function BoardColumn({
  label,
  tone,
  bookings,
  now,
}: {
  label: string;
  tone: BadgeTone;
  bookings: AdminFulfilmentBoardBooking[];
  now: Date;
}) {
  return (
    <div className="flex min-w-0 flex-col gap-3">
      <div className="flex items-center justify-between gap-2 px-1">
        <span className="text-sm font-semibold text-fg">{label}</span>
        <Badge tone={tone}>{bookings.length}</Badge>
      </div>
      <div className="flex flex-col gap-3">
        {bookings.length === 0 ? (
          <Card className="border border-dashed border-line !shadow-none">
            <p className="text-center text-xs text-fg-subtle">Nothing here</p>
          </Card>
        ) : (
          bookings.map((booking) => <BoardCard key={booking.id} booking={booking} now={now} />)
        )}
      </div>
    </div>
  );
}

function BoardCard({ booking, now }: { booking: AdminFulfilmentBoardBooking; now: Date }) {
  return (
    <Link href={`/bookings/${booking.id}`} className="group block">
      <Card className="transition-shadow duration-fast ease-out hover:shadow-md">
        <div className="flex items-start justify-between gap-2">
          <span className="nums text-sm font-medium text-fg underline-offset-4 group-hover:underline">
            {booking.reference}
          </span>
          <Badge tone={slotUrgencyTone(booking, now)}>{formatSlotTime(booking.slotStartTime)}</Badge>
        </div>
        <p className="mt-2 truncate text-sm text-fg">{booking.customerName}</p>
        <p className="truncate text-xs text-fg-muted">{booking.serviceName}</p>
        <p className="mt-2 truncate text-xs text-fg-subtle">
          {booking.assignedProviderName ? `Provider: ${booking.assignedProviderName}` : "No provider assigned"}
        </p>
      </Card>
    </Link>
  );
}
