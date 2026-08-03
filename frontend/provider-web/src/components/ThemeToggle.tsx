"use client";

import { useEffect, useState } from "react";
import {
  getThemePreference,
  resolveTheme,
  setThemePreference,
  subscribeToTheme,
} from "@/lib/theme";

/**
 * Light/dark switch for the app chrome.
 *
 * Renders a fixed-size placeholder until mounted: the server has no way to
 * know which theme the pre-paint script chose, so rendering the real icon on
 * the server would guarantee a hydration mismatch. Reserving the space keeps
 * the header from shifting when the real control appears.
 */
export function ThemeToggle({ className = "" }: { className?: string }) {
  const [mounted, setMounted] = useState(false);
  const [theme, setTheme] = useState<"light" | "dark">("light");

  useEffect(() => {
    const sync = () => setTheme(resolveTheme(getThemePreference()));
    sync();
    setMounted(true);
    return subscribeToTheme(sync);
  }, []);

  if (!mounted) {
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
