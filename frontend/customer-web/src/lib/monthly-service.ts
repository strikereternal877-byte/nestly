/**
 * Typed client + wire types for the customer Monthly Service surface
 * (docs/MONTHLY-SERVICE.md): maid-style month-long engagements with one
 * professional, an attendance register, and month-end invoices.
 *
 * Enums mirror the C# declaration order: no JsonStringEnumConverter is
 * registered, so every enum crosses the wire as its ordinal (same as
 * BookingStatus and the rest of types.ts). Day-of-week values are .NET's
 * DayOfWeek - Sunday = 0 through Saturday = 6, same as JS `Date.getDay()`.
 */
import { API_V1, apiFetch } from "./api";

export enum MonthlyServicePlanBasis {
  Hourly = 0,
  TaskBased = 1,
}

export enum MonthlyServiceFrequency {
  /** Customer picks any weekdays (a maid). */
  Weekdays = 0,
  /** Exactly N weekdays (e.g. car wash 3x a week). */
  TimesPerWeek = 1,
  /** Exactly N dates of the month, 1-28 (e.g. car wash 4x a month). */
  TimesPerMonth = 2,
}

export const MAX_MONTH_DATE = 28;

export enum MonthlyServiceContractStatus {
  PendingAssignment = 0,
  Active = 1,
  Paused = 2,
  Cancelled = 3,
}

export enum MonthlyServicePauseReason {
  Admin = 0,
  OverdueInvoice = 1,
}

export enum MonthlyServiceAttendanceStatus {
  Scheduled = 0,
  Present = 1,
  CustomerSkipped = 2,
  ProviderLeave = 3,
  CustomerUnavailable = 4,
  Absent = 5,
}

export enum MonthlyServiceAttendanceActor {
  System = 0,
  Customer = 1,
  Provider = 2,
  Admin = 3,
}

export enum MonthlyServiceDisputeStatus {
  None = 0,
  Open = 1,
  Upheld = 2,
  Rejected = 3,
}

export enum MonthlyServiceInvoiceStatus {
  Issued = 0,
  Overdue = 1,
  Paid = 2,
}

export enum MonthlyServicePaymentMethod {
  Online = 0,
  Cash = 1,
  Upi = 2,
  BankTransfer = 3,
}

export interface MonthlyServicePlan {
  id: string;
  serviceId: string;
  serviceName: string;
  cityId: string;
  cityName: string;
  name: string;
  description: string | null;
  basis: MonthlyServicePlanBasis;
  hoursPerVisit: number | null;
  includedTasks: string[];
  ratePerVisit: number;
  frequency: MonthlyServiceFrequency;
  timesPerPeriod: number | null;
}

export interface MonthlyServiceAddressSummary {
  id: string;
  label: string;
  line1: string;
  line2: string | null;
  landmark: string | null;
  city: string;
  pincode: string;
  contactName: string;
  contactMobile: string;
}

export interface MonthlyServiceAttendanceSummary {
  present: number;
  customerUnavailable: number;
  customerSkipped: number;
  providerLeave: number;
  absent: number;
  upcoming: number;
  openDisputes: number;
  billableVisits: number;
  billableAmount: number;
}

export interface MonthlyServiceAttendanceItem {
  id: string;
  contractId: string;
  /** yyyy-MM-dd, business-local calendar date. */
  date: string;
  /** HH:mm, business-local. */
  visitStartTime: string;
  status: MonthlyServiceAttendanceStatus;
  markedBy: MonthlyServiceAttendanceActor | null;
  markedAtUtc: string | null;
  note: string | null;
  checkedInAtUtc: string | null;
  checkedOutAtUtc: string | null;
  disputeStatus: MonthlyServiceDisputeStatus;
  disputeReason: string | null;
  disputeResolutionNote: string | null;
  isBillable: boolean;
  isInvoiced: boolean;
  dayCode: string | null;
  allowedActions: string[];
}

export interface MonthlyServiceAttendanceMonth {
  contractId: string;
  year: number;
  month: number;
  summary: MonthlyServiceAttendanceSummary;
  items: MonthlyServiceAttendanceItem[];
}

