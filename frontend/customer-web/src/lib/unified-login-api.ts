/**
 * Cross-origin calls the unified login entry point (task 206) makes directly
 * against admin-api/provider-api - the two backends this app's own `apiFetch`
 * (lib/api.ts) can't reach, since it's hardcoded to consumer-api's base URL.
 *
 * This exists only because there is no shared parent domain across the
 * three frontends yet (docs/DEVOPS.md's hosting/domain decisions are still
 * open) and no API gateway in front of the three backends - see
 * docs/ARCHITECTURE.md's "UNIFIED LOGIN" section for the full reasoning.
 * Each backend keeps issuing its own independently-audienced token exactly
 * as it does today; only the *routing* to reach it is shared.
 */
import { ApiError, type ProblemDetails } from "./api";

const ADMIN_API_BASE_URL = process.env.NEXT_PUBLIC_ADMIN_API_URL ?? "http://localhost:5177";
const PROVIDER_API_BASE_URL = process.env.NEXT_PUBLIC_PROVIDER_API_URL ?? "http://localhost:5337";

/** The admin-web/provider-web origins this page hands the browser off to after a successful sign-in. */
export const ADMIN_WEB_URL = process.env.NEXT_PUBLIC_ADMIN_WEB_URL ?? "http://localhost:3001";
export const PROVIDER_WEB_URL = process.env.NEXT_PUBLIC_PROVIDER_WEB_URL ?? "http://localhost:3002";

/** Same shape every backend's login endpoint returns (AdminLoginResponse/ProviderLoginResponse/LoginResponse are structurally identical). */
export interface CrossOriginSession {
  accessToken: string;
  accessTokenExpiresAtUtc: string;
  refreshToken: string;
}

async function crossOriginFetch<T>(baseUrl: string, path: string, body: unknown): Promise<T> {
  const response = await fetch(`${baseUrl}${path}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });

  if (!response.ok) {
    let problem: ProblemDetails | null = null;
    try {
      problem = (await response.json()) as ProblemDetails;
    } catch {
      // Non-JSON error body; keep problem null.
    }
    throw new ApiError(response.status, problem);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

export const loginAdmin = (email: string, password: string) =>
  crossOriginFetch<CrossOriginSession>(ADMIN_API_BASE_URL, "/api/v1/admin/auth/login", { email, password });

export const requestProviderLoginOtp = (mobile: string) =>
  crossOriginFetch<void>(PROVIDER_API_BASE_URL, "/api/v1/auth/login/otp", { mobile });

export const verifyProviderLoginOtp = (mobile: string, otpCode: string) =>
  crossOriginFetch<CrossOriginSession>(PROVIDER_API_BASE_URL, "/api/v1/auth/login/otp/verify", { mobile, otpCode });

/**
 * Hands the browser off to the target app's own origin with the session in
 * the URL fragment, not a query string or path segment - fragments are
 * never sent to the server (so the token never touches access logs on the
 * hop) and never included in the `Referer` header of whatever the callback
 * page navigates to next. The callback page strips it immediately via
 * `history.replaceState` once read, so it doesn't linger in browser history
 * either. This is the standard technique for a same-token cross-origin
 * handoff when there is no shared cookie domain to rely on instead.
 */
export function redirectWithSession(targetOrigin: string, destinationPath: string, session: CrossOriginSession): void {
  const fragment = new URLSearchParams({
    accessToken: session.accessToken,
    refreshToken: session.refreshToken,
    accessTokenExpiresAtUtc: session.accessTokenExpiresAtUtc,
  }).toString();

  window.location.href = `${targetOrigin}/auth/callback?next=${encodeURIComponent(destinationPath)}#${fragment}`;
}
