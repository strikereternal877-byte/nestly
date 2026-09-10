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
  /** Task 349: replaced by a newer document of the same type - never an admin decision, unlike the other three. Appended, matching the C# enum's own append-only rule. */
  Superseded = 3,
}

/** Mirrors Nestly.Domain.ProviderPhotoModerationStatus's declaration order exactly (task 293). */
export enum ProviderPhotoModerationStatus {
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
  /** The provider finished the job and it was verified (completion proof/OTP) - see the C# enum's doc comment for how this frees the provider for their next order. */
  Completed = 5,
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
  /** Cities this provider has an active service area for (task 371) — empty if none configured yet. */
  serviceCities: string[];
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
  /** Matches a provider with an active service area covering this city (task 371). */
  cityId?: string;
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
  /** Task 243: both-or-neither, full-overwrite (submitting both null clears a previously set location). */
  latitude?: number | null;
  longitude?: number | null;
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

/**
 * A provider's profile photo and where it stands with moderation (task 293).
 *
 * `photoUrl` is the raw stored reference, deliberately NOT the customer-facing
 * `PublicPhotoUrl` - a moderator has to see the photo precisely because it has
 * not been approved. `moderationStatus` is null exactly when `photoUrl` is.
 */
export interface ProviderPhoto {
  providerId: string;
  displayName: string;
  photoUrl: string | null;
  moderationStatus: ProviderPhotoModerationStatus | null;
  moderatedByAdminUserId: string | null;
  moderatedAtUtc: string | null;
  moderationNote: string | null;
}

export interface RejectProviderPhotoRequest {
  reason: string;
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
  /** Task 243: feeds the automatic-assignment engine's distance ranking (task 244). Null until set via the edit form below. */
  latitude: number | null;
  longitude: number | null;
  kycDocuments: ProviderKycDocument[];
  backgroundChecks: ProviderBackgroundCheck[];
  /** Task 293. Appended last, matching the C# positional record's own append-only rule. */
  photo: ProviderPhoto;
}

// ---- Capacity limits (task 245 built enforcement; task 308 adds this write path) ----

/**
 * A provider's dispatch capacity limits. Null on either field means
 * unlimited. Hard-enforced by the automatic-assignment engine; still only an
 * advisory load signal on manual admin assignment (PROVIDER.md OPEN
 * DECISIONS - AUTOMATIC ASSIGNMENT #2).
 */
export interface ProviderCapacity {
  providerId: string;
  maxJobsPerDay: number | null;
  maxJobsPerSlot: number | null;
}

/** Full-overwrite set of a provider's capacity limits. Null clears a limit back to unlimited. */
export interface SetProviderCapacityRequest {
  maxJobsPerDay: number | null;
  maxJobsPerSlot: number | null;
}

// ---- KYC approval and background check / activation (task 150b, 160) ----

export interface RejectProviderKycDocumentRequest {
  reason: string;
}

export interface RecordBackgroundCheckRequest {
  status: ProviderBackgroundCheckStatus;
  notes?: string;
}

// ---- Performance (task 150c; ranking list for docs/OPEN-FIXES-FEATURES.csv "Provider performance") ----

/**
 * All-time (unlike `ProviderPerformanceSummary` below, which defaults to a
 * rolling window) - matches `inProgressJobs`/`lifetimeEarnings`, which were
 * already all-time before this shape grew the rate/rating fields.
 *
 * `acceptanceRatePercent`/`averageResponseTimeMinutes`/`completionRatePercent`/
 * `averageRating` are null exactly when there is no data to compute them
 * from yet (no offers ever, no response ever, no acceptance ever, no visible
 * review yet respectively) - not the same as zero.
 */
export interface ProviderPerformance {
  providerId: string;
  totalAssignments: number;
  acceptedAssignments: number;
  rejectedAssignments: number;
  completedJobs: number;
  inProgressJobs: number;
  lifetimeEarnings: number;
  acceptanceRatePercent: number | null;
  averageResponseTimeMinutes: number | null;
  completionRatePercent: number | null;
  averageRating: number | null;
  ratingCount: number;
}

/**
 * One row of the provider-performance ranking list (docs/OPEN-FIXES-FEATURES.csv
 * "Provider performance"). Deliberately has NO `cancellations` field - see
 * `ProviderPerformanceSummaryResponse`'s C# doc comment
 * (ProviderManagementContracts.cs) for why: nothing in
 * `BookingProviderAssignmentStatus` distinguishes "provider accepted, then
 * backed out" from a plain decline (`Rejected`, already folded into
 * `acceptanceRatePercent`) or a booking-side cancellation (`Withdrawn`, not
 * the provider's doing) - reporting either as "cancellations" would
 * misattribute or double-count, so it is omitted rather than fabricated.
 */
export interface ProviderPerformanceSummary {
  providerId: string;
  displayName: string;
  status: ProviderStatus;
  offersReceived: number;
  acceptedOffers: number;
  acceptanceRatePercent: number | null;
  averageResponseTimeMinutes: number | null;
  completedJobs: number;
  completionRatePercent: number | null;
  averageRating: number | null;
  ratingCount: number;
}

/** Mirrors Nestly.Application.ProviderManagement.ProviderPerformanceSortField's declaration order exactly. */
export enum ProviderPerformanceSortField {
  DisplayName = 0,
  OffersReceived = 1,
  AcceptanceRate = 2,
  AverageResponseTime = 3,
  CompletionRate = 4,
  AverageRating = 5,
}

export interface ProviderPerformanceListParams {
  page?: number;
  pageSize?: number;
  /** Rolling window in days the offers/rate/response-time/completion columns are computed over - defaults to 30 server-side. Does not affect `averageRating`, which is always all-time. */
  periodDays?: number;
  sortBy?: ProviderPerformanceSortField;
  sortDescending?: boolean;
}

export interface ProviderPerformanceListResponse {
  items: ProviderPerformanceSummary[];
  totalCount: number;
  page: number;
  pageSize: number;
  periodDays: number;
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
  /** Appended last, matching the C# positional record's own append-only rule. */
  providerOnboardingStatus: ProviderOnboardingStatus;
}

/**
 * A candidate for manually assigning this booking (matched by service area +
 * skill, ranked by specificity then load) - informs the admin's choice, does
 * not assign anyone by itself. See BookingProviderAssignmentContracts.cs's
 * EligibleProviderResponse doc comment for why rating is deliberately not a
 * signal here (PROVIDER.md OPEN DECISIONS #4).
 */
/**
 * `acceptanceRatePercent`/`averageRating` (docs/OPEN-FIXES-FEATURES.csv
 * "Provider performance": "expose the key metrics inline in the assignment
 * picker") are display-only, like every other field here except the ranking
 * itself - see EligibleProviderResponse's C# doc comment
 * (BookingProviderAssignmentContracts.cs) for why rating/rate never affect
 * candidate order (PROVIDER.md OPEN DECISIONS #4/#3). Null means no data yet
 * (no offers ever / no visible review yet), not zero.
 */
export interface EligibleProvider {
  providerId: string;
  displayName: string;
  phone: string;
  pincodeMatch: boolean;
  serviceMatch: boolean;
  maxJobsPerDay: number | null;
  assignedJobsToday: number;
  acceptanceRatePercent: number | null;
  averageRating: number | null;
}