export interface MonthlyServiceContract {
  id: string;
  planId: string;
  planName: string;
  serviceName: string;
  basis: MonthlyServicePlanBasis;
  hoursPerVisit: number | null;
  includedTasks: string[];
  ratePerVisit: number;
  days: number[];
  visitStartTime: string;
  startDate: string;
  endDate: string | null;
  status: MonthlyServiceContractStatus;
  pauseReason: MonthlyServicePauseReason | null;
  customerNote: string | null;
  address: MonthlyServiceAddressSummary | null;
  provider: { id: string; displayName: string; photoUrl: string | null } | null;
  today: MonthlyServiceAttendanceItem | null;
  currentMonth: MonthlyServiceAttendanceSummary;
  unpaidAmount: number;
  createdAtUtc: string;
  cancelledAtUtc: string | null;
  cancellationReason: string | null;
  frequency: MonthlyServiceFrequency;
  timesPerPeriod: number | null;
  monthDates: number[];
  /** Next day a visit is still due (today included); null unless active. */
  nextVisitDate: string | null;
}

export interface MonthlyServiceInvoice {
  id: string;
  contractId: string;
  planName: string;
  customerId: string;
  customerName: string;
  providerId: string;
  providerName: string;
  periodStart: string;
  periodEnd: string;
  presentCount: number;
  customerUnavailableCount: number;
  customerSkippedCount: number;
  providerLeaveCount: number;
  absentCount: number;
  billableVisits: number;
  ratePerVisit: number;
  amount: number;
  commissionAmount: number;
  providerNetAmount: number;
  status: MonthlyServiceInvoiceStatus;
  issuedAtUtc: string;
  dueDate: string;
  paidAtUtc: string | null;
  paymentMethod: MonthlyServicePaymentMethod | null;
  paymentReference: string | null;
}

export interface MonthlyServiceContractRequestBody {
  planId: string;
  addressId: string;
  days: number[];
  visitStartTime: string;
  startDate: string;
  endDate: string | null;
  note: string | null;
  monthDates?: number[];
}

export const MONTHLY_SERVICE_ACTIONS = {
  skip: "skip",
  unskip: "unskip",
  confirm: "confirm",
  dispute: "dispute",
} as const;

/** Monday-first, the order a weekly schedule is read in India. */
export const WEEKDAYS: { value: number; short: string; long: string }[] = [
  { value: 1, short: "Mon", long: "Monday" },
  { value: 2, short: "Tue", long: "Tuesday" },
  { value: 3, short: "Wed", long: "Wednesday" },
  { value: 4, short: "Thu", long: "Thursday" },
  { value: 5, short: "Fri", long: "Friday" },
  { value: 6, short: "Sat", long: "Saturday" },
  { value: 0, short: "Sun", long: "Sunday" },
];

export function describeDays(days: number[]): string {
  if (days.length === 7) return "Every day";
  const set = new Set(days);
  if (set.size === 6 && !set.has(0)) return "Mon – Sat";
  if (set.size === 5 && !set.has(0) && !set.has(6)) return "Mon – Fri";
  return WEEKDAYS.filter((d) => set.has(d.value)).map((d) => d.short).join(", ");
}

function ordinal(n: number): string {
  const suffix = n % 10 === 1 && n !== 11 ? "st" : n % 10 === 2 && n !== 12 ? "nd" : n % 10 === 3 && n !== 13 ? "rd" : "th";
  return `${n}${suffix}`;
}

/** How often, in words - "3 times a week" / "4 times a month" / null for a free-weekday plan. */
export function describeFrequency(plan: { frequency: MonthlyServiceFrequency; timesPerPeriod: number | null }): string | null {
  if (plan.frequency === MonthlyServiceFrequency.TimesPerWeek) return `${plan.timesPerPeriod} time${plan.timesPerPeriod === 1 ? "" : "s"} a week`;
  if (plan.frequency === MonthlyServiceFrequency.TimesPerMonth) return `${plan.timesPerPeriod} time${plan.timesPerPeriod === 1 ? "" : "s"} a month`;
  return null;
}

