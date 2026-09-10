/**
 * Typed client for the Provider API's earnings surface (`/api/v1/earnings`).
 * Every call is authenticated. The backend (task #148) is live; `isNotImplemented`
 * is kept as a defensive check for a 501 in case an older deployment still
 * has the stub.
 */
import { API_V1, apiFetch } from "./api";
import type {
  EarningLedgerEntry,
  EarningsSummary,
  JobEarningsResponse,
  PayoutDetail,
  PayoutListResponse,
  PayoutSummary,
} from "./earnings-types";

const EARNINGS_BASE = `${API_V1}/earnings`;

export const getEarningsSummary = () =>
  apiFetch<EarningsSummary>(`${EARNINGS_BASE}/summary`, { authenticated: true });

export const listEarningsLedger = () =>
  apiFetch<EarningLedgerEntry[]>(`${EARNINGS_BASE}/ledger`, { authenticated: true });

/**
 * GET /earnings/jobs - the per-job gross/commission/net/payout-status ledger.
 * `fromDate`/`toDate` are inclusive local `YYYY-MM-DD` calendar dates (see
 * `lib/date.ts`), omitted entirely for "all time" rather than sent empty.
 * `pageSize` defaults high enough to cover a typical filtered period in one
 * page - this screen does not (yet) build a page-by-page control, matching
 * `listPayouts`'s own scope.
 */
export const listJobEarnings = (params: {
  fromDate?: string;
  toDate?: string;
  page?: number;
  pageSize?: number;
} = {}) => {
  const query = new URLSearchParams();
  if (params.fromDate) query.set("fromDate", params.fromDate);
  if (params.toDate) query.set("toDate", params.toDate);
  query.set("page", String(params.page ?? 1));
  query.set("pageSize", String(params.pageSize ?? 100));
  return apiFetch<JobEarningsResponse>(`${EARNINGS_BASE}/jobs?${query.toString()}`, { authenticated: true });
};

export const listPayouts = async (): Promise<PayoutSummary[]> => {
  const response = await apiFetch<PayoutListResponse>(`${EARNINGS_BASE}/payouts`, { authenticated: true });
  return response.items;
};

export const getPayoutDetail = (payoutId: string) =>
  apiFetch<PayoutDetail>(`${EARNINGS_BASE}/payouts/${payoutId}`, { authenticated: true });
