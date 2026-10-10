import { API_V1, apiFetch } from "@/lib/api";
import type { BookingStatus } from "@/lib/types";

/**
 * Typed client for the admin recurring-plan surface (task 299):
 * `GET /admin/recurring-plans`, `GET /admin/recurring-plans/report`, `GET /admin/recurring-plans/{id}` and the
 * pause / resume / cancel actions.
 *
 * Lives under `bookings/_lib` rather than `src/lib` for the same reason
 * `nestly-coins/_lib/coins-api.ts` does - nothing outside this module consumes
 * it, and the shared clients in `src/lib` each back a whole SRS section.
 * Recurring plans are not a section of their own: they are a way bookings come
 * into existence, which is also why they sit behind `bookings.read` rather
 * than a permission of their own (see RecurringPlansController's doc comment).
 */

/**
 * Mirrors Nestly.Domain.RecurringBookingPlanStatus's declaration order.
 * admin-api registers no JsonStringEnumConverter, so this enum crosses the
 * wire as its ordinal - same convention as `lib/types.ts`'s BookingStatus.
 * Keep in sync with the C# enum if its order ever changes.
 */
export enum RecurringPlanStatus {
  Active = 0,
  Paused = 1,
  Cancelled = 2,
  Completed = 3,
}

/** Mirrors Nestly.Domain.RecurringBookingRecurrenceFrequency's declaration order. */
export enum RecurrenceFrequency {
  Weekly = 0,
  Biweekly = 1,
  Monthly = 2,
  Daily = 3,
}

/**
 * Mirrors Nestly.Domain.RecurringBookingPauseReason's declaration order - why a plan is Paused. Append-only on the
 * server because it crosses the wire as its ordinal.
 */
export enum RecurringPlanPauseReason {
  Customer = 0,
  UnpaidVisits = 1,
  PaymentFailure = 2,
  Admin = 3,
}

/** Short wording for the status cell and the filter. */
export const PAUSE_REASON_LABELS: Record<RecurringPlanPauseReason, string> = {
  [RecurringPlanPauseReason.Customer]: "Paused by the customer",
  [RecurringPlanPauseReason.UnpaidVisits]: "Paused - visits went unpaid",
  [RecurringPlanPauseReason.PaymentFailure]: "Paused - auto-charge failed",
  [RecurringPlanPauseReason.Admin]: "Paused by support",
};

export const PLAN_STATUS_LABELS: Record<RecurringPlanStatus, string> = {
  [RecurringPlanStatus.Active]: "Active",
  [RecurringPlanStatus.Paused]: "Paused",
  [RecurringPlanStatus.Cancelled]: "Cancelled",
  [RecurringPlanStatus.Completed]: "Completed",
};

export const FREQUENCY_LABELS: Record<RecurrenceFrequency, string> = {
  [RecurrenceFrequency.Weekly]: "Weekly",
  [RecurrenceFrequency.Biweekly]: "Every 2 weeks",
  [RecurrenceFrequency.Monthly]: "Monthly",
  [RecurrenceFrequency.Daily]: "Every day",
};

const DAY_NAMES = [
  "Sunday",
  "Monday",
  "Tuesday",
  "Wednesday",
  "Thursday",
  "Friday",
  "Saturday",
] as const;

/**
 * "Weekly on Tuesday" / "Monthly on the 11th" — the cadence phrased the way a
 * human reads a standing appointment, rather than three columns the reader has
 * to recombine themselves. `DayOfWeek` crosses the wire as its .NET ordinal,
 * which is Sunday-first.
 */
export function describeCadence(plan: {
  frequency: RecurrenceFrequency;
  recurrenceDayOfWeek: number | null;
  recurrenceDayOfMonth: number | null;
}): string {
  const base = FREQUENCY_LABELS[plan.frequency] ?? String(plan.frequency);
  if (plan.recurrenceDayOfWeek !== null) {
    const day = DAY_NAMES[plan.recurrenceDayOfWeek];
    return day ? `${base} on ${day}` : base;
  }
  if (plan.recurrenceDayOfMonth !== null) {
    return `${base} on day ${plan.recurrenceDayOfMonth}`;
  }
  return base;
}

export interface RecurringPlanListItem {
  id: string;
  customerId: string;
  customerName: string;
  serviceId: string;
  serviceName: string;
  frequency: RecurrenceFrequency;
  recurrenceDayOfWeek: number | null;
  recurrenceDayOfMonth: number | null;
  startDate: string;
  endDate: string | null;
  occurrenceCount: number | null;
  completedOccurrenceCount: number;
  nextOccurrenceDate: string;
  status: RecurringPlanStatus;
  createdAtUtc: string;
  /** Paid for in advance (all visits up front) rather than visit by visit. */
  prepaidUpfront: boolean;
  autoChargeEnabled: boolean;
  /** Each visit is paid from the customer's wallet as it is booked. */
  applyWalletCredit: boolean;
  /** A prepaid cycle has been started and is waiting for the customer to pay. */
  isAwaitingPrepayment: boolean;
  prepaidThroughDate: string | null;
  /** Why the plan is Paused; null when it is not. */
  pauseReason: RecurringPlanPauseReason | null;
  /** The last "skip visits until" date the customer asked for. */
  skipUntilDate: string | null;
}

