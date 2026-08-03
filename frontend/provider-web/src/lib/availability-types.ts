/**
 * Response/request shapes for the Provider API's availability surface
 * (`/api/v1/availability`, docs/PROVIDER.md's `provider_availability` table):
 * weekly recurring windows plus ad hoc blackout dates.
 */

/** Mirrors the .NET BCL System.DayOfWeek enum's declaration order exactly (Sunday = 0), same convention admin-web's slots module uses. */
export enum DayOfWeek {
  Sunday = 0,
  Monday = 1,
  Tuesday = 2,
  Wednesday = 3,
  Thursday = 4,
  Friday = 5,
  Saturday = 6,
}

export const DAY_OF_WEEK_LABELS: Record<DayOfWeek, string> = {
  [DayOfWeek.Sunday]: "Sunday",
  [DayOfWeek.Monday]: "Monday",
  [DayOfWeek.Tuesday]: "Tuesday",
  [DayOfWeek.Wednesday]: "Wednesday",
  [DayOfWeek.Thursday]: "Thursday",
  [DayOfWeek.Friday]: "Friday",
  [DayOfWeek.Saturday]: "Saturday",
};

export const ALL_DAYS_OF_WEEK: readonly DayOfWeek[] = [
  DayOfWeek.Sunday,
  DayOfWeek.Monday,
  DayOfWeek.Tuesday,
  DayOfWeek.Wednesday,
  DayOfWeek.Thursday,
  DayOfWeek.Friday,
  DayOfWeek.Saturday,
];

export interface AvailabilityWindow {
  id: string;
  providerId: string;
  dayOfWeek: DayOfWeek;
  /** "HH:mm:ss" (TimeSpan over the wire). */
  startTime: string;
  /** "HH:mm:ss" (TimeSpan over the wire). */
  endTime: string;
  isActive: boolean;
}

export interface AvailabilityWindowInput {
  dayOfWeek: DayOfWeek;
  startTime: string;
  endTime: string;
}

/** Full replace, per the API contract - the whole weekly schedule is sent every time. */
export interface UpdateAvailabilityWindowsRequest {
  windows: AvailabilityWindowInput[];
}

export interface BlackoutDate {
  id: string;
  providerId: string;
  /** "YYYY-MM-DD" (DateOnly over the wire). */
  startDate: string;
  /** "YYYY-MM-DD" (DateOnly over the wire). */
  endDate: string;
  reason: string | null;
}

export interface CreateBlackoutDateRequest {
  startDate: string;
  endDate: string;
  reason?: string;
}

export interface AvailabilityResponse {
  windows: AvailabilityWindow[];
  blackoutDates: BlackoutDate[];
}
