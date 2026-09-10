/**
 * Week-calendar composition for `/calendar` (docs/OPEN-FIXES-FEATURES.csv,
 * "Provider Web, Proposed new page, Calendar and week view": "Availability
 * and committed jobs are shown in separate places... making it hard for a
 * professional to manage their week or spot a double booking before it
 * happens").
 *
 * Deliberately pure client-side arithmetic over the two endpoints the page
 * already fetches (`GET /availability`, `GET /jobs`) rather than a new
 * backend endpoint: both "a job's slot falls outside declared availability"
 * and "two jobs overlap" are plain date/time interval comparisons over data
 * already on the wire (`JobListItem.slotDate` + the two time snapshots,
 * `AvailabilityWindow.dayOfWeek` + its two times) - a combined endpoint would
 * duplicate this same logic server-side for no additional data the frontend
 * doesn't already have.
 */
import { toLocalIsoDate } from "./date";
import { isActiveJobStatus } from "./jobs-active";
import type { AvailabilityWindow, DayOfWeek } from "./availability-types";
import type { JobListItem } from "./jobs-types";

/** A `Date` some number of days from `date`. Negative goes backwards. Does not mutate `date`. */
export function addDays(date: Date, days: number): Date {
  const next = new Date(date);
  next.setDate(next.getDate() + days);
  return next;
}

/**
 * The Monday that starts the week containing `date` - work-week order, same
 * convention the `/availability` editor's `DAY_ORDER` uses (providers think
 * in work weeks, not weeks that start on a Sunday).
 */
export function startOfWeek(date: Date): Date {
  const start = new Date(date);
  const day = start.getDay();
  const mondayOffset = day === 0 ? -6 : 1 - day;
  start.setDate(start.getDate() + mondayOffset);
  start.setHours(0, 0, 0, 0);
  return start;
}

/** The 7 local `YYYY-MM-DD` calendar dates of the week starting at `weekStart` (Monday..Sunday). */
export function weekDates(weekStart: Date): string[] {
  return Array.from({ length: 7 }, (_, index) => toLocalIsoDate(addDays(weekStart, index)));
}

/**
 * A `YYYY-MM-DD` calendar date's day of week, as the same ordinal
 * `AvailabilityWindow.dayOfWeek` uses - JS's `Date.getDay()` and .NET's
 * `System.DayOfWeek` both start the week at Sunday = 0, so no remapping is
 * needed. Parsed as local date parts rather than through `new Date(string)` -
 * see lib/date.ts's header comment for why a bare date string is UTC-anchored
 * and reads as the previous day in IST before 05:30.
 */
export function dayOfWeekOf(isoDate: string): DayOfWeek {
  const [year, month, day] = isoDate.split("-").map(Number);
  return new Date(year, month - 1, day).getDay() as DayOfWeek;
}

function toMinutes(time: string): number {
  const [hours, minutes] = time.split(":").map(Number);
  return hours * 60 + minutes;
}

interface TimeRange {
  start: string;
  end: string;
}

/**
 * True when `range` falls entirely inside at least one of `windows` - a slot
 * that only partly overlaps a declared window (starts before it opens, or
 * runs past when it closes) is still a real "outside availability" problem,
 * not a partial pass.
 */
function isWithinAnyWindow(range: TimeRange, windows: readonly AvailabilityWindow[]): boolean {
  const start = toMinutes(range.start);
  const end = toMinutes(range.end);
  return windows.some((w) => toMinutes(w.startTime) <= start && end <= toMinutes(w.endTime));
}

/** True when two same-day time ranges intersect (half-open, so a job ending exactly when another starts is not a conflict). */
function timeRangesOverlap(a: TimeRange, b: TimeRange): boolean {
  return toMinutes(a.start) < toMinutes(b.end) && toMinutes(b.start) < toMinutes(a.end);
}

/** One job's conflict state, keyed by `JobListItem.assignmentId` in {@link computeJobConflicts}'s result. */
export interface JobConflict {
  /** This job's slot falls outside every declared availability window for its day of week. */
  outsideAvailability: boolean;
  /** `assignmentId`s of other committed jobs whose slot overlaps this one on the same day. Empty when none. */
  overlapsWithAssignmentIds: string[];
}

/**
 * Conflict state for every "committed" job - {@link isActiveJobStatus}, the
 * same broader-than-`Accepted` filter `/today` uses for "still has a real
 * slot commitment" (an open `Assigned` offer already holds a slot just as
 * much as an accepted one, and a provider juggling two offers for the same
 * hour needs to see that clash before accepting either). Terminal statuses
 * (`Completed`, `Rejected`, `Reassigned`, `Withdrawn`) are excluded - a slot
 * that never happened, or already did, cannot conflict with anything.
 *
 * Only jobs that actually have a conflict appear as keys in the result -
 * absence of an entry means "no conflict", so callers can use `.get(id)` with
 * a fallback rather than testing every field on every job.
 */
export function computeJobConflicts(
  jobs: readonly JobListItem[],
  windows: readonly AvailabilityWindow[],
): ReadonlyMap<string, JobConflict> {
  const committed = jobs.filter((job) => isActiveJobStatus(job.status));
  const result = new Map<string, JobConflict>();

  for (const job of committed) {
    const jobRange: TimeRange = { start: job.slotStartTimeSnapshot, end: job.slotEndTimeSnapshot };
    const dayWindows = windows.filter((w) => w.dayOfWeek === dayOfWeekOf(job.slotDate));
    const outsideAvailability = dayWindows.length === 0 || !isWithinAnyWindow(jobRange, dayWindows);

    const overlapsWithAssignmentIds = committed
      .filter(
        (other) =>
          other.assignmentId !== job.assignmentId &&
          other.slotDate === job.slotDate &&
          timeRangesOverlap(jobRange, { start: other.slotStartTimeSnapshot, end: other.slotEndTimeSnapshot }),
      )
      .map((other) => other.assignmentId);

    if (outsideAvailability || overlapsWithAssignmentIds.length > 0) {
      result.set(job.assignmentId, { outsideAvailability, overlapsWithAssignmentIds });
    }
  }

  return result;
}
