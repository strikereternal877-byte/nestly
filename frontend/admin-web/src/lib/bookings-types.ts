/**
 * Admin booking management shapes (SRS 12.11, 12.13.2-3; tasks 115a-117c)
 * mirror the C# records in Nestly.Application.BookingManagement
 * (BookingManagementContracts.cs) - see BookingsController. AdminApi has no
 * JsonStringEnumConverter registered (same caveat as types.ts's BookingStatus),
 * so every enum below serialises over the wire as its ordinal and must stay
 * in declaration-order sync with its C# source.
 */
import type { BookingStatus } from "./types";

/** Mirrors Nestly.Domain.CancellationActor's declaration order exactly. */
export enum CancellationActor {
  Customer = 0,
  Admin = 1,
  System = 2,
}

/** Mirrors Nestly.Domain.RescheduleActor's declaration order exactly. */
export enum RescheduleActor {
  Customer = 0,
  Admin = 1,
  System = 2,
}

/** Mirrors Nestly.Domain.RefundMethod's declaration order exactly. */
export enum RefundMethod {
  Gateway = 0,
  Wallet = 1,
}

/** Mirrors Nestly.Domain.RefundStatus's declaration order exactly. */
export enum RefundStatus {
  Initiated = 0,
  Processing = 1,
  Refunded = 2,
  Failed = 3,
}

/** Mirrors Nestly.Domain.RefundType's declaration order exactly. */
export enum RefundType {
  Full = 0,
  Partial = 1,
}

/** Mirrors Nestly.Domain.PaymentTransactionStatus's declaration order exactly. */
export enum PaymentTransactionStatus {
  Pending = 0,
  Success = 1,
  Failed = 2,
  Cancelled = 3,
}

// ---- List (SRS 12.11.1) ----

export interface AdminBookingListItem {
  id: string;
  customerName: string;
  customerMobile: string;
  serviceName: string;
  city: string;
  slotDate: string;
  status: BookingStatus;
  statusLabel: string;
  totalPayable: number;
  couponCode: string | null;
  createdAtUtc: string;
}

export interface AdminBookingSearchResponse {
  items: AdminBookingListItem[];
  totalCount: number;
  page: number;
  pageSize: number;
}

/** Query parameters for the admin booking search endpoint (SRS 12.11.1, task 115a). All optional. */
export interface AdminBookingSearchParams {
  bookingId?: string;
  customerName?: string;
  customerMobile?: string;
  status?: BookingStatus;
  city?: string;
  slotDateFrom?: string;
  slotDateTo?: string;
  createdFromUtc?: string;
  createdToUtc?: string;
  serviceId?: string;
  categoryId?: string;
  couponCode?: string;
  page?: number;
  pageSize?: number;
}

// ---- Detail (SRS 12.11.2) ----

export interface AdminBookingCustomerSnapshot {
  customerId: string;
  name: string;
  mobile: string;
}

export interface AdminBookingAddressSnapshot {
  label: string;
  line1: string;
  line2: string | null;
  landmark: string | null;
  pincode: string;
  city: string;
  state: string;
  contactName: string;
  contactMobile: string;
}

export interface AdminBookingSlotSnapshot {
  slotWindowId: string;
  date: string;
  windowName: string;
  startTime: string;
  endTime: string;
}

export interface AdminBookingAddOn {
  id: string;
  serviceAddOnId: string;
  name: string;
  unitPrice: number;
  quantity: number;
  lineTotal: number;
}

export interface AdminBookingItem {
  id: string;
  serviceId: string;
  name: string;
  unitPrice: number;
  quantity: number;
  lineTotal: number;
  addOns: AdminBookingAddOn[];
}

export interface AdminBookingPrice {
  basePrice: number;
  quantity: number;
  baseTotal: number;
  addOnTotal: number;
  visitCharge: number;
  subtotal: number;
  taxPercentage: number;
  taxAmount: number;
  platformFee: number;
  totalPayable: number;
  couponCode: string | null;
  couponDiscountAmount: number | null;
  finalPayable: number;
}

export interface AdminBookingStatusTimelineEntry {
  fromStatus: BookingStatus | null;
  toStatus: BookingStatus;
  toStatusLabel: string;
  reason: string | null;
  changedAtUtc: string;
}

export interface AdminBookingPaymentSummary {
  id: string;
  status: PaymentTransactionStatus;
  amount: number;
  currency: string;
  gatewayPaymentRef: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface AdminBookingCancellation {
  id: string;
  actor: CancellationActor;
  reason: string;
  withinFreeCancellationWindow: boolean;
  cancellationFeeAmount: number;
  refundAmount: number;
  refundMethod: RefundMethod | null;
  refundTransactionId: string | null;
  internalNotes: string | null;
  createdAtUtc: string;
}

export interface AdminBookingReschedule {
  id: string;
  actor: RescheduleActor;
  reason: string | null;
  fromSlotDate: string;
  fromSlotStartTime: string;
  toSlotDate: string;
  toSlotStartTime: string;
  isLate: boolean;
  feeAmount: number;
  createdAtUtc: string;
}

export interface AdminBookingRefund {
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

export interface AdminBookingDetail {
  id: string;
  customer: AdminBookingCustomerSnapshot;
  address: AdminBookingAddressSnapshot;
  slot: AdminBookingSlotSnapshot;
  items: AdminBookingItem[];
  price: AdminBookingPrice;
  status: BookingStatus;
  statusLabel: string;
  timeline: AdminBookingStatusTimelineEntry[];
  payment: AdminBookingPaymentSummary | null;
  cancellation: AdminBookingCancellation | null;
  reschedules: AdminBookingReschedule[];
  refunds: AdminBookingRefund[];
  createdAtUtc: string;
}

// ---- Actions (SRS 12.11.3, tasks 115d, 117a-c) ----

export interface AdminBookingStatusUpdateRequest {
  newStatus: BookingStatus;
  reason?: string;
}

export interface AdminCancelBookingRequest {
  reason: string;
  internalNotes?: string;
}

export interface AdminRescheduleBookingRequest {
  localityId: string;
  slotWindowId: string;
  slotDate: string;
  reason?: string;
}

export interface AdminRefundRequest {
  isFullRefund: boolean;
  amount?: number;
  reason: string;
  method: RefundMethod;
}

/**
 * Completion proof shapes mirror the C# records in Nestly.Application.Bookings
 * (BookingCompletionProofContracts.cs) - tasks 195-198 dispute-review evidence.
 */
export interface CompletionChecklistAnswerResponse {
  item: string;
  completed: boolean;
  notes: string | null;
}

export interface BookingCompletionProofResponse {
  id: string;
  bookingId: string;
  photoRefs: string[];
  checklistAnswers: CompletionChecklistAnswerResponse[];
  submittedByProviderId: string;
  submittedAtUtc: string;
}