export interface RecurringPlanVisit {
  bookingId: string;
  bookingReference: string;
  slotDate: string;
  status: BookingStatus;
  statusLabel: string;
  totalPayable: number;
}

/** One plan with the customer's contact and wallet balance and the visits it has generated (upcoming first, then the latest past ones). */
export interface RecurringPlanDetail {
  plan: RecurringPlanListItem;
  customerMobile: string;
  walletBalance: number;
  visits: RecurringPlanVisit[];
}

export interface RecurringPlanSearchResponse {
  items: RecurringPlanListItem[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface RecurringPlanStatusCount {
  status: RecurringPlanStatus;
  planCount: number;
}

export interface RecurringPlanFrequencyCount {
  frequency: RecurrenceFrequency;
  planCount: number;
}

export interface RecurringPlanDailyVolume {
  slotDate: string;
  bookingCount: number;
}

export interface RecurringPlanReport {
  totalPlans: number;
  byStatus: RecurringPlanStatusCount[];
  activeByFrequency: RecurringPlanFrequencyCount[];
  horizonFromDate: string;
  horizonToDate: string;
  plansDueInHorizon: number;
  upcomingOccurrenceVolume: number;
  upcomingVolumeByDate: RecurringPlanDailyVolume[];
}

export interface RecurringPlanSearchParams {
  status?: string;
  frequency?: string;
  /** A RecurringPlanPauseReason ordinal, as a string. */
  pauseReason?: string;
  /** "true" = prepaid plans only, "false" = pay-per-visit plans only. */
  prepaidUpfront?: string;
  page: number;
  pageSize: number;
}

const BASE = `${API_V1}/recurring-plans`;

export function searchRecurringPlans(
  params: RecurringPlanSearchParams,
): Promise<RecurringPlanSearchResponse> {
  const query = new URLSearchParams({
    page: String(params.page),
    pageSize: String(params.pageSize),
  });
  if (params.status) query.set("status", params.status);
  if (params.frequency) query.set("frequency", params.frequency);
  if (params.pauseReason) query.set("pauseReason", params.pauseReason);
  if (params.prepaidUpfront) query.set("prepaidUpfront", params.prepaidUpfront);

  return apiFetch<RecurringPlanSearchResponse>(`${BASE}?${query.toString()}`, {
    authenticated: true,
  });
}

/**
 * The horizon is sent as plain `yyyy-mm-dd`, not as a UTC instant: the backend
 * takes `DateOnly` and compares against a booking's slot *date*, which is a
 * calendar day rather than a moment (unlike the coins/coupon reports, whose
 * ranges are `DateTime` and so go through `lib/day-range`). Omitting both ends
 * asks the server for its own default horizon.
 */
export function getRecurringPlanReport(
  fromDate?: string,
  toDate?: string,
): Promise<RecurringPlanReport> {
  const query = new URLSearchParams();
  if (fromDate) query.set("fromDate", fromDate);
  if (toDate) query.set("toDate", toDate);
  const suffix = query.toString() ? `?${query.toString()}` : "";

  return apiFetch<RecurringPlanReport>(`${BASE}/report${suffix}`, { authenticated: true });
}

/**
 * Cancels the whole standing instruction - no further occurrences are ever
 * generated (Order/Booking Management UX pass: previously an admin could
 * only stop recurring work by cancelling the individual bookings it had
 * already produced, one at a time, via BookingsController). A reason is
 * required for the audit trail.
 */
export function cancelRecurringPlan(planId: string, reason: string): Promise<RecurringPlanListItem> {
  return apiFetch<RecurringPlanListItem>(`${BASE}/${planId}/cancel`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify({ reason }),
  });
}

/** One plan in full: wallet balance, customer contact and the visits it has produced (`GET /admin/recurring-plans/{id}`). */
export function getRecurringPlan(planId: string): Promise<RecurringPlanDetail> {
  return apiFetch<RecurringPlanDetail>(`${BASE}/${planId}`, { authenticated: true });
}

/**
 * Pauses an active plan on the customer's behalf. Visits already booked are untouched; the customer is told support
 * paused it and cannot resume it themselves. A reason is required for the audit trail.
 */
export function pauseRecurringPlan(planId: string, reason: string): Promise<RecurringPlanListItem> {
  return apiFetch<RecurringPlanListItem>(`${BASE}/${planId}/pause`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify({ reason }),
  });
}

/** Resumes a paused plan, whoever or whatever paused it. The customer is told; a reason is required for the audit trail. */
export function resumeRecurringPlan(planId: string, reason: string): Promise<RecurringPlanListItem> {
  return apiFetch<RecurringPlanListItem>(`${BASE}/${planId}/resume`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify({ reason }),
  });
}
