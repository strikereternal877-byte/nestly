/**
 * Typed fetch wrapper for the Provider API.
 * Base URL comes from NEXT_PUBLIC_API_URL (see .env.example).
 */
import { clearSession, getAccessToken, getRefreshToken, storeSession } from "./auth";
import type { ProviderLoginResponse } from "./types";

export const API_BASE_URL =
  process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5337";

/**
 * Every provider endpoint is served under the v1 route prefix. Unlike
 * admin-api (a shared project with /api/v1/admin/... prefixed routes),
 * provider-api's entire surface is provider-scoped, so there is no extra
 * "/admin"-style segment here.
 */
export const API_V1 = "/api/v1";

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly problem: ProblemDetails | null,
  ) {
    super(problem?.detail ?? `Request failed with status ${status}`);
    this.name = "ApiError";
  }
}

/** RFC 7807 problem details returned by the backend on failures. */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  correlationId?: string;
  errors?: { code: string; message: string }[];
}

/**
 * Best-effort human-readable message for a failed request.
 *
 * Mirrors admin-web/src/lib/api.ts's describeError: ValidationProblem
 * responses carry per-field messages under `errors` (either the array shape
 * or ASP.NET's object-keyed-by-field-name shape), a plain failure carries
 * only `detail`.
 */
export function describeError(error: unknown): string {
  if (error instanceof ApiError) {
    const fieldErrors = error.problem?.errors;
    if (Array.isArray(fieldErrors) && fieldErrors.length > 0) {
      return fieldErrors.map((e) => e.message).join(" ");
    }
    if (fieldErrors && !Array.isArray(fieldErrors)) {
      const messages = Object.values(
        fieldErrors as unknown as Record<string, string[]>,
      ).flat();
      if (messages.length > 0) return messages.join(" ");
    }
    return error.message;
  }
  return error instanceof Error ? error.message : "Something went wrong.";
}

/**
 * True when the backend answered with 501 Not Implemented - the expected
 * shape for the Jobs and Earnings surfaces until sibling tasks #147/#148
 * land their underlying entities (see docs/PROVIDER.md). Callers use this to
 * render a "not yet available" empty state instead of a generic error.
 */
export function isNotImplemented(error: unknown): boolean {
  return error instanceof ApiError && error.status === 501;
}

/**
 * Login-specific error message: checks the signals RFC 7807 + the codebase's
 * Module.Reason convention (docs/API.md) make available - HTTP status (401
 * invalid/expired OTP or bad credentials, 429 throttled) - rather than
 * asserting one exact code string that provider-api's controller may not use
 * yet.
 *
 * `context` distinguishes the two sign-in modes (task 372's OTP/password
 * toggle) because a 401 means something different in each: an incorrect/
 * expired code for OTP, versus a wrong email or password for the password
 * flow. Getting this wrong (e.g. telling a password-flow user their "code"
 * expired) is confusing even though both cases are technically a 401. The
 * password branch defers to `describeError` instead of hardcoding text
 * because `ProviderLoginService.LoginWithPasswordAsync` already returns a
 * user-facing "Invalid email or password." detail for that case.
 */
export function describeLoginError(
  error: unknown,
  context: "otp" | "password" = "otp",
): string {
  if (error instanceof ApiError) {
    if (error.status === 429) {
      return "Too many attempts. Please wait a few minutes and try again.";
    }
    if (error.status === 401 && context === "otp") {
      return "That code is incorrect or has expired. Request a new one and try again.";
    }
  }
  return describeError(error);
}

export interface ApiFetchOptions extends RequestInit {
  /** Attaches the stored bearer token. Required by every authenticated provider endpoint. */
  authenticated?: boolean;
}

