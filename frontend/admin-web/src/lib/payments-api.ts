/**
 * Typed client for the Admin API's payment transaction view (SRS 12.13.1,
 * task 311) and payment reconciliation queue (docs/OPEN-FIXES-FEATURES.csv
 * "Payment reconciliation"): `PaymentsController`. Read-only except for
 * `voidPaymentTransaction` - gated server-side behind the "payments"
 * permission module ("payments.read" for every GET, "payments.write" for
 * the void action).
 */
import { API_V1, apiFetch } from "./api";
import type {
  AdminPaymentReconciliationResponse,
  AdminPaymentTransactionDetail,
  AdminPaymentTransactionListItem,
  AdminPaymentTransactionSearchParams,
  AdminVoidPaymentTransactionRequest,
  PagedAdminPaymentTransactionResponse,
} from "./payments-types";

const PAYMENTS_BASE = `${API_V1}/payments`;

// Same `object`-typed query() helper as bookings-api.ts/coupon-api.ts, so a
// named params interface can be passed without a cast.
function query(params: object): string {
  const entries = Object.entries(params as Record<string, string | number | boolean | undefined>)
    .filter(([, value]) => value !== undefined);
  if (entries.length === 0) return "";
  return `?${new URLSearchParams(entries.map(([key, value]) => [key, String(value)])).toString()}`;
}

export const searchPaymentTransactions = (params: AdminPaymentTransactionSearchParams) =>
  apiFetch<PagedAdminPaymentTransactionResponse>(`${PAYMENTS_BASE}${query(params)}`, { authenticated: true });

export const getPaymentTransactionDetail = (transactionId: string) =>
  apiFetch<AdminPaymentTransactionDetail>(`${PAYMENTS_BASE}/${transactionId}`, { authenticated: true });

export const getPaymentReconciliation = (page: number, pageSize: number) =>
  apiFetch<AdminPaymentReconciliationResponse>(`${PAYMENTS_BASE}/reconciliation${query({ page, pageSize })}`, {
    authenticated: true,
  });

export const voidPaymentTransaction = (transactionId: string, request: AdminVoidPaymentTransactionRequest) =>
  apiFetch<AdminPaymentTransactionListItem>(`${PAYMENTS_BASE}/${transactionId}/void`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });
