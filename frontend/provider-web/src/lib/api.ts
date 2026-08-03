/**
 * Typed fetch wrapper for the Provider API.
 * Base URL comes from NEXT_PUBLIC_API_URL (see .env.example).
 */
import { clearSession, getAccessToken } from "./auth";

const API_BASE_URL =
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
 * invalid/expired OTP, 429 throttled) - rather than asserting one exact code
 * string that provider-api's controller may not use yet.
 */
export function describeLoginError(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 429) {
      return "Too many attempts. Please wait a few minutes and try again.";
    }
    if (error.status === 401) {
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
 * Shared request/error-handling core behind `apiFetch` - everything except
 * how the successful body is read back lives here.
 */
async function performFetch(path: string, init: ApiFetchOptions | undefined, defaultHeaders: Record<string, string>): Promise<Response> {
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
    let problem: ProblemDetails | null = null;
    try {
      problem = (await response.json()) as ProblemDetails;
    } catch {
      // Non-JSON error body; keep problem null.
    }

    // A 401 on an authenticated call means the token the caller had is no
    // longer valid (expired, revoked, or the account was deactivated after
    // login) - clear it so every mounted guard (RequireProviderAuth) reacts
    // to the auth-changed event and sends the provider back to /login. An
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
