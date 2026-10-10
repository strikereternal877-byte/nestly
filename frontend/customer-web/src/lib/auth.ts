"use client";

import { browserStorageEnvironment, removeFromBothStores, sessionStoreFor } from "./session-storage";
import type { KeyValueStorage } from "./session-storage";
import type { LoginResponse } from "./types";

/**
 * Client-side session storage.
 *
 * Known limitation: tokens held in Web Storage are readable by any script on
 * the origin, so an XSS bug becomes a session-theft bug. The backend currently
 * returns the token pair in the response body (see AuthController), so there
 * is no httpOnly cookie to use instead. In a browser tab the session is kept
 * in sessionStorage rather than localStorage, which narrows the window: it
 * dies with the tab. An app installed to the home screen has no tab to keep
 * open, so there it is kept in localStorage and the customer stays signed in
 * (see session-storage.ts). Moving issuance to a Set-Cookie header is the real
 * fix and is tracked as hardening work, not something this client can do on
 * its own.
 */
const ACCESS_TOKEN_KEY = "nestly.accessToken";
const REFRESH_TOKEN_KEY = "nestly.refreshToken";
const EXPIRES_AT_KEY = "nestly.accessTokenExpiresAt";

/** Notifies subscribed components (the header, guards) that auth state moved. */
const AUTH_CHANGED_EVENT = "nestly:auth-changed";

function isBrowser(): boolean {
  return typeof window !== "undefined";
}

/** The store the session lives in right now: the tab's in a browser, the device's in an installed app. */
function activeStore(): KeyValueStorage {
  return sessionStoreFor(browserStorageEnvironment());
}

export function storeSession(session: LoginResponse): void {
  if (!isBrowser()) return;
  const store = activeStore();
  store.setItem(ACCESS_TOKEN_KEY, session.accessToken);
  store.setItem(REFRESH_TOKEN_KEY, session.refreshToken);
  store.setItem(EXPIRES_AT_KEY, session.accessTokenExpiresAtUtc);
  window.dispatchEvent(new Event(AUTH_CHANGED_EVENT));
}

export function clearSession(): void {
  if (!isBrowser()) return;
  removeFromBothStores(browserStorageEnvironment(), [ACCESS_TOKEN_KEY, REFRESH_TOKEN_KEY, EXPIRES_AT_KEY]);
  window.dispatchEvent(new Event(AUTH_CHANGED_EVENT));
}

export function getAccessToken(): string | null {
  if (!isBrowser()) return null;
  return activeStore().getItem(ACCESS_TOKEN_KEY);
}

export function getRefreshToken(): string | null {
  if (!isBrowser()) return null;
  return activeStore().getItem(REFRESH_TOKEN_KEY);
}

/** True only when a token is present *and* has not already expired. */
export function isAuthenticated(): boolean {
  const token = getAccessToken();
  if (!token) return false;

  const expiresAt = activeStore().getItem(EXPIRES_AT_KEY);
  if (!expiresAt) return false;

  // The backend serialises the expiry as UTC; append the marker when it is
  // missing so Date does not read it as local time and over-report validity.
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
 * Validates a `?redirect=` query value before navigating to it. The value
 * comes straight off the URL, so it must be constrained to a same-origin
 * relative path - passing it through unchecked would make this an open
 * redirect (`redirect=https://evil.example` or the protocol-relative
 * `redirect=//evil.example`, which browsers treat as absolute).
 */
export function safeRedirectTarget(raw: string | null): string | null {
  if (!raw) return null;
  if (!raw.startsWith("/") || raw.startsWith("//") || raw.includes("://")) return null;
  return raw;
}
