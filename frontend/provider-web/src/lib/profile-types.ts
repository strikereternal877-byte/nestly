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

/** provider_kyc_document.verification_status. */
export type KycVerificationStatus = "Pending" | "Approved" | "Rejected";

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
 * `fileRef` is a file *reference/URL* string, not a binary upload - there is
 * no file storage backend wired up yet (see the KYC submission form's own
 * comment), so this only ever carries a reference the provider pasted in or
 * that a future upload flow will populate once storage exists.
 */
export interface SubmitKycDocumentRequest {
  docType: KycDocType;
  fileRef: string;
  docNumber?: string;
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

export type { ProviderProfile };
