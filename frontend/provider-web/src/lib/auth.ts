"use client";

import { decodeJwtPayload } from "./jwt";
import type { ProviderLoginResponse, ProviderSessionClaims } from "./types";

/**
 * Client-side session storage for the provider portal.
 *
 * Mirrors admin-web/src/lib/auth.ts (itself mirroring customer-web) for
 * consistency across Nestly's frontends. Same known limitation applies:
 * tokens in Web Storage are readable by any script on the origin, so an XSS
 * bug becomes a session-theft bug. sessionStorage (not localStorage) narrows
 * the window - the session dies with the browser tab. Moving issuance to an
 * httpOnly cookie is real hardening work, tracked separately, not something
 * this client can do unilaterally.
 *
 * Keys are namespaced "nestly.provider.*" (distinct from admin-web's
 * "nestly.admin.*" and customer-web's "nestly.*") so none of the three apps
 * collide if ever inspected side by side.
 */
const ACCESS_TOKEN_KEY = "nestly.provider.accessToken";
const REFRESH_TOKEN_KEY = "nestly.provider.refreshToken";
const EXPIRES_AT_KEY = "nestly.provider.accessTokenExpiresAt";

/** Notifies subscribed components (layout guard, header) that auth state moved. */
const AUTH_CHANGED_EVENT = "nestly-provider:auth-changed";

function isBrowser(): boolean {
  return typeof window !== "undefined";
}

export function storeSession(session: ProviderLoginResponse): void {
  if (!isBrowser()) return;
  sessionStorage.setItem(ACCESS_TOKEN_KEY, session.accessToken);
  sessionStorage.setItem(REFRESH_TOKEN_KEY, session.refreshToken);
  sessionStorage.setItem(EXPIRES_AT_KEY, session.accessTokenExpiresAtUtc);
  window.dispatchEvent(new Event(AUTH_CHANGED_EVENT));
}

export function clearSession(): void {
  if (!isBrowser()) return;
  sessionStorage.removeItem(ACCESS_TOKEN_KEY);
  sessionStorage.removeItem(REFRESH_TOKEN_KEY);
  sessionStorage.removeItem(EXPIRES_AT_KEY);
  window.dispatchEvent(new Event(AUTH_CHANGED_EVENT));
}

export function getAccessToken(): string | null {
  if (!isBrowser()) return null;
  return sessionStorage.getItem(ACCESS_TOKEN_KEY);
}

export function getRefreshToken(): string | null {
  if (!isBrowser()) return null;
  return sessionStorage.getItem(REFRESH_TOKEN_KEY);
}

/** True only when a token is present *and* has not already expired. */
export function isAuthenticated(): boolean {
  const token = getAccessToken();
  if (!token) return false;

  const expiresAt = sessionStorage.getItem(EXPIRES_AT_KEY);
  if (!expiresAt) return false;

  // The backend is expected to serialise the expiry as UTC; append the
  // marker when it is missing so Date does not read it as local time and
  // over-report validity (same defensive parsing as admin-web/customer-web).
  const normalised = /[Zz]|[+-]\d{2}:\d{2}$/.test(expiresAt)
    ? expiresAt
    : `${expiresAt}Z`;

  return new Date(normalised).getTime() > Date.now();
}

export function subscribeToAuthChanges(listener: () => void): () => void {
  if (!isBrowser()) return () => undefined;
  window.addEventListener(AUTH_CHANGED_EVENT, listener);
  return () => window.removeEventListener(AUTH_CHANGED_EVENT, listener);
}

/**
 * Raw JWT payload shape, defensive about which claim key the token actually
 * uses - provider-api's exact claim names are set by a concurrently-built
 * sibling task, so both the short JWT claim names and ASP.NET Core's default
 * long XML-schema URIs are checked (same defensive approach as admin-web).
 */
interface RawProviderTokenPayload {
  sub?: string;
  nameid?: string;
  phone_number?: string;
  mobile?: string;
  "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/mobilephone"?: string;
  [claim: string]: unknown;
}

/**
 * Reads the provider's identity off the current access token for "signed in
 * as" display (see components/ProviderHeader.tsx). Returns null when there is
 * no token or it cannot be decoded.
 */
export function getSessionClaims(): ProviderSessionClaims | null {
  const token = getAccessToken();
  if (!token) return null;

  const payload = decodeJwtPayload<RawProviderTokenPayload>(token);
  if (!payload) return null;

  return {
    subject: payload.sub ?? payload.nameid ?? null,
    mobile:
      payload.phone_number ??
      payload.mobile ??
      payload["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/mobilephone"] ??
      null,
  };
}
