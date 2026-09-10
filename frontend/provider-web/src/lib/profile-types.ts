/**
 * Response/request shapes for the Provider API's profile/onboarding surface
 * (`/api/v1/profile`, docs/PROVIDER.md's Capability & Coverage / Identity
 * domains): profile details, KYC documents, service areas, and skills.
 *
 * provider-api registers a JsonStringEnumConverter (per the task brief this
 * client was built against), so enum-like fields below are plain string
 * unions rather than the ordinal-number encoding admin-web's AdminApi types
 * need - no declaration-order coupling to a C# source to maintain here.
 */
import type { ProviderOnboardingStatus, ProviderProfile } from "./types";

export interface UpdateProfileRequest {
  legalName: string;
  displayName: string;
  email?: string;
}

/** provider_kyc_document.doc_type (docs/PROVIDER.md's Identity domain). */
export type KycDocType =
  | "IdentityProof"
  | "AddressProof"
  | "BankAccountProof"
  | "ProfessionalCertificate"
  | "Other";

/**
 * provider_kyc_document.verification_status. "Superseded" (task 349) is set
 * on an older document the moment a provider submits a newer one of the same
 * `docType` - it never comes from an admin decision, unlike the other three.
 */
export type KycVerificationStatus = "Pending" | "Approved" | "Rejected" | "Superseded";

export interface KycDocument {
  id: string;
  providerId: string;
  docType: KycDocType;
  docNumber: string | null;
  fileRef: string;
  verificationStatus: KycVerificationStatus;
  submittedAt: string;
  verifiedAt: string | null;
}

export interface KycStatusResponse {
  providerId: string;
  onboardingStatus: ProviderOnboardingStatus;
  documents: KycDocument[];
}

/**
 * `fileRef` is a file *reference/URL* string, not the file itself - the
 * upload endpoint (`uploadKycDocumentFile` in `profile-api.ts`) fills this in
 * with a real hosted URL, but a provider can also paste one directly.
 */
export interface SubmitKycDocumentRequest {
  docType: KycDocType;
  fileRef: string;
  docNumber?: string;
}

/**
 * `photoUrl` is a file *reference/URL* string, not the file itself - same
 * shape as `SubmitKycDocumentRequest.fileRef`. Null or empty clears the
 * photo. Setting one always sends it back for admin review.
 */
export interface UpdateProviderPhotoRequest {
  photoUrl: string | null;
}

export interface ServiceArea {
  id: string;
  providerId: string;
  cityId: string;
  zoneId: string | null;
  pincodeId: string | null;
  isActive: boolean;
}

export interface ServiceAreaInput {
  cityId: string;
  zoneId?: string;
  pincodeId?: string;
}

/** Full replace, per the API contract - the whole coverage set is sent every time. */
export interface UpdateServiceAreasRequest {
  areas: ServiceAreaInput[];
}

export interface ProviderSkill {
  id: string;
  providerId: string;
  categoryId: string;
  serviceId: string | null;
  isActive: boolean;
}

export interface ProviderSkillInput {
  categoryId: string;
  serviceId?: string;
}

/** Full replace, per the API contract - the whole skill set is sent every time. */
export interface UpdateSkillsRequest {
  skills: ProviderSkillInput[];
}

/**
 * One prerequisite on the go-live checklist (docs/OPEN-FIXES-FEATURES.csv
 * "Provider Web, Proposed new page, Onboarding checklist and go-live
 * status"). `key` is what the UI switches on to pick a fix-it link; `label`
 * is ready to render as-is.
 */
export interface GoLiveCheck {
  key: "kycApproved" | "hasActiveSkill" | "hasActiveServiceArea" | "hasAvailability" | (string & {});
  label: string;
  isComplete: boolean;
}

/** `GET /profile/go-live-status` - whether the provider is missing any prerequisite to start receiving work. */
export interface GoLiveStatus {
  isGoLiveReady: boolean;
  checks: GoLiveCheck[];
}

export type { ProviderProfile };
