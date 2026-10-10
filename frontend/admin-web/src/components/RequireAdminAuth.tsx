"use client";

import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import type { ReactNode } from "react";
import { Spinner } from "@/components/ui";
import { refreshAccessToken } from "@/lib/api";
import { getRefreshToken, isAuthenticated, subscribeToAuthChanges } from "@/lib/auth";

/**
 * Client-side guard for the authenticated admin shell (task 98d, SRS 25.2).
 *
 * Mirrors customer-web/src/components/RequireAuth.tsx. This is a usability
 * measure, not a security boundary: the token lives in the browser, so
 * anything rendered here is reachable by a determined user. The actual
 * enforcement is the [Authorize] attribute on the Admin API - every request
 * these screens make is rejected server-side without a valid JWT, and
 * lib/api.ts's apiFetch clears the local session the moment the server says
 * a token is no longer good (401), which this guard reacts to immediately
 * via subscribeToAuthChanges - covering both "never logged in" and
 * "was logged in, token just expired/was revoked" in the same code path.
 */
// Flips true after this tab's first client render commits - see
// customer-web/src/components/RequireAuth.tsx for why `typeof window` alone
// isn't enough (it's defined during hydration too, not just after it, so
// reading real auth state on the first paint would mismatch the `undefined`
// the server rendered).
let hasClientRendered = false;

export function RequireAdminAuth({ children }: { children: ReactNode }) {
  const router = useRouter();
  const [authed, setAuthed] = useState<boolean | undefined>(() =>
    hasClientRendered ? isAuthenticated() : undefined,
  );

  useEffect(() => {
    hasClientRendered = true;

    // If the access token has already expired at mount time (routine after
    // any break away from the tab) but a refresh token is still stored, try
    // one silent refresh before treating this as a real sign-out - matches
    // what lib/api.ts's performFetch already does mid-session on a 401. Only
    // a genuinely missing refresh token, or a refresh call that itself fails
    // (expired/revoked), should bounce to /login. Mirrors
    // customer-web/src/components/RequireAuth.tsx's sync logic.
    const sync = () => {
      if (isAuthenticated()) {
        setAuthed(true);
        return;
      }
      if (!getRefreshToken()) {
        setAuthed(false);
        return;
      }
      void refreshAccessToken().then((refreshed) => {
        setAuthed(refreshed);
      });
    };
    sync();
    return subscribeToAuthChanges(sync);
  }, []);

  useEffect(() => {
    if (authed === false) {
      router.replace("/login");
    }
  }, [authed, router]);

  if (authed === undefined || !authed) {
    // aria-live so a screen reader hears the transition rather than landing on
    // a silently-changed page; polite because neither state is an alert.
    return (
      <p
        aria-live="polite"
        className="flex items-center gap-2 p-8 text-sm text-fg-muted"
      >
        <Spinner />
        {authed === undefined ? "Loading…" : "Redirecting to sign in…"}
      </p>
    );
  }

  return <>{children}</>;
}
