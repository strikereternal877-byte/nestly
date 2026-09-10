/**
 * Types for the Admin API's payment transaction view (SRS 12.13.1, task
 * 311): `PaymentsController` (admin-api). Mirrors
 * `backend/shared/Application/Payments/AdminPaymentContracts.cs` field for
 * field. No `JsonStringEnumConverter` is registered anywhere in this
 * codebase, so `status` below serialises as its C# enum's ordinal - see
 * `bookings-types.ts`'s identical caveat, whose `PaymentTransactionStatus`/
 * `RefundMethod`/`RefundStatus`/`RefundType` enums this file reuses rather
 * than re-declaring (they already mirror the same backend enums this
 * surface reads).
 */
import type { BookingStatus } from "./types";
import { PaymentTransactionStatus, RefundMethod, RefundStatus, RefundType } from "./bookings-types";

export { PaymentTransactionStatus, RefundMethod, RefundStatus, RefundType };

/** Mirrors Nestly.Domain.PaymentAttemptStatus's declaration order exactly. */
export enum PaymentAttemptStatus {
  Created = 0,
  Success = 1,
  Failed = 2,
}

export interface AdminPaymentAttempt {
  id: string;
  attemptNumber: number;
  gatewayOrderId: string;
  gatewayPaymentRef: string | null;
  status: PaymentAttemptStatus;
  failureReason: string | null;
  createdAtUtc: string;
  completedAtUtc: string | null;
}

export interface AdminPaymentRefund {
  id: string;
  type: RefundType;
  method: RefundMethod;
  amount: number;
  status: RefundStatus;
  gatewayRefundRef: string | null;
  reason: string;
  createdAtUtc: string;
  processedAtUtc: string | null;
}

// ---- List ----

export interface AdminPaymentTransactionListItem {
  id: string;
  bookingId: string;
  customerId: string;
  amount: number;
  currency: string;
  status: PaymentTransactionStatus;
  latestGatewayOrderId: string | null;
  latestGatewayPaymentRef: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface PagedAdminPaymentTransactionResponse {
  items: AdminPaymentTransactionListItem[];
  totalCount: number;
  page: number;
  pageSize: number;
}

/** Query parameters for the admin payment transaction search endpoint. All optional. */
export interface AdminPaymentTransactionSearchParams {
  bookingId?: string;
  status?: PaymentTransactionStatus;
  fromUtc?: string;
  toUtc?: string;
  page?: number;
  pageSize?: number;
}

// ---- Detail ----

export interface AdminPaymentTransactionDetail {
  id: string;
  bookingId: string;
  customerId: string;
  amount: number;
  currency: string;
  status: PaymentTransactionStatus;
  attempts: AdminPaymentAttempt[];
  refunds: AdminPaymentRefund[];
  commissionRatePercentage: number | null;
  commissionAmount: number | null;
  createdAtUtc: string;
  updatedAtUtc: string;
}

// ---- Reconciliation (docs/OPEN-FIXES-FEATURES.csv "Payment reconciliation") ----

/** Mirrors Nestly.Application.Payments.PaymentReconciliationCategory's declaration order exactly. */
export enum PaymentReconciliationCategory {
  StuckPending = 0,
  Failed = 1,
  Orphaned = 2,
}

/**
 * One row of the `GET /admin/payments/reconciliation` queue: a booking
 * Awaiting Payment/Payment Failed joined against its transaction, if any -
 * `PaymentsController.GetReconciliation`. `paymentTransactionId`/
 * `transactionStatus` are null only for the "no transaction at all" flavour
 * of `Orphaned` (an abandoned checkout that never reached "create order").
 */
export interface AdminPaymentReconciliationItem {
  category: PaymentReconciliationCategory;
  bookingId: string;
  bookingReference: string;
  customerName: string;
  bookingStatus: BookingStatus;
  bookingStatusLabel: string;
  paymentTransactionId: string | null;
  transactionStatus: PaymentTransactionStatus | null;
  amount: number;
  currency: string;
  openSinceUtc: string;
  ageMinutes: number;
}

export interface AdminPaymentReconciliationResponse {
  items: AdminPaymentReconciliationItem[];
  totalCount: number;
  page: number;
  pageSize: number;
  stuckPendingCount: number;
  failedCount: number;
  orphanedCount: number;
}

/** Body of the void action - an optional admin-supplied reason. */
export interface AdminVoidPaymentTransactionRequest {
  reason?: string;
}
