"use client";

import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import type { ReactNode } from "react";
import { ScreenSkeleton } from "@/components/states";
import { refreshAccessToken } from "@/lib/api";
import { getRefreshToken, isAuthenticated, subscribeToAuthChanges } from "@/lib/auth";

/**
 * Client-side guard for the authenticated provider shell.
 *
 * Mirrors admin-web/src/components/RequireAdminAuth.tsx. This is a
 * usability measure, not a security boundary: the token lives in the
 * browser, so anything rendered here is reachable by a determined user. The
 * actual enforcement is the [Authorize] attribute on the Provider API - every
 * request these screens make is rejected server-side without a valid JWT,
 * and lib/api.ts's apiFetch clears the local session the moment the server
 * says a token is no longer good (401), which this guard reacts to
 * immediately via subscribeToAuthChanges - covering "never logged in" and
 * "was logged in, token was revoked mid-visit" (a real 401 arrived and
 * apiFetch's own refresh attempt already failed) in the same code path.
 *
 * A *locally* expired access token is a third case apiFetch's reactive path
 * doesn't cover: if nothing happened to be mid-API-call right when the
 * token's short lifetime elapsed - a provider who just reopened the tab
 * after being away, not one actively using it - the first thing to notice
 * is this guard's own isAuthenticated() check, with no 401 involved at all.
 * That used to redirect straight to /login?reason=expired without ever
 * trying the refresh token sitting right there in storage. This guard now
 * attempts refreshAccessToken() itself first in that case (see the mount
 * effect below) - the same refresh apiFetch uses, so both paths converge
 * on one outcome instead of the guard silently giving up sooner than
 * apiFetch would have.
 */
// Flips true after this tab's first client render commits - see
// customer-web/src/components/RequireAuth.tsx for why `typeof window` alone
// isn't enough (it's defined during hydration too, not just after it, so
// reading real auth state on the first paint would mismatch the `undefined`
// the server rendered).
let hasClientRendered = false;

export function RequireProviderAuth({ children }: { children: ReactNode }) {
  const router = useRouter();
  // Only the confidently-authenticated fast path skips the loading skeleton
  // on a same-tab remount; anything else (never signed in, or locally
  // expired and possibly refreshable) starts undefined so the redirect
  // effect below can't fire off a stale `false` before the mount effect's
  // refresh attempt has had a chance to run.
  const [authed, setAuthed] = useState<boolean | undefined>(() =>
    hasClientRendered && isAuthenticated() ? true : undefined,
  );
  // Whether this tab actually held a live session before `authed` most
  // recently flipped to false - distinguishes "the session just expired /
  // was revoked mid-visit" from "nobody was ever signed in on this tab"
  // (e.g. a bookmarked authenticated URL opened cold). Only the former is a
  // session-expiry event worth surfacing on /login (see docs/OPEN-FIXES-
  // FEATURES.csv "Session expiry messaging") - the login page must not tell
  // a first-time visitor their session "expired" when none existed.
  const wasAuthedRef = useRef(false);

  useEffect(() => {
    hasClientRendered = true;

    // If the access token has already expired at mount time (very common -
    // it only lives 15 minutes, so any page load after a short break lands
    // here) but a refresh token is still stored, try one silent refresh
    // before treating this as a real sign-out. Only a refresh token that's
    // actually missing, or a refresh call that itself fails (expired/
    // revoked), should bounce to /login - a technically-expired access token
    // alone must not.
    const sync = () => {
      if (isAuthenticated()) {
        setAuthed(true);
        return;
      }
      if (!getRefreshToken()) {
        setAuthed(false);
        return;
      }
      // A stored refresh token means this tab did hold a real session before
      // now, even though `authed` itself hasn't been `true` yet this mount -
      // mark it so a refresh failure below reads as "session expired", not
      // "never signed in" (see wasAuthedRef's own comment).
      wasAuthedRef.current = true;
      void refreshAccessToken().then((refreshed) => {
        setAuthed(refreshed);
      });
    };
    sync();
    return subscribeToAuthChanges(sync);
  }, []);

  useEffect(() => {
    if (authed === true) {
      wasAuthedRef.current = true;
      return;
    }
    if (authed === false) {
      router.replace(wasAuthedRef.current ? "/login?reason=expired" : "/login");
    }
  }, [authed, router]);

  // A bare "Loading…" line here reflowed the whole shell the moment the
  // session resolved, on every authenticated route in the app.
  if (authed === undefined) {
    return <ScreenSkeleton />;
  }

  if (!authed) {
    return (
      <ScreenSkeleton>
        <p role="status" className="text-sm text-fg-muted">
          Redirecting you to sign in…
        </p>
      </ScreenSkeleton>
    );
  }

  return <>{children}</>;
}
