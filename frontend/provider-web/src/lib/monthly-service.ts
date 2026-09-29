/**
 * Typed client + wire types for the professional's Monthly Service surface
 * (docs/MONTHLY-SERVICE.md): their standing engagements, the day's visits,
 * and attendance marking (check-in with the customer's code, check-out,
 * leave, customer not available).
 *
 * Enums mirror the C# declaration order - no JsonStringEnumConverter is
 * registered, so they cross the wire as ordinals. Day-of-week values are
 * .NET DayOfWeek (Sunday = 0), same as `Date.getDay()`.
 */
import { API_V1, apiFetch } from "./api";

export enum MonthlyServicePlanBasis {
  Hourly = 0,
  TaskBased = 1,
}

export enum MonthlyServiceContractStatus {
  PendingAssignment = 0,
  Active = 1,
  Paused = 2,
  Cancelled = 3,
}

export enum MonthlyServiceAttendanceStatus {
  Scheduled = 0,
  Present = 1,
  CustomerSkipped = 2,
  ProviderLeave = 3,
  CustomerUnavailable = 4,
  Absent = 5,
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

export interface MonthlyAddress {
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

export interface MonthlySummary {
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

export interface MonthlyAttendanceItem {
  id: string;
  contractId: string;
  date: string;
  visitStartTime: string;
  status: MonthlyServiceAttendanceStatus;
  note: string | null;
  checkedInAtUtc: string | null;
  checkedOutAtUtc: string | null;
  disputeStatus: MonthlyServiceDisputeStatus;
  isBillable: boolean;
  isInvoiced: boolean;
  allowedActions: string[];
}

export interface MonthlyContract {
  id: string;
  planName: string;
  serviceName: string;
  basis: MonthlyServicePlanBasis;
  hoursPerVisit: number | null;
  includedTasks: string[];
  days: number[];
  visitStartTime: string;
  startDate: string;
  endDate: string | null;
  status: MonthlyServiceContractStatus;
  customerName: string;
  customerNote: string | null;
  address: MonthlyAddress | null;
  ratePerVisit: number;
  netPerVisit: number;
  currentMonth: MonthlySummary;
}

export interface MonthlyVisit {
  attendance: MonthlyAttendanceItem;
  planName: string;
  basis: MonthlyServicePlanBasis;
  hoursPerVisit: number | null;
  includedTasks: string[];
  customerName: string;
  address: MonthlyAddress | null;
}

export interface MonthlyAttendanceMonth {
  contractId: string;
  year: number;
  month: number;
  summary: MonthlySummary;
  items: MonthlyAttendanceItem[];
}

export interface MonthlyInvoice {
  id: string;
  contractId: string;
  planName: string;
  customerName: string;
  periodStart: string;
  periodEnd: string;
  presentCount: number;
  customerUnavailableCount: number;
  customerSkippedCount: number;
  providerLeaveCount: number;
  absentCount: number;
  billableVisits: number;
  amount: number;
  commissionAmount: number;
  providerNetAmount: number;
  status: MonthlyServiceInvoiceStatus;
  dueDate: string;
  paidAtUtc: string | null;
}

export const VISIT_ACTIONS = {
  checkIn: "check-in",
  checkOut: "check-out",
  leave: "leave",
  cancelLeave: "cancel-leave",
  customerUnavailable: "customer-unavailable",
} as const;

export const WEEKDAY_SHORT = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

export function describeDays(days: number[]): string {
  if (days.length === 7) return "Every day";
  const set = new Set(days);
  if (set.size === 6 && !set.has(0)) return "Mon – Sat";
  if (set.size === 5 && !set.has(0) && !set.has(6)) return "Mon – Fri";
  return [1, 2, 3, 4, 5, 6, 0].filter((d) => set.has(d)).map((d) => WEEKDAY_SHORT[d]).join(", ");
}

export function formatClock(hhmm: string): string {
  const [h, m] = hhmm.split(":").map(Number);
  const hour = h % 12 === 0 ? 12 : h % 12;
  return `${hour}:${String(m).padStart(2, "0")} ${h >= 12 ? "PM" : "AM"}`;
}

export const ATTENDANCE_LABEL: Record<MonthlyServiceAttendanceStatus, string> = {
  [MonthlyServiceAttendanceStatus.Scheduled]: "Scheduled",
  [MonthlyServiceAttendanceStatus.Present]: "Present",
  [MonthlyServiceAttendanceStatus.CustomerSkipped]: "Customer skipped",
  [MonthlyServiceAttendanceStatus.ProviderLeave]: "Your leave",
  [MonthlyServiceAttendanceStatus.CustomerUnavailable]: "Customer not available",
  [MonthlyServiceAttendanceStatus.Absent]: "Absent",
};

const BASE = `${API_V1}/monthly-service`;

export const listMonthlyContracts = () => apiFetch<MonthlyContract[]>(`${BASE}/contracts`, { authenticated: true });

export const getMonthlyAttendance = (contractId: string, year: number, month: number) =>
  apiFetch<MonthlyAttendanceMonth>(`${BASE}/contracts/${contractId}/attendance?year=${year}&month=${month}`, {
    authenticated: true,
  });

export const listMonthlyVisits = (date?: string) =>
  apiFetch<MonthlyVisit[]>(`${BASE}/visits${date ? `?date=${date}` : ""}`, { authenticated: true });

export const listMonthlyInvoices = () => apiFetch<MonthlyInvoice[]>(`${BASE}/invoices`, { authenticated: true });

export const checkInVisit = (attendanceId: string, code: string, latitude: number | null, longitude: number | null) =>
  apiFetch<MonthlyAttendanceItem>(`${BASE}/visits/${attendanceId}/check-in`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify({ code, latitude, longitude }),
  });

export const checkOutVisit = (attendanceId: string) =>
  apiFetch<MonthlyAttendanceItem>(`${BASE}/visits/${attendanceId}/check-out`, { method: "POST", authenticated: true });

export const markVisit = (attendanceId: string, action: "leave" | "customer-unavailable", note: string | null) =>
  apiFetch<MonthlyAttendanceItem>(`${BASE}/visits/${attendanceId}/${action}`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify({ note }),
  });

export const cancelLeave = (attendanceId: string) =>
  apiFetch<MonthlyAttendanceItem>(`${BASE}/visits/${attendanceId}/cancel-leave`, { method: "POST", authenticated: true });

/** Best-effort device position for the check-in record - never blocks check-in if denied or slow. */
export function currentPosition(timeoutMs = 4000): Promise<{ latitude: number; longitude: number } | null> {
  if (typeof navigator === "undefined" || !navigator.geolocation) return Promise.resolve(null);
  return new Promise((resolve) => {
    const timer = setTimeout(() => resolve(null), timeoutMs);
    navigator.geolocation.getCurrentPosition(
      (pos) => {
        clearTimeout(timer);
        resolve({
          latitude: Math.round(pos.coords.latitude * 1e6) / 1e6,
          longitude: Math.round(pos.coords.longitude * 1e6) / 1e6,
        });
      },
      () => {
        clearTimeout(timer);
        resolve(null);
      },
      { enableHighAccuracy: true, timeout: timeoutMs, maximumAge: 60_000 },
    );
  });
}