/**
 * Exchanges the stored refresh token for a new access/refresh pair.
 *
 * Mirrors customer-web/src/lib/api.ts's refreshAccessToken: a module-level
 * promise so a burst of concurrent 401s (several queries firing at once when
 * the access token expires mid-session) triggers exactly one refresh call
 * instead of one per request; every caller awaits the same in-flight
 * promise, then it's cleared so the next expiry starts a fresh one. Calls
 * `fetch` directly rather than going through `auth-api.ts`'s `refreshSession`
 * (which itself calls `apiFetch`) to avoid a circular import between the two
 * modules and to keep this path free of the retry logic it exists to serve.
 */
let refreshPromise: Promise<boolean> | null = null;

function refreshAccessToken(): Promise<boolean> {
  if (!refreshPromise) {
    refreshPromise = (async () => {
      const refreshToken = getRefreshToken();
      if (!refreshToken) return false;
      try {
        const response = await fetch(`${API_BASE_URL}${API_V1}/auth/refresh`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ refreshToken }),
        });
        if (!response.ok) return false;
        storeSession((await response.json()) as ProviderLoginResponse);
        return true;
      } catch {
        return false;
      }
    })().finally(() => {
      refreshPromise = null;
    });
  }
  return refreshPromise;
}

/**
 * Shared request/error-handling core behind `apiFetch` - everything except
 * how the successful body is read back lives here.
 */
async function performFetch(
  path: string,
  init: ApiFetchOptions | undefined,
  defaultHeaders: Record<string, string>,
  isRetry = false,
): Promise<Response> {
  const { authenticated, ...requestInit } = init ?? {};

  const headers: Record<string, string> = {
    ...defaultHeaders,
    ...(requestInit.headers as Record<string, string> | undefined),
  };

  if (authenticated) {
    const token = getAccessToken();
    if (!token) {
      throw new ApiError(401, { detail: "You are not signed in." });
    }
    headers.Authorization = `Bearer ${token}`;
  }

  const response = await fetch(`${API_BASE_URL}${path}`, {
    ...requestInit,
    headers,
  });

  if (!response.ok) {
    // A 401 on an authenticated call usually just means the short-lived
    // access token expired mid-session - silently refresh it and retry the
    // request once before treating this as a real auth failure. Only
    // authenticated calls attempt this (login/OTP/refresh itself never sets
    // `authenticated`, so it can't recurse into itself).
    if (authenticated && response.status === 401 && !isRetry) {
      const refreshed = await refreshAccessToken();
      if (refreshed) {
        return performFetch(path, init, defaultHeaders, true);
      }
    }

    let problem: ProblemDetails | null = null;
    try {
      problem = (await response.json()) as ProblemDetails;
    } catch {
      // Non-JSON error body; keep problem null.
    }

    // A 401 on an authenticated call (after the refresh attempt above has
    // already failed or been skipped) means the session truly can't continue
    // - clear it so every mounted guard (RequireProviderAuth) reacts to the
    // auth-changed event and sends the provider back to /login. An
    // unauthenticated call rejecting with 401 (e.g. a bad OTP) must NOT
    // clear anything - there is nothing to clear, and this is the expected
    // "invalid code" outcome.
    if (authenticated && response.status === 401) {
      clearSession();
    }

    throw new ApiError(response.status, problem);
  }

  return response;
}

export async function apiFetch<T>(
  path: string,
  init?: ApiFetchOptions,
): Promise<T> {
  const response = await performFetch(path, init, { "Content-Type": "application/json" });

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

/**
 * Multipart file upload. No `Content-Type` default here (unlike `apiFetch`)
 * - the browser must set it itself to `multipart/form-data; boundary=...`
 * when the body is a `FormData`; a fixed `application/json` header (or any
 * explicit multipart header without the boundary the browser generates)
 * would produce a body the server can't parse.
 */
export async function apiUpload<T>(
  path: string,
  formData: FormData,
  init?: Omit<ApiFetchOptions, "body" | "method">,
): Promise<T> {
  const response = await performFetch(path, { ...init, method: "POST", body: formData }, {});
  return (await response.json()) as T;
}
