/**
 * Typed client for the Admin API's booking-management surface (SRS 12.11,
 * 12.13.2-3; tasks 115a-117c): `BookingsController` - filterable search,
 * full detail/timeline, general status updates, and the cancel/reschedule/
 * refund actions. Every call is authenticated - these are admin-only
 * endpoints gated behind the "bookings" permission module server-side.
 */
import { API_V1, apiFetch } from "./api";
import type {
  AdminBookingDetail,
  AdminBookingSearchParams,
  AdminBookingSearchResponse,
  AdminBookingStatusUpdateRequest,
  AdminBookingTrackingResponse,
  AdminCancelBookingRequest,
  AdminFulfilmentBoardResponse,
  AdminManualPaymentRequest,
  AdminRefundRequest,
  AdminRescheduleBookingRequest,
  AdminUnassignedAtRiskBookingSearchResponse,
  BookingCompletionProofResponse,
  RescheduleCity,
  RescheduleLocality,
  RescheduleSlotAvailability,
} from "./bookings-types";

const BOOKINGS_BASE = `${API_V1}/bookings`;

// Parameter typed as `object` (not `Record<string, ...>`) so that named
// interfaces like AdminBookingSearchParams - which have no index signature
// of their own - can be passed in without a cast; matches coupon-api.ts's
// query() helper.
function query(params: object): string {
  const entries = Object.entries(params as Record<string, string | number | boolean | undefined>)
    .filter(([, value]) => value !== undefined);
  if (entries.length === 0) return "";
  return `?${new URLSearchParams(entries.map(([key, value]) => [key, String(value)])).toString()}`;
}

export const searchBookings = (params: AdminBookingSearchParams) =>
  apiFetch<AdminBookingSearchResponse>(`${BOOKINGS_BASE}${query(params)}`, { authenticated: true });

export const getBookingDetail = (bookingId: string) =>
  apiFetch<AdminBookingDetail>(`${BOOKINGS_BASE}/${bookingId}`, { authenticated: true });

export const updateBookingStatus = (bookingId: string, request: AdminBookingStatusUpdateRequest) =>
  apiFetch<AdminBookingDetail>(`${BOOKINGS_BASE}/${bookingId}/status`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const cancelBooking = (bookingId: string, request: AdminCancelBookingRequest) =>
  apiFetch<AdminBookingDetail>(`${BOOKINGS_BASE}/${bookingId}/cancel`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const rescheduleBooking = (bookingId: string, request: AdminRescheduleBookingRequest) =>
  apiFetch<AdminBookingDetail>(`${BOOKINGS_BASE}/${bookingId}/reschedule`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const refundBooking = (bookingId: string, request: AdminRefundRequest) =>
  apiFetch<AdminBookingDetail>(`${BOOKINGS_BASE}/${bookingId}/refund`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

/** Row 25, docs/OPEN-FIXES-FEATURES.csv - records a manual/offline payment and confirms the booking. */
export const recordManualPayment = (bookingId: string, request: AdminManualPaymentRequest) =>
  apiFetch<AdminBookingDetail>(`${BOOKINGS_BASE}/${bookingId}/manual-payment`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

// ---- Reschedule pickers (row 26, docs/OPEN-FIXES-FEATURES.csv) ----

/** Active cities for the reschedule panel's locality picker. */
export const getRescheduleCities = () =>
  apiFetch<RescheduleCity[]>(`${BOOKINGS_BASE}/reschedule-cities`, { authenticated: true });

/** Localities matching a name/pincode search within a city. */
export const searchRescheduleLocalities = (cityId: string, search: string) =>
  apiFetch<RescheduleLocality[]>(
    `${BOOKINGS_BASE}/reschedule-localities${query({ cityId, search: search || undefined })}`,
    { authenticated: true },
  );

/** Available slot windows for this booking's service, at a locality, on a date. */
export const getRescheduleSlots = (bookingId: string, localityId: string, date: string) =>
  apiFetch<RescheduleSlotAvailability>(
    `${BOOKINGS_BASE}/${bookingId}/reschedule-slots${query({ localityId, date })}`,
    { authenticated: true },
  );

/**
 * Completion proof (photos + checklist) for a booking, if any (tasks 195-198
 * dispute review). Normalised to `null` rather than letting apiFetch's
 * `undefined` leak out: the endpoint answers 204 until the provider has
 * submitted proof, and React Query rejects an `undefined` resolution
 * ("Query data cannot be undefined"), which put the proof card into an error
 * state on every booking that simply has no proof yet.
 */
export const getBookingCompletionProof = async (
  bookingId: string,
): Promise<BookingCompletionProofResponse | null> => {
  const result = await apiFetch<BookingCompletionProofResponse | undefined>(
    `${BOOKINGS_BASE}/${bookingId}/completion-proof`,
    { authenticated: true },
  );
  return result ?? null;
};

/** Live tracking snapshot for the ops view (task 284). Rejects with a 404 ApiError - see AdminBookingTrackingResponse's doc comment - when there is no live data to show; the caller renders that as a plain state, not an error. */
export const getBookingTracking = (bookingId: string) =>
  apiFetch<AdminBookingTrackingResponse>(`${BOOKINGS_BASE}/${bookingId}/tracking`, { authenticated: true });

/**
 * Row "Unassigned and at-risk queue", docs/OPEN-FIXES-FEATURES.csv: paid
 * bookings with no live provider, soonest slot first.
 */
export const getUnassignedAtRiskBookings = (page: number, pageSize: number) =>
  apiFetch<AdminUnassignedAtRiskBookingSearchResponse>(
    `${BOOKINGS_BASE}/unassigned-at-risk${query({ page, pageSize })}`,
    { authenticated: true },
  );

/**
 * Row "Fulfilment control room", docs/OPEN-FIXES-FEATURES.csv: every
 * operationally live booking for one calendar day, flat - the /fulfilment
 * page buckets these into status columns itself. `date` is a local
 * "yyyy-MM-dd" (see lib/date.ts); omit it to let the server default to today.
 */
export const getFulfilmentBoard = (date?: string) =>
  apiFetch<AdminFulfilmentBoardResponse>(
    `${BOOKINGS_BASE}/fulfilment-board${query({ date })}`,
    { authenticated: true },
  );
