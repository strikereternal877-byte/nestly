import { API_V1, ApiError, apiFetch } from "@/lib/api";
import { endOfLocalDayUtc, startOfLocalDayUtc } from "@/lib/day-range";

/**
 * Typed client for the admin Nestly Coins surface (task 202):
 * `GET/PUT /admin/nestly-coins/config/{audience}` and
 * `GET /admin/nestly-coins/reports/issued`.
 *
 * Lives in the module rather than `src/lib` because nothing else consumes it;
 * the shared clients there each back a whole SRS section.
 */

export enum NestlyCoinsAudience {
  Customer = 0,
  Provider = 1,
}

/** Tab strip for `ui.tsx`'s `Tabs`, which keys on a string value. */
export const AUDIENCE_TABS: readonly { value: string; label: string }[] = [
  { value: String(NestlyCoinsAudience.Customer), label: "Customer" },
  { value: String(NestlyCoinsAudience.Provider), label: "Provider" },
];

export interface NestlyCoinsProgramConfig {
  id: string;
  audience: NestlyCoinsAudience;
  earnRatePer100: number;
  minimumOrderAmount: number;
  requireReorder: boolean;
  maxCoinsPerMonth: number | null;
  expiryDays: number;
  clawbackWindowDays: number;
  isActive: boolean;
  updatedAtUtc: string;
}

export interface NestlyCoinsProgramConfigRequest {
  earnRatePer100: number;
  minimumOrderAmount: number;
  requireReorder: boolean;
  maxCoinsPerMonth: number | null;
  expiryDays: number;
  clawbackWindowDays: number;
  isActive: boolean;
}

export interface NestlyCoinsIssuedReport {
  audience: NestlyCoinsAudience;
  fromUtc: string;
  toUtc: string;
  totalIssued: number;
  totalClawedBack: number;
  netOutstanding: number;
}

const COINS_BASE = `${API_V1}/nestly-coins`;

/**
 * `null` rather than a thrown error when the audience has no config row.
 * "Not configured" is a normal state — tasks 200/201's services treat a
 * missing row as "coins disabled for this side" — so the screen shows the
 * create form pre-filled with defaults instead of an error.
 */
export async function getCoinsConfig(
  audience: NestlyCoinsAudience,
): Promise<NestlyCoinsProgramConfig | null> {
  try {
    return await apiFetch<NestlyCoinsProgramConfig>(`${COINS_BASE}/config/${audience}`, {
      authenticated: true,
    });
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) return null;
    throw err;
  }
}

export function upsertCoinsConfig(
  audience: NestlyCoinsAudience,
  request: NestlyCoinsProgramConfigRequest,
): Promise<NestlyCoinsProgramConfig> {
  return apiFetch<NestlyCoinsProgramConfig>(`${COINS_BASE}/config/${audience}`, {
    method: "PUT",
    authenticated: true,
    body: JSON.stringify(request),
  });
}

/**
 * Coins issued vs. clawed back over a *local calendar* window.
 *
 * The range used to be sent as the bare `yyyy-mm-dd` straight out of the date
 * input, unencoded, leaving the server to interpret a date-only string as an
 * instant. Both ends are now pinned to the admin's local day boundaries via
 * `lib/day-range`, and the whole query string is built with `URLSearchParams`.
 */
export function getCoinsIssuedReport(
  audience: NestlyCoinsAudience,
  fromDate: string,
  toDate: string,
): Promise<NestlyCoinsIssuedReport> {
  const params = new URLSearchParams({ audience: String(audience) });
  const fromUtc = startOfLocalDayUtc(fromDate);
  const toUtc = endOfLocalDayUtc(toDate);
  if (fromUtc) params.set("fromUtc", fromUtc);
  if (toUtc) params.set("toUtc", toUtc);

  return apiFetch<NestlyCoinsIssuedReport>(`${COINS_BASE}/reports/issued?${params.toString()}`, {
    authenticated: true,
  });
}
