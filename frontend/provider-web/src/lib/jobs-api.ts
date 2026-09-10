/**
 * Typed client for the Provider API's jobs surface (`/api/v1/jobs`). Every
 * call is authenticated. The backend (task #147) is live; `isNotImplemented`
 * is kept as a defensive check for a 501 in case an older deployment still
 * has the stub.
 */
import { API_V1, apiFetch, apiUpload } from "./api";
import type {
  BookingCompletionProofResponse,
  CustomerRatingEligibility,
  CustomerRatingResponse,
  JobDetail,
  JobListItem,
  JobListParams,
  JobListResponse,
  RecordProviderLocationRequest,
  RecordProviderLocationResponse,
  SubmitCompletionProofRequest,
  SubmitCompletionVerificationRequest,
  SubmitCustomerRatingRequest,
  UploadCompletionPhotoResponse,
} from "./jobs-types";

const JOBS_BASE = `${API_V1}/jobs`;

function query(params: JobListParams): string {
  const entries = Object.entries(params).filter(([, value]) => value !== undefined && value !== "");
  if (entries.length === 0) return "";
  return `?${new URLSearchParams(entries as [string, string][]).toString()}`;
}

export const listJobs = async (params: JobListParams): Promise<JobListItem[]> => {
  const response = await apiFetch<JobListResponse>(`${JOBS_BASE}${query(params)}`, { authenticated: true });
  return response.items;
};

export const getJobDetail = (jobId: string) =>
  apiFetch<JobDetail>(`${JOBS_BASE}/${jobId}`, { authenticated: true });

export const acceptJob = (jobId: string) =>
  apiFetch<JobDetail>(`${JOBS_BASE}/${jobId}/accept`, { method: "POST", authenticated: true });

/**
 * Row 38, docs/OPEN-FIXES-FEATURES.csv: called after an accept attempt fails
 * for a reason that was not the provider's fault (a transient server error,
 * not the 401/session-expiry case `apiFetch` already retries transparently),
 * so the response window is pushed out instead of silently lost while the
 * provider retries.
 */
export const extendJobResponseDeadline = (jobId: string) =>
  apiFetch<JobDetail>(`${JOBS_BASE}/${jobId}/extend-response-deadline`, { method: "POST", authenticated: true });

export const rejectJob = (jobId: string) =>
  apiFetch<JobDetail>(`${JOBS_BASE}/${jobId}/reject`, { method: "POST", authenticated: true });

export const startJob = (jobId: string) =>
  apiFetch<JobDetail>(`${JOBS_BASE}/${jobId}/start`, { method: "POST", authenticated: true });

/** Optional, task 270/283 - Start still works straight from Accepted, so tapping this is never required. */
export const markJobEnRoute = (jobId: string) =>
  apiFetch<JobDetail>(`${JOBS_BASE}/${jobId}/en-route`, { method: "POST", authenticated: true });

/** Optional, same as markJobEnRoute - a provider can tap Start without ever marking Arrived. */
export const markJobArrived = (jobId: string) =>
  apiFetch<JobDetail>(`${JOBS_BASE}/${jobId}/arrived`, { method: "POST", authenticated: true });

export const completeJob = (jobId: string) =>
  apiFetch<JobDetail>(`${JOBS_BASE}/${jobId}/complete`, { method: "POST", authenticated: true });

export const submitCompletionProof = (jobId: string, request: SubmitCompletionProofRequest) =>
  apiFetch<JobDetail>(`${JOBS_BASE}/${jobId}/completion-proof`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

/** The photos+checklist evidence required before `completeJob` will succeed (tasks 195-197). */
export const submitCompletionVerification = (jobId: string, request: SubmitCompletionVerificationRequest) =>
  apiFetch<BookingCompletionProofResponse>(`${JOBS_BASE}/${jobId}/completion-verification`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

/**
 * 204 No Content ("nothing submitted yet") is the expected shape for a job
 * that has no completion verification on file. `apiFetch` resolves a 204 as
 * `undefined`, but TanStack Query v5 treats `undefined` as "no data" and
 * logs a dev warning every time a queryFn resolves it - so this coalesces
 * to `null`, an explicit "confirmed absent" value React Query is happy to
 * cache, instead of letting the generic apiFetch behavior leak through.
 */
export const getCompletionVerification = async (
  jobId: string,
): Promise<BookingCompletionProofResponse | null> => {
  const result = await apiFetch<BookingCompletionProofResponse | undefined>(
    `${JOBS_BASE}/${jobId}/completion-verification`,
    { authenticated: true },
  );
  return result ?? null;
};

/**
 * Uploads one camera/gallery photo for completion verification and returns
 * its ref (an absolute URL) - feed the result straight into
 * `submitCompletionVerification`'s `photoRefs`.
 */
export const uploadCompletionPhoto = (jobId: string, file: File) => {
  const formData = new FormData();
  formData.append("file", file);
  return apiUpload<UploadCompletionPhotoResponse>(`${JOBS_BASE}/${jobId}/completion-photos`, formData, {
    authenticated: true,
  });
};

/**
 * One location fix (task 269/282). The server throttles independently of
 * this client (`ProviderLocationIngestOptions`, ~15s minimum interval) and
 * answers 202 rather than 200 for a fix it accepted-but-dropped as too soon -
 * both are a successful `apiFetch` call, not a thrown error, so a throttled
 * ping never surfaces as a failure to the provider.
 */
export const recordProviderLocation = (jobId: string, request: RecordProviderLocationRequest) =>
  apiFetch<RecordProviderLocationResponse>(`${JOBS_BASE}/${jobId}/location`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

/** Bidirectional reviews: whether this job can be rated right now. */
export const getCustomerRatingEligibility = (jobId: string) =>
  apiFetch<CustomerRatingEligibility>(`${JOBS_BASE}/${jobId}/customer-rating/eligibility`, { authenticated: true });

/**
 * The rating already submitted for this job, if any - a 204 means none yet.
 * Normalises to `null` (same as `getCompletionVerification` above) rather
 * than passing `undefined` through: React Query v5 treats a query function
 * resolving to `undefined` as a contract violation (it reserves `undefined`
 * to mean "no data fetched yet") and logs a console error every time this
 * fires for a completed-but-unrated job - the common case right after a job
 * finishes.
 */
export const getCustomerRating = async (
  jobId: string,
): Promise<CustomerRatingResponse | null> => {
  const result = await apiFetch<CustomerRatingResponse | undefined>(
    `${JOBS_BASE}/${jobId}/customer-rating`,
    { authenticated: true },
  );
  return result ?? null;
};

export const submitCustomerRating = (jobId: string, request: SubmitCustomerRatingRequest) =>
  apiFetch<CustomerRatingResponse>(`${JOBS_BASE}/${jobId}/customer-rating`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });
