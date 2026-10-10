"use client";

import { useSyncExternalStore } from "react";
import {
  getThemePreference,
  resolveTheme,
  setThemePreference,
  subscribeToTheme,
} from "@/lib/theme";

function getClientTheme(): "light" | "dark" {
  return resolveTheme(getThemePreference());
}

/**
 * null, not a real theme: the server has no way to know which theme the
 * pre-paint script chose, so this is what tells the placeholder branch below
 * apart from a real, hydrated theme - rendering a guessed theme on the
 * server would guarantee a hydration mismatch.
 */
function getServerTheme(): "light" | "dark" | null {
  return null;
}

/**
 * Light/dark switch for the app chrome.
 *
 * useSyncExternalStore, not a mount-flag + effect: this is exactly the
 * "external source that can genuinely differ between server and client"
 * case it exists for, and it reads as one derived value instead of two
 * pieces of state kept in sync by hand. Renders a fixed-size placeholder
 * until the real (client) snapshot is available, so the header doesn't
 * shift when the icon appears.
 */
export function ThemeToggle({ className = "" }: { className?: string }) {
  const theme = useSyncExternalStore(subscribeToTheme, getClientTheme, getServerTheme);

  if (theme === null) {
    return <div className={`h-9 w-9 ${className}`} aria-hidden />;
  }

  const next = theme === "dark" ? "light" : "dark";

  return (
    <button
      type="button"
      onClick={() => setThemePreference(next)}
      aria-label={`Switch to ${next} theme`}
      title={`Switch to ${next} theme`}
      className={`inline-flex h-9 w-9 items-center justify-center rounded-lg text-fg-muted transition-colors duration-fast ease-out hover:bg-surface-3 hover:text-fg ${className}`}
    >
      {theme === "dark" ? <SunIcon /> : <MoonIcon />}
    </button>
  );
}

function SunIcon() {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.75"
      strokeLinecap="round"
      className="h-[18px] w-[18px]"
      aria-hidden
    >
      <circle cx="12" cy="12" r="4" />
      <path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4" />
    </svg>
  );
}

function MoonIcon() {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.75"
      strokeLinecap="round"
      strokeLinejoin="round"
      className="h-[18px] w-[18px]"
      aria-hidden
    >
      <path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8Z" />
    </svg>
  );
}