/** A contract's schedule in words: "Mon – Sat", or "4 times a month (1st, 8th, 15th, 22nd)". */
export function describeSchedule(contract: { frequency: MonthlyServiceFrequency; timesPerPeriod: number | null; days: number[]; monthDates: number[] }): string {
  if (contract.frequency === MonthlyServiceFrequency.TimesPerMonth) {
    return `${describeFrequency(contract)} (${contract.monthDates.map(ordinal).join(", ")})`;
  }
  const days = describeDays(contract.days);
  return contract.frequency === MonthlyServiceFrequency.TimesPerWeek ? `${describeFrequency(contract)} (${days})` : days;
}

export function describeVisit(plan: { basis: MonthlyServicePlanBasis; hoursPerVisit: number | null }): string {
  return plan.basis === MonthlyServicePlanBasis.Hourly && plan.hoursPerVisit
    ? `${plan.hoursPerVisit} hour${plan.hoursPerVisit === 1 ? "" : "s"} per visit`
    : "Fixed tasks per visit";
}

/** "08:00" -> "8:00 AM". */
export function formatClock(hhmm: string): string {
  const [h, m] = hhmm.split(":").map(Number);
  const suffix = h >= 12 ? "PM" : "AM";
  const hour = h % 12 === 0 ? 12 : h % 12;
  return `${hour}:${String(m).padStart(2, "0")} ${suffix}`;
}

export const attendanceLabel: Record<MonthlyServiceAttendanceStatus, string> = {
  [MonthlyServiceAttendanceStatus.Scheduled]: "Scheduled",
  [MonthlyServiceAttendanceStatus.Present]: "Came",
  [MonthlyServiceAttendanceStatus.CustomerSkipped]: "Skipped by you",
  [MonthlyServiceAttendanceStatus.ProviderLeave]: "On leave",
  [MonthlyServiceAttendanceStatus.CustomerUnavailable]: "You were not available",
  [MonthlyServiceAttendanceStatus.Absent]: "Did not come",
};

const MY_CONTRACTS = `${API_V1}/me/monthly-service-contracts`;
const MY_ATTENDANCE = `${API_V1}/me/monthly-service-attendance`;
const MY_INVOICES = `${API_V1}/me/monthly-service-invoices`;

export const browseMonthlyPlans = (cityId?: string) =>
  apiFetch<MonthlyServicePlan[]>(`${API_V1}/monthly-service/plans${cityId ? `?cityId=${cityId}` : ""}`);

export const requestMonthlyService = (body: MonthlyServiceContractRequestBody) =>
  apiFetch<MonthlyServiceContract>(`${API_V1}/monthly-service/contracts`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(body),
  });

export const listMyMonthlyServices = () =>
  apiFetch<MonthlyServiceContract[]>(MY_CONTRACTS, { authenticated: true });

export const getMyMonthlyService = (id: string) =>
  apiFetch<MonthlyServiceContract>(`${MY_CONTRACTS}/${id}`, { authenticated: true });

export const getMonthlyAttendance = (id: string, year: number, month: number) =>
  apiFetch<MonthlyServiceAttendanceMonth>(`${MY_CONTRACTS}/${id}/attendance?year=${year}&month=${month}`, {
    authenticated: true,
  });

export const cancelMyMonthlyService = (id: string, reason: string | null) =>
  apiFetch<void>(`${MY_CONTRACTS}/${id}/cancel`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify({ reason }),
  });

export const actOnAttendance = (attendanceId: string, action: "skip" | "unskip" | "confirm") =>
  apiFetch<MonthlyServiceAttendanceItem>(`${MY_ATTENDANCE}/${attendanceId}/${action}`, {
    method: "POST",
    authenticated: true,
  });

export const disputeAttendance = (attendanceId: string, reason: string) =>
  apiFetch<MonthlyServiceAttendanceItem>(`${MY_ATTENDANCE}/${attendanceId}/dispute`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify({ reason }),
  });

export const listMyMonthlyInvoices = () =>
  apiFetch<MonthlyServiceInvoice[]>(MY_INVOICES, { authenticated: true });

export const payMonthlyInvoice = (invoiceId: string) =>
  apiFetch<MonthlyServiceInvoice>(`${MY_INVOICES}/${invoiceId}/pay`, {
    method: "POST",
    authenticated: true,
  });
