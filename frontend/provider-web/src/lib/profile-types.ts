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

// ---- Bank account (structured payout details, docs/PROVIDER.md OPEN DECISIONS #3) ----
//
// Unlike the KYC types above, ProviderBankAccountResponse mirrors
// Nestly.Application.ProviderManagement.ProviderBankAccountResponse's raw
// enum field (VerificationStatus) rather than a hand-stringified one - that
// namespace's contracts are shared with admin-api's AdminApi, which has no
// JsonStringEnumConverter registered (see earnings-types.ts's PayoutStatus,
// same convention: numeric ordinal, declaration-order-synced with its C#
// source). ProviderApi does not register one either - this file's own top
// comment describing a JsonStringEnumConverter applies to the hand-written
// KYC/photo contracts above, not this shared-namespace one.

/** Mirrors Nestly.Domain.ProviderBankAccountVerificationStatus's declaration order exactly. */
export enum BankAccountVerificationStatus {
  Pending = 0,
  Verified = 1,
  Rejected = 2,
}

export interface BankAccount {
  id: string;
  providerId: string;
  accountHolderName: string;
  accountNumber: string;
  ifscCode: string;
  bankName: string;
  verificationStatus: BankAccountVerificationStatus;
  verifiedBy: string | null;
  verifiedAt: string | null;
  rejectionReason: string | null;
  updatedAt: string;
}

/** `PUT /profile/bank-account` - upsert; the provider id is never sent, taken from the JWT server-side (SRS 28.3 IDOR), same convention as `SubmitKycDocumentRequest`. */
export interface SubmitBankAccountRequest {
  accountHolderName: string;
  accountNumber: string;
  ifscCode: string;
  bankName: string;
}

export type { ProviderProfile };
