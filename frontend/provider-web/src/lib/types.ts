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
export interface ProviderProfile {
  id: string;
  legalName: string;
  displayName: string;
  phone: string;
  email: string | null;
  status: ProviderStatus;
  onboardingStatus: ProviderOnboardingStatus;
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
}

export interface VerifyLoginOtpRequest {
  mobile: string;
  otpCode: string;
}

export interface RefreshSessionRequest {
  refreshToken: string;
}

export interface LogoutRequest {
  refreshToken: string;
}
