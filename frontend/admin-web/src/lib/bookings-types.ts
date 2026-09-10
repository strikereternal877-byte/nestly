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

/** Mirrors Nestly.Domain.ManualPaymentMethod's declaration order exactly. */
export enum ManualPaymentMethod {
  Cash = 0,
  Upi = 1,
  BankTransfer = 2,
  Other = 3,
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
  /** Short human-facing code ("GLX-260825-K7F3M") - what to show/search on instead of `id`. */
  reference: string;
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
  /** Short human-facing code ("GLX-260825-K7F3M") or any substring of one - matches server-side against `Booking.BookingReference`. */
  reference?: string;
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
  /** Short human-facing code ("GLX-260825-K7F3M") - what to show/search on instead of `id`. */
  reference: string;
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

/** Row 25, docs/OPEN-FIXES-FEATURES.csv - mirrors Nestly.Application.BookingManagement.AdminManualPaymentRequest. */
export interface AdminManualPaymentRequest {
  method: ManualPaymentMethod;
  reference: string;
}

// ---- Reschedule pickers (row 26, docs/OPEN-FIXES-FEATURES.csv) ----
// Mirrors the shapes customer-web's LocalitySelector/SlotPicker already
// consume (Nestly.Application.Geography/Slots), served here via
// BookingsController's reschedule-cities/reschedule-localities/reschedule-slots
// actions so the admin reschedule panel needs no hand-typed UUIDs.

export interface RescheduleCity {
  id: string;
  name: string;
  stateName: string;
}

export interface RescheduleLocality {
  id: string;
  name: string;
  zoneName: string;
  pincodeCode: string;
  pincodeId: string;
}

/** Mirrors Nestly.Application.Slots.SlotUnavailabilityReason's declaration order exactly. */
export enum SlotUnavailabilityReason {
  None = 0,
  NotServiceable = 1,
  DateOutOfBookableRange = 2,
  Blackout = 3,
  NoWindowsConfigured = 4,
  CutoffPassed = 5,
  FullyBooked = 6,
}

export interface RescheduleSlotOption {
  slotWindowId: string;
  name: string;
  /** .NET TimeSpan serialises as "hh:mm:ss". */
  startTime: string;
  endTime: string;
  maxBookingsPerSlot: number | null;
}

export interface RescheduleSlotAvailability {
  isServiceable: boolean;
  slots: RescheduleSlotOption[];
  reason: SlotUnavailabilityReason;
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

/**
 * Live tracking snapshot for the ops view (task 284) - mirrors
 * Nestly.Application.Tracking.BookingTrackingContracts.cs field for field,
 * the same shape task 275/281 already ported into customer-web's types.ts.
 * A 404 (Booking.NotFound or Booking.TrackingUnavailable) means "no live
 * data" - see the tracking card's handling in the booking detail page.
 */
export interface AdminTrackedProviderSummary {
  displayName: string;
  photoUrl: string | null;
  rating: number | null;
  maskedPhone: string | null;
}

export interface AdminTrackedLocation {
  latitude: number;
  longitude: number;
  recordedAtUtc: string;
}

export interface AdminTrackedEta {
  etaSeconds: number;
  etaComputedAtUtc: string;
}

export interface AdminTrackedDestination {
  latitude: number;
  longitude: number;
}

export interface AdminBookingTrackingResponse {
  bookingId: string;
  status: BookingStatus;
  statusLabel: string;
  provider: AdminTrackedProviderSummary | null;
  providerLocation: AdminTrackedLocation | null;
  eta: AdminTrackedEta | null;
  destination: AdminTrackedDestination;
}

// ---- Unassigned & at-risk queue (docs/OPEN-FIXES-FEATURES.csv "Admin Web,
// Proposed new page, Unassigned and at-risk queue") - mirrors
// Nestly.Application.BookingManagement.AdminUnassignedAtRiskBookingResponse. ----

/**
 * One row of the queue: a paid booking with no live provider yet. `slotDate`/
 * `slotStartTime` come back raw (not a precomputed countdown) so this page
 * can format and refresh "time until slot" itself, same as every other
 * slot display in admin-web.
 */
export interface AdminUnassignedAtRiskBooking {
  id: string;
  reference: string;
  customerName: string;
  serviceName: string;
  slotDate: string;
  /** .NET TimeSpan serialises as "hh:mm:ss". */
  slotStartTime: string;
  city: string;
  pincode: string;
  status: BookingStatus;
  statusLabel: string;
  createdAtUtc: string;
}

export interface AdminUnassignedAtRiskBookingSearchResponse {
  items: AdminUnassignedAtRiskBooking[];
  totalCount: number;
  page: number;
  pageSize: number;
}

// ---- Fulfilment control room (docs/OPEN-FIXES-FEATURES.csv "Admin Web,
// Proposed new page, Fulfilment control room") - mirrors
// Nestly.Application.BookingManagement.AdminFulfilmentBoardBookingResponse.
// A flat list for one day; the /fulfilment page buckets these into status
// columns and layers its own "overdue/at-risk" read on top, same reasoning as
// AdminUnassignedAtRiskBooking above. ----

/** One board card. `slotDate`/`slotStartTime` come back raw, same as {@link AdminUnassignedAtRiskBooking}. */
export interface AdminFulfilmentBoardBooking {
  id: string;
  reference: string;
  customerName: string;
  serviceName: string;
  slotDate: string;
  /** .NET TimeSpan serialises as "hh:mm:ss". */
  slotStartTime: string;
  city: string;
  pincode: string;
  status: BookingStatus;
  statusLabel: string;
  assignedProviderId: string | null;
  assignedProviderName: string | null;
  createdAtUtc: string;
}

export interface AdminFulfilmentBoardResponse {
  /** The calendar date the board was built for, echoed back (.NET DateOnly, "yyyy-MM-dd"). */
  date: string;
  items: AdminFulfilmentBoardBooking[];
}
