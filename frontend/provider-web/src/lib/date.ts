/**
 * Local calendar-date helpers.
 *
 * Every date the API takes as `YYYY-MM-DD` means a *calendar day*, not an
 * instant. `new Date().toISOString().slice(0, 10)` is the obvious way to
 * produce one and is wrong everywhere east of Greenwich: toISOString converts
 * to UTC first, so in IST (UTC+05:30) any local time before 05:30 yields the
 * previous day. That shipped as a real defect in the slot picker, the booking
 * summary, the reschedule screen and the recurring-booking form — each
 * defaulted a customer to yesterday's date for the first five and a half
 * hours of every day.
 *
 * Use these instead of touching toISOString for anything calendar-shaped.
 */

/** A `Date`'s local calendar date as `YYYY-MM-DD`. */
export function toLocalIsoDate(date: Date): string {
  const year = date.getFullYear();
  const month = `${date.getMonth() + 1}`.padStart(2, "0");
  const day = `${date.getDate()}`.padStart(2, "0");
  return `${year}-${month}-${day}`;
}

/** Today, as a local `YYYY-MM-DD` calendar date. */
export function todayIsoDate(): string {
  return toLocalIsoDate(new Date());
}

/**
 * `offsetDays` from today as a local `YYYY-MM-DD`. Negative goes backwards.
 *
 * Uses `setDate`, which handles month and year rollover and — unlike adding
 * milliseconds — stays correct across a daylight-saving transition, where a
 * day is not always 24 hours.
 */
export function isoDateOffsetFromToday(offsetDays: number): string {
  const date = new Date();
  date.setDate(date.getDate() + offsetDays);
  return toLocalIsoDate(date);
}
