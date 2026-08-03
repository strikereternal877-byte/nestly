/**
 * Typed client for the Provider API's jobs surface (`/api/v1/jobs`). Every
 * call is authenticated. The backend (task #147) is live; `isNotImplemented`
 * is kept as a defensive check for a 501 in case an older deployment still
 * has the stub.
 */
import { API_V1, apiFetch } from "./api";
import type {
  BookingCompletionProofResponse,
  JobDetail,
  JobListItem,
  JobListParams,
  JobListResponse,
  SubmitCompletionProofRequest,
  SubmitCompletionVerificationRequest,
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

export const rejectJob = (jobId: string) =>
  apiFetch<JobDetail>(`${JOBS_BASE}/${jobId}/reject`, { method: "POST", authenticated: true });

export const startJob = (jobId: string) =>
  apiFetch<JobDetail>(`${JOBS_BASE}/${jobId}/start`, { method: "POST", authenticated: true });

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

export const getCompletionVerification = (jobId: string) =>
  apiFetch<BookingCompletionProofResponse | undefined>(`${JOBS_BASE}/${jobId}/completion-verification`, {
    authenticated: true,
  });
