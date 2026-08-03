"use client";

import { useRouter } from "next/navigation";
import { API_V1, apiFetch } from "@/lib/api";
import { clearSession, getRefreshToken } from "@/lib/auth";
import type { ProviderSessionClaims } from "@/lib/types";

/** Top chrome bar for the authenticated provider shell. Mirrors admin-web's AdminHeader. */
export function ProviderHeader({ claims }: { claims: ProviderSessionClaims | null }) {
  const router = useRouter();

  const signOut = async () => {
    const refreshToken = getRefreshToken();

    // Clear locally regardless of the server's answer: the provider asked to
    // be signed out, and a network failure must not leave the token behind.
    try {
      if (refreshToken) {
        await apiFetch(`${API_V1}/auth/logout`, {
          method: "POST",
          body: JSON.stringify({ refreshToken }),
        });
      }
    } catch {
      // Already-invalid tokens are a no-op server-side; nothing to report.
    } finally {
      clearSession();
      router.push("/login");
    }
  };

  return (
    <header className="flex items-center justify-between border-b border-black/10 px-6 py-4 dark:border-white/15">
      <span className="font-semibold tracking-tight">Nestly Provider</span>

      <div className="flex items-center gap-4 text-sm">
        {claims?.mobile ? (
          <span className="text-neutral-600 dark:text-neutral-400">{claims.mobile}</span>
        ) : null}
        <button type="button" onClick={signOut} className="hover:underline">
          Sign out
        </button>
      </div>
    </header>
  );
}
