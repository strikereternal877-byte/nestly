/**
 * Admin provider management shapes (PROVIDER.md; tasks 147, 148, 150a-c, 159,
 * 160) mirror the C# records in Nestly.Application.ProviderManagement
 * (ProviderManagementContracts.cs, BookingProviderAssignmentContracts.cs,
 * ProviderFinancialContracts.cs) - see ProvidersController/PayoutsController.
 * AdminApi has no JsonStringEnumConverter registered (see bookings-types.ts's
 * same caveat), so every enum below serialises over the wire as its ordinal
 * and must stay in declaration-order sync with its C# source.
 */

/** Mirrors Nestly.Domain.ProviderType's declaration order exactly. */
export enum ProviderType {
  Individual = 0,
  Company = 1,
}

/** Mirrors Nestly.Domain.ProviderStatus's declaration order exactly. */
export enum ProviderStatus {
  PendingVerification = 0,
  Active = 1,
  Suspended = 2,
  Deactivated = 3,
}

/** Mirrors Nestly.Domain.ProviderOnboardingStatus's declaration order exactly. */
export enum ProviderOnboardingStatus {
  Registered = 0,
  ProfileCompleted = 1,
  KycSubmitted = 2,
  KycVerified = 3,
  Completed = 4,
}

/** Mirrors Nestly.Domain.ProviderKycDocumentType's declaration order exactly. */
export enum ProviderKycDocumentType {
  IdentityProof = 0,
  AddressProof = 1,
  BankAccountProof = 2,
  ProfessionalCertificate = 3,
  Other = 4,
}

/** Mirrors Nestly.Domain.ProviderKycVerificationStatus's declaration order exactly. */
export enum ProviderKycVerificationStatus {
  Pending = 0,
  Approved = 1,
  Rejected = 2,
}

/** Mirrors Nestly.Domain.ProviderBackgroundCheckStatus's declaration order exactly. */
export enum ProviderBackgroundCheckStatus {
  Pending = 0,
  Passed = 1,
  Failed = 2,
}

/** Mirrors Nestly.Domain.BookingAssignedByType's declaration order exactly. */
export enum BookingAssignedByType {
  Admin = 0,
  System = 1,
}

/** Mirrors Nestly.Domain.BookingProviderAssignmentStatus's declaration order exactly. */
export enum BookingProviderAssignmentStatus {
  Assigned = 0,
  Accepted = 1,
  Rejected = 2,
  Reassigned = 3,
  Withdrawn = 4,
}

/** Mirrors Nestly.Domain.ProviderEarningEntryType's declaration order exactly. */
export enum ProviderEarningEntryType {
  Credit = 0,
  Debit = 1,
}

/** Mirrors Nestly.Domain.ProviderEarningSourceType's declaration order exactly. */
export enum ProviderEarningSourceType {
  JobCompletion = 0,
  Penalty = 1,
  ManualAdjustment = 2,
}

/** Mirrors Nestly.Domain.ProviderPayoutStatus's declaration order exactly. */
export enum ProviderPayoutStatus {
  Pending = 0,
  Processing = 1,
  Paid = 2,
  Failed = 3,
}

// ---- CRUD (task 150a) ----

export interface ProviderSummary {
  id: string;
  legalName: string;
  displayName: string;
  phone: string;
  email: string | null;
  status: ProviderStatus;
  onboardingStatus: ProviderOnboardingStatus;
  createdAt: string;
}

export interface ProviderSearchResponse {
  items: ProviderSummary[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface ProviderSearchParams {
  name?: string;
  phone?: string;
  status?: ProviderStatus;
  onboardingStatus?: ProviderOnboardingStatus;
  page?: number;
  pageSize?: number;
}

export interface CreateProviderRequest {
  legalName: string;
  displayName: string;
  phone: string;
  email?: string;
}

export interface UpdateProviderRequest {
  legalName: string;
  displayName: string;
  email?: string;
}

export interface SuspendProviderRequest {
  reason: string;
}

export interface ProviderKycDocument {
  id: string;
  docType: ProviderKycDocumentType;
  docNumber: string | null;
  fileRef: string;
  verificationStatus: ProviderKycVerificationStatus;
  verifiedBy: string | null;
  verifiedAt: string | null;
  submittedAt: string;
}

export interface ProviderBackgroundCheck {
  id: string;
  status: ProviderBackgroundCheckStatus;
  checkedBy: string;
  checkedAt: string;
  notes: string | null;
}

export interface ProviderDetail {
  id: string;
  legalName: string;
  displayName: string;
  providerType: ProviderType;
  phone: string;
  email: string | null;
  status: ProviderStatus;
  onboardingStatus: ProviderOnboardingStatus;
  createdAt: string;
  updatedAt: string;
  kycDocuments: ProviderKycDocument[];
  backgroundChecks: ProviderBackgroundCheck[];
}

// ---- KYC approval and background check / activation (task 150b, 160) ----

export interface RejectProviderKycDocumentRequest {
  reason: string;
}

export interface RecordBackgroundCheckRequest {
  status: ProviderBackgroundCheckStatus;
  notes?: string;
}

// ---- Performance (task 150c) ----

export interface ProviderPerformance {
  providerId: string;
  totalAssignments: number;
  acceptedAssignments: number;
  rejectedAssignments: number;
  completedJobs: number;
  inProgressJobs: number;
  lifetimeEarnings: number;
}

// ---- Earnings ledger and payouts (task 148) ----

export interface ProviderEarningLedgerEntry {
  id: string;
  providerId: string;
  entryType: ProviderEarningEntryType;
  amount: number;
  balanceAfter: number;
  sourceType: ProviderEarningSourceType;
  sourceReferenceId: string | null;
  description: string;
  createdAtUtc: string;
}

export interface ProviderEarningsSummary {
  providerId: string;
  currentBalance: number;
  entries: ProviderEarningLedgerEntry[];
}

export interface RecordProviderEarningAdjustmentRequest {
  entryType: ProviderEarningEntryType;
  amount: number;
  sourceType: ProviderEarningSourceType;
  sourceReferenceId?: string;
  description: string;
}

export interface CreateProviderPayoutRequest {
  periodStart: string;
  periodEnd: string;
}

export interface UpdateProviderPayoutStatusRequest {
  status: ProviderPayoutStatus;
  payoutReference?: string;
  notes?: string;
}

export interface ProviderPayout {
  id: string;
  providerId: string;
  providerDisplayName: string;
  periodStart: string;
  periodEnd: string;
  totalAmount: number;
  status: ProviderPayoutStatus;
  payoutReference: string | null;
  notes: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface ProviderPayoutSearchResponse {
  items: ProviderPayout[];
  totalCount: number;
  page: number;
  pageSize: number;
}

// ---- Booking assignment (task 147, 159) ----

export interface AssignProviderRequest {
  providerId: string;
  responseDeadline?: string;
}

export interface RejectAssignmentRequest {
  reason?: string;
}

export interface BookingProviderAssignment {
  id: string;
  bookingId: string;
  providerId: string;
  providerDisplayName: string;
  assignedByType: BookingAssignedByType;
  assignedByUserId: string | null;
  assignedAt: string;
  status: BookingProviderAssignmentStatus;
  responseDeadline: string | null;
  respondedAt: string | null;
  notes: string | null;
}
