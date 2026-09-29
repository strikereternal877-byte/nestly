/**
 * Response/request shapes shared across the Provider API's auth surface.
 *
 * Mirrors admin-web/src/lib/types.ts's AdminLoginResponse shape - the task
 * brief that shipped alongside provider-api (task 151, see docs/PROVIDER.md)
 * confirms the login/refresh response is field-for-field the same:
 * accessToken/accessTokenExpiresAtUtc/refreshToken.
 *
 * Unlike admin-api, provider-api registers a JsonStringEnumConverter (per the
 * task brief), so status-like fields below are plain string unions rather
 * than the ordinal-number enums admin-web has to use for AdminApi's C#
 * enums - no declaration-order coupling to maintain here.
 */

export interface ProviderLoginResponse {
  accessToken: string;
  accessTokenExpiresAtUtc: string;
  refreshToken: string;
}

/**
 * Claims this client reads off the decoded access token for UI purposes
 * ("signed in as" display) - never for authorization decisions, which
 * remain the API's job. See lib/jwt.ts's decodeJwtPayload doc comment.
 */
export interface ProviderSessionClaims {
  subject: string | null;
  mobile: string | null;
}

/** provider.status - PendingVerification while KYC/onboarding is incomplete. */
export type ProviderStatus = "PendingVerification" | "Active" | "Suspended" | "Deactivated";

/** provider.onboarding_status - the step-by-step onboarding funnel (docs/PROVIDER.md). */
export type ProviderOnboardingStatus =
  | "Registered"
  | "ProfileCompleted"
  | "KycSubmitted"
  | "KycVerified"
  | "Completed";

/** The provider profile shape returned by both GET and PUT /profile. */
/** provider.photo_moderation_status - null exactly when `photoUrl` is. */
export type ProviderPhotoModerationStatus = "Pending" | "Approved" | "Rejected";

export interface ProviderProfile {
  id: string;
  legalName: string;
  displayName: string;
  phone: string;
  email: string | null;
  status: ProviderStatus;
  onboardingStatus: ProviderOnboardingStatus;
  /**
   * The provider's OWN view of their photo, so it is present whatever the
   * moderation state. Customers see it only once it is Approved - the API
   * gates that separately (Provider.PublicPhotoUrl), so this screen showing a
   * pending photo is correct rather than a leak.
   */
  photoUrl: string | null;
  photoModerationStatus: ProviderPhotoModerationStatus | null;
  /** Why a photo was rejected. Without it a rejection is a silent dead end. */
  photoModerationNote: string | null;
  /**
   * Task 309. Both null together when the provider has no visible reviews
   * yet - a distinct state from a rating of zero, so render "not yet rated,"
   * not "0.0 stars."
   */
  averageRating: number | null;
  reviewCount: number | null;
}

// ---- Auth requests (POST /auth/...) ----

export interface RequestOtpRequest {
  mobile: string;
}

export interface RegisterProviderRequest {
  mobile: string;
  otpCode: string;
  legalName: string;
  displayName: string;
  email?: string;
  consentAccepted: boolean;
  /** Optional — task 372. Requires `email` to also be set; creates an additional email+password auth identity alongside the always-present mobile identity. */
  password?: string;
}

export interface RequestRegistrationEmailOtpRequest {
  email: string;
}

/**
 * Email-first registration: the OTP proves ownership of the email address
 * instead of the mobile number. Mobile is still collected and stored but is
 * not itself OTP-verified on this path.
 */
export interface RegisterProviderWithEmailRequest {
  email: string;
  otpCode: string;
  legalName: string;
  displayName: string;
  mobile: string;
  password: string;
  consentAccepted: boolean;
  referralCode?: string | null;
}

export interface VerifyLoginOtpRequest {
  mobile: string;
  otpCode: string;
}

/** Task 372: email+password login, mirroring customer-web's LoginWithPasswordRequestBody. */
export interface LoginWithPasswordRequest {
  email: string;
  password: string;
}

/** Task 372: step 1 of the forgot-password flow. */
export interface ForgotPasswordRequest {
  email: string;
}

/** Task 372: step 2 — the OTP was sent to the mobile number on file, not the email. */
export interface ResetPasswordRequest {
  email: string;
  otpCode: string;
  newPassword: string;
}

export interface RefreshSessionRequest {
  refreshToken: string;
}

export interface LogoutRequest {
  refreshToken: string;
}
