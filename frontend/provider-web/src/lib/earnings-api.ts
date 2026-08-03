/**
 * Typed client for the Provider API's earnings surface (`/api/v1/earnings`).
 * Every call is authenticated. The backend (task #148) is live; `isNotImplemented`
 * is kept as a defensive check for a 501 in case an older deployment still
 * has the stub.
 */
import { API_V1, apiFetch } from "./api";
import type { EarningLedgerEntry, EarningsSummary, PayoutDetail, PayoutListResponse, PayoutSummary } from "./earnings-types";

const EARNINGS_BASE = `${API_V1}/earnings`;

export const getEarningsSummary = () =>
  apiFetch<EarningsSummary>(`${EARNINGS_BASE}/summary`, { authenticated: true });

export const listEarningsLedger = () =>
  apiFetch<EarningLedgerEntry[]>(`${EARNINGS_BASE}/ledger`, { authenticated: true });

export const listPayouts = async (): Promise<PayoutSummary[]> => {
  const response = await apiFetch<PayoutListResponse>(`${EARNINGS_BASE}/payouts`, { authenticated: true });
  return response.items;
};

export const getPayoutDetail = (payoutId: string) =>
  apiFetch<PayoutDetail>(`${EARNINGS_BASE}/payouts/${payoutId}`, { authenticated: true });
