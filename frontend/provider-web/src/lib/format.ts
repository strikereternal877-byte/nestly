/**
 * Display formatters shared by the provider screens.
 *
 * Money and timestamps were previously formatted ad hoc at every call site
 * (`₹${n.toFixed(2)}`, `new Date(s).toLocaleString()`), which drifted between
 * the ledger, the payout list and the payout detail for the same value.
 *
 * NOTE: nothing here produces a `YYYY-MM-DD` calendar date. That is
 * deliberate - use lib/date.ts for those. `toISOString().slice(0, 10)`
 * converts to UTC first and returns the previous day in IST before 05:30.
 */

const INR = new Intl.NumberFormat("en-IN", {
  style: "currency",
  currency: "INR",
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});

/**
 * Rupee amount with Indian digit grouping (₹1,23,456.00).
 *
 * Always pair the output with the `.nums` class wherever amounts stack into a
 * column, so the digits line up as the values change.
 */
export function formatInr(amount: number | null | undefined): string {
  if (amount === null || amount === undefined || Number.isNaN(amount)) return "—";
  return INR.format(amount);
}

/**
 * Signed rupee amount for ledger rows. A debit is rendered as a negative so a
 * penalty can never be mistaken for a credit of the same size.
 */
export function formatSignedInr(amount: number, isDebit: boolean): string {
  const magnitude = INR.format(Math.abs(amount));
  return isDebit ? `−${magnitude}` : `+${magnitude}`;
}

/** An instant as a short local date, e.g. "3 Aug 2026". */
export function formatDate(value: string | null | undefined): string {
  if (!value) return "—";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "—";
  return date.toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" });
}

/** An instant as a local date and time, e.g. "3 Aug 2026, 14:30". */
export function formatDateTime(value: string | null | undefined): string {
  if (!value) return "—";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "—";
  return date.toLocaleString(undefined, {
    day: "numeric",
    month: "short",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

/**
 * A `YYYY-MM-DD` calendar date (as the API sends it) rendered for humans.
 * Parsed as local parts rather than through `new Date(string)`, which treats a
 * bare date as UTC midnight and so shows the previous day in IST.
 */
export function formatIsoDate(value: string | null | undefined): string {
  if (!value) return "—";
  const [year, month, day] = value.split("-").map(Number);
  if (!year || !month || !day) return value;
  return new Date(year, month - 1, day).toLocaleDateString(undefined, {
    day: "numeric",
    month: "short",
    year: "numeric",
  });
}

/** "09:00:00" (a TimeSpan over the wire) as "09:00". */
export function formatTime(value: string | null | undefined): string {
  if (!value) return "—";
  return value.slice(0, 5);
}

const RELATIVE_UNITS: [Intl.RelativeTimeFormatUnit, number][] = [
  ["year", 60 * 60 * 24 * 365],
  ["month", 60 * 60 * 24 * 30],
  ["week", 60 * 60 * 24 * 7],
  ["day", 60 * 60 * 24],
  ["hour", 60 * 60],
  ["minute", 60],
];

const relativeTimeFormat = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });

/**
 * An instant as a short relative phrase, e.g. "3 days ago" - what a review
 * feed reads best with (docs/OPEN-FIXES-FEATURES.csv "Ratings and feedback").
 * Falls back to {@link formatDate} beyond a year, where "N years ago" stops
 * being more useful than the actual date, and for anything under a minute
 * ("just now" - reviews are never that fresh in practice, but an honest
 * floor beats a negative/zero relative value).
 */
export function formatRelativeDate(value: string | null | undefined): string {
  if (!value) return "—";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "—";

  const elapsedSeconds = Math.round((date.getTime() - Date.now()) / 1000);
  const absSeconds = Math.abs(elapsedSeconds);

  if (absSeconds < 60) return "Just now";

  for (const [unit, secondsPerUnit] of RELATIVE_UNITS) {
    if (absSeconds >= secondsPerUnit || unit === "minute") {
      const roundedValue = Math.round(elapsedSeconds / secondsPerUnit);
      return relativeTimeFormat.format(roundedValue, unit);
    }
  }

  return formatDate(value);
}
