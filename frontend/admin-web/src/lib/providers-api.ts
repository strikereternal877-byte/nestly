/**
 * Typed client for the Admin API's provider-management surface (PROVIDER.md;
 * tasks 147, 148, 150a-c, 160): `ProvidersController` - CRUD, KYC approval,
 * background check/activation, performance and earnings - plus
 * `PayoutsController`. Every call is authenticated - these are admin-only
 * endpoints gated behind the "provider"/"payout" permission modules
 * server-side.
 */
import { API_V1, apiFetch } from "./api";
import type {
  AssignProviderRequest,
  BookingProviderAssignment,
  CreateProviderPayoutRequest,
  CreateProviderRequest,
  EligibleProvider,
  ProviderCapacity,
  ProviderDetail,
  ProviderEarningsSummary,
  ProviderKycDocument,
  ProviderPayout,
  ProviderPhoto,
  ProviderPayoutSearchResponse,
  ProviderPerformance,
  ProviderPerformanceListParams,
  ProviderPerformanceListResponse,
  ProviderSearchParams,
  ProviderSearchResponse,
  ProviderBackgroundCheck,
  ProviderPayoutStatus,
  RecordBackgroundCheckRequest,
  RecordProviderEarningAdjustmentRequest,
  RejectAssignmentRequest,
  RejectProviderKycDocumentRequest,
  RejectProviderPhotoRequest,
  SetProviderCapacityRequest,
  SuspendProviderRequest,
  UpdateProviderPayoutStatusRequest,
  UpdateProviderRequest,
} from "./providers-types";

const PROVIDERS_BASE = `${API_V1}/providers`;
const PAYOUTS_BASE = `${API_V1}/payouts`;
const BOOKINGS_BASE = `${API_V1}/bookings`;

// Mirrors bookings-api.ts's query() helper.
function query(params: object): string {
  const entries = Object.entries(params as Record<string, string | number | boolean | undefined>)
    .filter(([, value]) => value !== undefined);
  if (entries.length === 0) return "";
  return `?${new URLSearchParams(entries.map(([key, value]) => [key, String(value)])).toString()}`;
}

// ---- CRUD (task 150a) ----

export const searchProviders = (params: ProviderSearchParams) =>
  apiFetch<ProviderSearchResponse>(`${PROVIDERS_BASE}${query(params)}`, { authenticated: true });

export const getProviderDetail = (providerId: string) =>
  apiFetch<ProviderDetail>(`${PROVIDERS_BASE}/${providerId}`, { authenticated: true });

export const createProvider = (request: CreateProviderRequest) =>
  apiFetch<ProviderDetail>(PROVIDERS_BASE, { method: "POST", authenticated: true, body: JSON.stringify(request) });

