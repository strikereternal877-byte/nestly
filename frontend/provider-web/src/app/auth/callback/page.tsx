"use client";

import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { Alert, PageHeading } from "@/components/ui";
import { storeSession } from "@/lib/auth";
import type { ProviderLoginResponse } from "@/lib/types";

const DEFAULT_DESTINATION = "/jobs";

/**
 * Receives a session handed off from the unified login entry point
 * (customer-web's `/login`, task 206) after a successful provider sign-in
 * there. There is no shared cookie domain across the three frontends
 * (docs/DEVOPS.md's hosting/domain decisions are still open), so the token
 * travels in the URL fragment - never sent to any server, stripped from
 * history the instant it's read - rather than a query string. This page's
 * only job is to move that fragment into this origin's own session storage
 * via the existing `storeSession`, exactly as this app's own `/login` page
 * does after calling provider-api directly.
 */
export default function AuthCallbackPage() {
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const fragment = new URLSearchParams(window.location.hash.slice(1));
    const accessToken = fragment.get("accessToken");
    const refreshToken = fragment.get("refreshToken");
    const accessTokenExpiresAtUtc = fragment.get("accessTokenExpiresAtUtc");
    const next = new URLSearchParams(window.location.search).get("next") || DEFAULT_DESTINATION;

    // Strip the fragment immediately, whether or not it parsed - a token
    // must never sit in browser history longer than this one tick.
    window.history.replaceState(null, "", window.location.pathname + window.location.search);

    if (!accessToken || !refreshToken || !accessTokenExpiresAtUtc) {
      setError("Sign-in link is missing or has expired. Please sign in again.");
      return;
    }

    const session: ProviderLoginResponse = { accessToken, refreshToken, accessTokenExpiresAtUtc };
    storeSession(session);
    router.replace(next);
  }, [router]);

  return (
    <main className="mx-auto flex min-h-screen w-full max-w-md flex-col justify-center px-6 py-12">
      <PageHeading title="Signing you in…" />
      {error ? (
        <Alert>
          {error}{" "}
          <a href="/login" className="underline">
            Go to sign in
          </a>
          .
        </Alert>
      ) : null}
    </main>
  );
}
