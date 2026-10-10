/**
 * Typed client for the Provider API's profile/onboarding surface
 * (`/api/v1/profile`). Every call is authenticated.
 */
import { API_V1, apiFetch, apiUpload } from "./api";
import type {
  BankAccount,
  GoLiveStatus,
  KycStatusResponse,
  ServiceArea,
  SubmitBankAccountRequest,
  SubmitKycDocumentRequest,
  UpdateProfileRequest,
  UpdateProviderPhotoRequest,
  UpdateServiceAreasRequest,
  UpdateSkillsRequest,
  ProviderSkill,
} from "./profile-types";
import type { ProviderProfile } from "./types";

const PROFILE_BASE = `${API_V1}/profile`;

export const getProfile = () =>
  apiFetch<ProviderProfile>(PROFILE_BASE, { authenticated: true });

export const updateProfile = (request: UpdateProfileRequest) =>
  apiFetch<ProviderProfile>(PROFILE_BASE, {
    method: "PUT",
    authenticated: true,
    body: JSON.stringify(request),
  });

/** Sets or clears the profile photo. The response carries the new moderation state. */
export const updateProfilePhoto = (request: UpdateProviderPhotoRequest) =>
  apiFetch<ProviderProfile>(`${PROFILE_BASE}/photo`, {
    method: "PUT",
    authenticated: true,
    body: JSON.stringify(request),
  });

/**
 * Uploads a photo file and returns its URL, for feeding into
 * {@link updateProfilePhoto}'s `photoUrl`. A separate call rather than one
 * combined upload-and-save request, so the existing save/moderation flow on
 * `updateProfilePhoto` needs no change.
 */
export const uploadProfilePhoto = (file: File) => {
  const formData = new FormData();
  formData.append("file", file);
  return apiUpload<{ url: string }>(`${PROFILE_BASE}/photo/upload`, formData, { authenticated: true });
};

export const getKycStatus = () =>
  apiFetch<KycStatusResponse>(`${PROFILE_BASE}/kyc`, { authenticated: true });

export const submitKycDocument = (request: SubmitKycDocumentRequest) =>
  apiFetch<KycStatusResponse["documents"][number]>(`${PROFILE_BASE}/kyc/documents`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

/**
 * Uploads a KYC document file and returns its URL, for feeding into
 * {@link submitKycDocument}'s `fileRef`. Same upload-then-submit split as
 * {@link uploadProfilePhoto}.
 */
export const uploadKycDocumentFile = (file: File) => {
  const formData = new FormData();
  formData.append("file", file);
  return apiUpload<{ url: string }>(`${PROFILE_BASE}/kyc/documents/upload`, formData, { authenticated: true });
};

export const getServiceAreas = () =>
  apiFetch<ServiceArea[]>(`${PROFILE_BASE}/service-areas`, { authenticated: true });

export const updateServiceAreas = (request: UpdateServiceAreasRequest) =>
  apiFetch<ServiceArea[]>(`${PROFILE_BASE}/service-areas`, {
    method: "PUT",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const getSkills = () =>
  apiFetch<ProviderSkill[]>(`${PROFILE_BASE}/skills`, { authenticated: true });

export const updateSkills = (request: UpdateSkillsRequest) =>
  apiFetch<ProviderSkill[]>(`${PROFILE_BASE}/skills`, {
    method: "PUT",
    authenticated: true,
    body: JSON.stringify(request),
  });

/**
 * The provider's go-live checklist (docs/OPEN-FIXES-FEATURES.csv
 * "Onboarding checklist and go-live status") - which specific prerequisites
 * are still missing before the account can start receiving work.
 */
export const getGoLiveStatus = () =>
  apiFetch<GoLiveStatus>(`${PROFILE_BASE}/go-live-status`, { authenticated: true });

/**
 * The caller's own structured bank account details for payouts
 * (docs/PROVIDER.md OPEN DECISIONS #3). 404s when nothing has been submitted
 * yet - callers treat that as an empty state, not an error (see
 * `isNotImplemented` in `api.ts` for the same status-code-as-signal idiom).
 */
export const getBankAccount = () =>
  apiFetch<BankAccount>(`${PROFILE_BASE}/bank-account`, { authenticated: true });

/** Submits or edits the caller's bank account details - an upsert (one row, always reset to Pending on resubmission), unlike KYC documents. */
export const submitBankAccount = (request: SubmitBankAccountRequest) =>
  apiFetch<BankAccount>(`${PROFILE_BASE}/bank-account`, {
    method: "PUT",
    authenticated: true,
    body: JSON.stringify(request),
  });