export const updateProvider = (providerId: string, request: UpdateProviderRequest) =>
  apiFetch<ProviderDetail>(`${PROVIDERS_BASE}/${providerId}`, {
    method: "PUT",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const suspendProvider = (providerId: string, request: SuspendProviderRequest) =>
  apiFetch<ProviderDetail>(`${PROVIDERS_BASE}/${providerId}/suspend`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const reactivateProvider = (providerId: string) =>
  apiFetch<ProviderDetail>(`${PROVIDERS_BASE}/${providerId}/reactivate`, { method: "POST", authenticated: true });

// ---- KYC approval, background check, activation (task 150b, 160) ----

export const approveKycDocument = (documentId: string) =>
  apiFetch<ProviderKycDocument>(`${PROVIDERS_BASE}/kyc-documents/${documentId}/approve`, {
    method: "POST",
    authenticated: true,
  });

export const rejectKycDocument = (documentId: string, request: RejectProviderKycDocumentRequest) =>
  apiFetch<ProviderKycDocument>(`${PROVIDERS_BASE}/kyc-documents/${documentId}/reject`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const recordBackgroundCheck = (providerId: string, request: RecordBackgroundCheckRequest) =>
  apiFetch<ProviderBackgroundCheck>(`${PROVIDERS_BASE}/${providerId}/background-check`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const activateProvider = (providerId: string) =>
  apiFetch<ProviderDetail>(`${PROVIDERS_BASE}/${providerId}/activate`, { method: "POST", authenticated: true });

// ---- Profile photo moderation (task 293) ----

/** The moderation queue: every provider whose photo is awaiting a verdict. */
export const listPendingProviderPhotos = () =>
  apiFetch<ProviderPhoto[]>(`${PROVIDERS_BASE}/photo-moderation/pending`, { authenticated: true });

/** Approving is the only thing that makes a photo visible to customers. */
export const approveProviderPhoto = (providerId: string) =>
  apiFetch<ProviderPhoto>(`${PROVIDERS_BASE}/${providerId}/photo/approve`, {
    method: "POST",
    authenticated: true,
  });

export const rejectProviderPhoto = (providerId: string, request: RejectProviderPhotoRequest) =>
  apiFetch<ProviderPhoto>(`${PROVIDERS_BASE}/${providerId}/photo/reject`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

// ---- Capacity limits (task 245 built enforcement; task 308 adds this write path) ----

export const getProviderCapacity = (providerId: string) =>
  apiFetch<ProviderCapacity>(`${PROVIDERS_BASE}/${providerId}/capacity`, { authenticated: true });

export const setProviderCapacity = (providerId: string, request: SetProviderCapacityRequest) =>
  apiFetch<ProviderCapacity>(`${PROVIDERS_BASE}/${providerId}/capacity`, {
    method: "PUT",
    authenticated: true,
    body: JSON.stringify(request),
  });

// ---- Performance and earnings (task 150c, 148) ----

export const getProviderPerformance = (providerId: string) =>
  apiFetch<ProviderPerformance>(`${PROVIDERS_BASE}/${providerId}/performance`, { authenticated: true });

/** The provider-performance ranking list (docs/OPEN-FIXES-FEATURES.csv "Provider performance"). */
export const listProviderPerformance = (params: ProviderPerformanceListParams) =>
  apiFetch<ProviderPerformanceListResponse>(`${PROVIDERS_BASE}/performance${query(params)}`, { authenticated: true });

export const getProviderEarnings = (providerId: string) =>
  apiFetch<ProviderEarningsSummary>(`${PROVIDERS_BASE}/${providerId}/earnings`, { authenticated: true });

export const recordEarningAdjustment = (providerId: string, request: RecordProviderEarningAdjustmentRequest) =>
  apiFetch<ProviderEarningsSummary>(`${PROVIDERS_BASE}/${providerId}/earnings`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

// ---- Payouts (task 148) ----

export const searchPayouts = (providerId: string, status?: ProviderPayoutStatus) =>
  apiFetch<ProviderPayoutSearchResponse>(`${PAYOUTS_BASE}${query({ providerId, status })}`, { authenticated: true });

export const createPayoutBatch = (providerId: string, request: CreateProviderPayoutRequest) =>
  apiFetch<ProviderPayout>(`${PAYOUTS_BASE}/providers/${providerId}`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const updatePayoutStatus = (payoutId: string, request: UpdateProviderPayoutStatusRequest) =>
  apiFetch<ProviderPayout>(`${PAYOUTS_BASE}/${payoutId}/status`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

// ---- Booking assignment (task 147, 159 - used from the booking detail screen) ----

export const assignProviderToBooking = (bookingId: string, request: AssignProviderRequest) =>
  apiFetch<BookingProviderAssignment>(`${BOOKINGS_BASE}/${bookingId}/assign-provider`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const rejectBookingAssignment = (bookingId: string, request: RejectAssignmentRequest) =>
  apiFetch<BookingProviderAssignment>(`${BOOKINGS_BASE}/${bookingId}/reject-assignment`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const getBookingAssignmentHistory = (bookingId: string) =>
  apiFetch<BookingProviderAssignment[]>(`${BOOKINGS_BASE}/${bookingId}/assignments`, { authenticated: true });

export const getEligibleProviders = (bookingId: string) =>
  apiFetch<EligibleProvider[]>(`${BOOKINGS_BASE}/${bookingId}/eligible-providers`, { authenticated: true });
