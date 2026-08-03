"use client";

import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { ThemeToggle } from "@/components/ThemeToggle";
import { cx } from "@/components/ui";
import { API_V1, apiFetch } from "@/lib/api";
import { clearSession, getRefreshToken } from "@/lib/auth";
import type { ProviderSessionClaims } from "@/lib/types";

/** Top chrome bar for the authenticated provider shell. Mirrors admin-web's AdminHeader. */
export function ProviderHeader({ claims }: { claims: ProviderSessionClaims | null }) {
  const router = useRouter();
  const [menuOpen, setMenuOpen] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!menuOpen) return;

    const onPointerDown = (event: MouseEvent) => {
      if (!menuRef.current?.contains(event.target as Node)) setMenuOpen(false);
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") setMenuOpen(false);
    };

    document.addEventListener("mousedown", onPointerDown);
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("mousedown", onPointerDown);
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [menuOpen]);

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

  const mobile = claims?.mobile ?? null;

  return (
    <header className="sticky top-0 z-40 flex h-16 shrink-0 items-center gap-3 border-b border-line bg-bg/80 px-4 backdrop-blur-md sm:px-6">
      <span className="flex items-center gap-2">
        <span
          aria-hidden
          className="flex h-8 w-8 items-center justify-center rounded-lg bg-brand-gradient text-fg-on-brand shadow-brand"
        >
          <svg viewBox="0 0 24 24" fill="none" className="h-[18px] w-[18px]">
            <path
              d="M4 11.5 12 5l8 6.5V19a1 1 0 0 1-1 1h-4v-5h-6v5H5a1 1 0 0 1-1-1v-7.5Z"
              fill="currentColor"
            />
          </svg>
        </span>
        <span className="text-[0.9375rem] font-semibold tracking-tight text-fg">
          Nestly <span className="text-fg-muted">Provider</span>
        </span>
      </span>

      <div className="flex-1" />

      <ThemeToggle />

      <div ref={menuRef} className="relative">
        <button
          type="button"
          onClick={() => setMenuOpen((current) => !current)}
          aria-haspopup="menu"
          aria-expanded={menuOpen}
          className="inline-flex h-9 items-center gap-2 rounded-lg border border-line bg-surface px-2 text-sm text-fg shadow-xs transition-colors duration-fast ease-out hover:border-line-strong hover:bg-surface-2"
        >
          <span className="flex h-6 w-6 items-center justify-center rounded-full bg-brand-600 text-fg-on-brand">
            <svg
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              className="h-3.5 w-3.5"
              aria-hidden
            >
              <circle cx="12" cy="8" r="3.5" />
              <path d="M5 20a7 7 0 0 1 14 0" strokeLinecap="round" />
            </svg>
          </span>
          <span className="hidden sm:inline">{mobile ?? "Account"}</span>
          <svg
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            strokeLinecap="round"
            className={cx(
              "h-3.5 w-3.5 text-fg-subtle transition-transform duration-fast",
              menuOpen && "rotate-180",
            )}
            aria-hidden
          >
            <path d="m6 9 6 6 6-6" />
          </svg>
        </button>

        {menuOpen ? (
          <div
            role="menu"
            className="absolute right-0 top-full z-50 mt-2 w-56 animate-pop overflow-hidden rounded-xl border border-line bg-surface p-1.5 shadow-lg"
          >
            {mobile ? (
              <>
                <p className="truncate px-3 py-2 text-sm font-medium text-fg">{mobile}</p>
                <div className="my-1 border-t border-line" />
              </>
            ) : null}
            <button
              type="button"
              role="menuitem"
              onClick={signOut}
              className="block w-full rounded-lg px-3 py-2 text-left text-sm text-danger transition-colors duration-fast ease-out hover:bg-danger-soft"
            >
              Sign out
            </button>
          </div>
        ) : null}
      </div>
    </header>
  );
}
