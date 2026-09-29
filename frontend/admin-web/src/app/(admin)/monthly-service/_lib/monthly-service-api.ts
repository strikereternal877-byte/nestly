/**
 * Typed client for the admin Monthly Service surface (docs/MONTHLY-SERVICE.md):
 * plan catalog, engagements (assign / pause / resume / cancel), attendance
 * corrections and disputes, and month-end invoices with offline payment
 * recording.
 *
 * Enums mirror the C# declaration order - admin-api registers no
 * JsonStringEnumConverter, so they cross the wire as ordinals. Day-of-week
 * values are .NET DayOfWeek (Sunday = 0).
 */
import { API_V1, apiFetch } from "@/lib/api";

export enum MonthlyServicePlanBasis {
  Hourly = 0,
  TaskBased = 1,
}

export enum MonthlyServiceFrequency {
  Weekdays = 0,
  TimesPerWeek = 1,
  TimesPerMonth = 2,
}

export const FREQUENCY_LABELS: Record<MonthlyServiceFrequency, string> = {
  [MonthlyServiceFrequency.Weekdays]: "Customer picks weekdays (e.g. maid)",
  [MonthlyServiceFrequency.TimesPerWeek]: "N times a week (e.g. car wash)",
  [MonthlyServiceFrequency.TimesPerMonth]: "N times a month (e.g. car wash)",
};

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

export const CONTRACT_STATUS_LABELS: Record<MonthlyServiceContractStatus, string> = {
  [MonthlyServiceContractStatus.PendingAssignment]: "Needs professional",
  [MonthlyServiceContractStatus.Active]: "Active",
  [MonthlyServiceContractStatus.Paused]: "Paused",
  [MonthlyServiceContractStatus.Cancelled]: "Cancelled",
};

export const ATTENDANCE_LABELS: Record<MonthlyServiceAttendanceStatus, string> = {
  [MonthlyServiceAttendanceStatus.Scheduled]: "Scheduled",
  [MonthlyServiceAttendanceStatus.Present]: "Present",
  [MonthlyServiceAttendanceStatus.CustomerSkipped]: "Customer skipped",
  [MonthlyServiceAttendanceStatus.ProviderLeave]: "Professional leave",
  [MonthlyServiceAttendanceStatus.CustomerUnavailable]: "Customer unavailable",
  [MonthlyServiceAttendanceStatus.Absent]: "Absent",
};

export const ACTOR_LABELS: Record<MonthlyServiceAttendanceActor, string> = {
  [MonthlyServiceAttendanceActor.System]: "System",
  [MonthlyServiceAttendanceActor.Customer]: "Customer",
  [MonthlyServiceAttendanceActor.Provider]: "Professional",
  [MonthlyServiceAttendanceActor.Admin]: "Admin",
};

export const INVOICE_STATUS_LABELS: Record<MonthlyServiceInvoiceStatus, string> = {
  [MonthlyServiceInvoiceStatus.Issued]: "Due",
  [MonthlyServiceInvoiceStatus.Overdue]: "Overdue",
  [MonthlyServiceInvoiceStatus.Paid]: "Paid",
};

export const PAYMENT_METHOD_LABELS: Record<MonthlyServicePaymentMethod, string> = {
  [MonthlyServicePaymentMethod.Online]: "Online",
  [MonthlyServicePaymentMethod.Cash]: "Cash",
  [MonthlyServicePaymentMethod.Upi]: "UPI",
  [MonthlyServicePaymentMethod.BankTransfer]: "Bank transfer",
};

export interface MonthlyServicePlanAdmin {
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
  commissionPercent: number;
  isActive: boolean;
  activeContractCount: number;
  createdAtUtc: string;
  updatedAtUtc: string;
  frequency: MonthlyServiceFrequency;
  timesPerPeriod: number | null;
}

export interface MonthlyServicePlanRequest {
  serviceId: string;
  cityId: string;
  name: string;
  description: string | null;
  basis: MonthlyServicePlanBasis;
  hoursPerVisit: number | null;
  includedTasks: string[];
  ratePerVisit: number;
  commissionPercent: number;
  frequency: MonthlyServiceFrequency;
  timesPerPeriod: number | null;
}

