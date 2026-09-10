"use client";

import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import type { ReactNode } from "react";
import { ScreenSkeleton } from "@/components/states";
import { isAuthenticated, subscribeToAuthChanges } from "@/lib/auth";

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
 * immediately via subscribeToAuthChanges - covering both "never logged in"
 * and "was logged in, token just expired/was revoked" in the same code path.
 */
// Flips true after this tab's first client render commits - see
// customer-web/src/components/RequireAuth.tsx for why `typeof window` alone
// isn't enough (it's defined during hydration too, not just after it, so
// reading real auth state on the first paint would mismatch the `undefined`
// the server rendered).
let hasClientRendered = false;

export function RequireProviderAuth({ children }: { children: ReactNode }) {
  const router = useRouter();
  const [authed, setAuthed] = useState<boolean | undefined>(() =>
    hasClientRendered ? isAuthenticated() : undefined,
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
    const sync = () => setAuthed(isAuthenticated());
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