export interface MonthlyServiceAddress {
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

export interface MonthlyServiceSummary {
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

export interface MonthlyServiceContractListItem {
  id: string;
  customerId: string;
  customerName: string;
  customerPhone: string;
  planName: string;
  cityName: string;
  status: MonthlyServiceContractStatus;
  pauseReason: MonthlyServicePauseReason | null;
  providerId: string | null;
  providerName: string | null;
  days: number[];
  visitStartTime: string;
  startDate: string;
  endDate: string | null;
  createdAtUtc: string;
  frequency: MonthlyServiceFrequency;
  timesPerPeriod: number | null;
  monthDates: number[];
}

export interface MonthlyServiceContractSearch {
  items: MonthlyServiceContractListItem[];
  totalCount: number;
  page: number;
  pageSize: number;
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

export interface MonthlyServiceContractDetail {
  contract: MonthlyServiceContractListItem;
  basis: MonthlyServicePlanBasis;
  hoursPerVisit: number | null;
  includedTasks: string[];
  ratePerVisit: number;
  commissionPercent: number;
  customerNote: string | null;
  address: MonthlyServiceAddress | null;
  currentMonth: MonthlyServiceSummary;
  invoices: MonthlyServiceInvoice[];
  cancelledAtUtc: string | null;
  cancellationReason: string | null;
}

export interface MonthlyServiceAttendanceItem {
  id: string;
  contractId: string;
  date: string;
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
  allowedActions: string[];
}

export interface MonthlyServiceAttendanceMonth {
  contractId: string;
  year: number;
  month: number;
  summary: MonthlyServiceSummary;
  items: MonthlyServiceAttendanceItem[];
}

export interface MonthlyServiceEligibleProvider {
  id: string;
  displayName: string;
  phone: string;
  hasSkill: boolean;
  servesCity: boolean;
  activeContractCount: number;
  conflict: string | null;
}

export interface MonthlyServiceDispute {
  attendance: MonthlyServiceAttendanceItem;
  customerName: string;
  providerName: string;
  planName: string;
}

export interface MonthlyServiceInvoiceSearch {
  items: MonthlyServiceInvoice[];
  totalCount: number;
  page: number;
  pageSize: number;
  outstandingAmount: number;
}

export interface MonthlyServiceDailyRunResult {
  daysClosedAsAbsent: number;
  attendanceRowsScheduled: number;
  invoicesIssued: number;
  invoicesHeldForDisputes: number;
  invoicesMarkedOverdue: number;
  contractsPausedForNonPayment: number;
}

export const ADMIN_ATTENDANCE_ACTIONS = { correct: "correct", resolveDispute: "resolve-dispute" } as const;

export const WEEKDAY_SHORT = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

export function describeDays(days: number[]): string {
  if (days.length === 7) return "Every day";
  const set = new Set(days);
  if (set.size === 6 && !set.has(0)) return "Mon – Sat";
  if (set.size === 5 && !set.has(0) && !set.has(6)) return "Mon – Fri";
  return [1, 2, 3, 4, 5, 6, 0].filter((d) => set.has(d)).map((d) => WEEKDAY_SHORT[d]).join(", ");
}

function ordinal(n: number): string {
  const suffix = n % 10 === 1 && n !== 11 ? "st" : n % 10 === 2 && n !== 12 ? "nd" : n % 10 === 3 && n !== 13 ? "rd" : "th";
  return `${n}${suffix}`;
}

/** "Mon – Sat", "3x a week (Mon, Wed, Fri)" or "4x a month (1st, 8th, 15th, 22nd)". */
export function describeSchedule(c: { frequency: MonthlyServiceFrequency; timesPerPeriod: number | null; days: number[]; monthDates: number[] }): string {
  if (c.frequency === MonthlyServiceFrequency.TimesPerMonth) return `${c.timesPerPeriod}x a month (${c.monthDates.map(ordinal).join(", ")})`;
  if (c.frequency === MonthlyServiceFrequency.TimesPerWeek) return `${c.timesPerPeriod}x a week (${describeDays(c.days)})`;
  return describeDays(c.days);
}

export function describePlanFrequency(p: { frequency: MonthlyServiceFrequency; timesPerPeriod: number | null }): string {
  if (p.frequency === MonthlyServiceFrequency.TimesPerMonth) return `${p.timesPerPeriod}x / month`;
  if (p.frequency === MonthlyServiceFrequency.TimesPerWeek) return `${p.timesPerPeriod}x / week`;
  return "Chosen weekdays";
}

export function formatClock(hhmm: string): string {
  const [h, m] = hhmm.split(":").map(Number);
  const hour = h % 12 === 0 ? 12 : h % 12;
  return `${hour}:${String(m).padStart(2, "0")} ${h >= 12 ? "PM" : "AM"}`;
}

/** "2026-10-05" -> "5 Oct 2026" without timezone shifting (a calendar date, not an instant). */
export function formatDay(isoDate: string): string {
  const [y, m, d] = isoDate.split("-").map(Number);
  return new Date(y, m - 1, d).toLocaleDateString("en-IN", { day: "numeric", month: "short", year: "numeric" });
}

export function formatPeriod(periodStart: string): string {
  const [y, m] = periodStart.split("-").map(Number);
  return new Date(y, m - 1, 1).toLocaleDateString("en-IN", { month: "long", year: "numeric" });
}

const PLANS = `${API_V1}/monthly-service-plans`;
const CONTRACTS = `${API_V1}/monthly-service-contracts`;
const INVOICES = `${API_V1}/monthly-service-invoices`;

const post = <T>(url: string, body?: unknown) =>
  apiFetch<T>(url, { method: "POST", authenticated: true, body: body === undefined ? undefined : JSON.stringify(body) });

export const listMonthlyPlans = () => apiFetch<MonthlyServicePlanAdmin[]>(PLANS, { authenticated: true });
export const createMonthlyPlan = (request: MonthlyServicePlanRequest) => post<MonthlyServicePlanAdmin>(PLANS, request);
export const updateMonthlyPlan = (id: string, request: MonthlyServicePlanRequest) =>
  apiFetch<MonthlyServicePlanAdmin>(`${PLANS}/${id}`, { method: "PUT", authenticated: true, body: JSON.stringify(request) });
export const setMonthlyPlanActive = (id: string, active: boolean) => post<void>(`${PLANS}/${id}/${active ? "activate" : "deactivate"}`);

export const searchMonthlyContracts = (params: { status?: MonthlyServiceContractStatus; customerSearch?: string; page: number; pageSize: number }) => {
  const query = new URLSearchParams({ page: String(params.page), pageSize: String(params.pageSize) });
  if (params.status !== undefined) query.set("status", String(params.status));
  if (params.customerSearch) query.set("customerSearch", params.customerSearch);
  return apiFetch<MonthlyServiceContractSearch>(`${CONTRACTS}?${query.toString()}`, { authenticated: true });
};
export const getMonthlyContract = (id: string) => apiFetch<MonthlyServiceContractDetail>(`${CONTRACTS}/${id}`, { authenticated: true });
export const getMonthlyContractAttendance = (id: string, year: number, month: number) =>
  apiFetch<MonthlyServiceAttendanceMonth>(`${CONTRACTS}/${id}/attendance?year=${year}&month=${month}`, { authenticated: true });
export const listEligibleProviders = (id: string) =>
  apiFetch<MonthlyServiceEligibleProvider[]>(`${CONTRACTS}/${id}/eligible-providers`, { authenticated: true });
export const assignMonthlyProvider = (id: string, providerId: string) =>
  post<MonthlyServiceContractDetail>(`${CONTRACTS}/${id}/assign-provider`, { providerId });
export const pauseMonthlyContract = (id: string) => post<void>(`${CONTRACTS}/${id}/pause`);
export const resumeMonthlyContract = (id: string) => post<void>(`${CONTRACTS}/${id}/resume`);
export const cancelMonthlyContract = (id: string, reason: string | null) => post<void>(`${CONTRACTS}/${id}/cancel`, { reason });
export const listMonthlyDisputes = () => apiFetch<MonthlyServiceDispute[]>(`${CONTRACTS}/disputes`, { authenticated: true });
export const resolveMonthlyDispute = (
  attendanceId: string,
  body: { upheld: boolean; correctedStatus: MonthlyServiceAttendanceStatus | null; note: string | null },
) => post<MonthlyServiceAttendanceItem>(`${CONTRACTS}/attendance/${attendanceId}/resolve-dispute`, body);
export const correctMonthlyAttendance = (attendanceId: string, status: MonthlyServiceAttendanceStatus, note: string | null) =>
  apiFetch<MonthlyServiceAttendanceItem>(`${CONTRACTS}/attendance/${attendanceId}`, {
    method: "PUT",
    authenticated: true,
    body: JSON.stringify({ status, note }),
  });
export const runMonthlyDailyJob = () => post<MonthlyServiceDailyRunResult>(`${CONTRACTS}/run-daily-job`);

export const searchMonthlyInvoices = (params: { status?: MonthlyServiceInvoiceStatus; page: number; pageSize: number }) => {
  const query = new URLSearchParams({ page: String(params.page), pageSize: String(params.pageSize) });
  if (params.status !== undefined) query.set("status", String(params.status));
  return apiFetch<MonthlyServiceInvoiceSearch>(`${INVOICES}?${query.toString()}`, { authenticated: true });
};
export const recordMonthlyPayment = (id: string, method: MonthlyServicePaymentMethod, reference: string | null) =>
  post<MonthlyServiceInvoice>(`${INVOICES}/${id}/record-payment`, { method, reference });
