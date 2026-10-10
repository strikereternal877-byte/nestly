"use client";

import { useSyncExternalStore } from "react";

/**
 * Task #355: a provider standing at a job site with patchy signal needs to be
 * told the app has lost connectivity, not left staring at a button that never
 * stops spinning. Backed by the browser's own connectivity signal
 * (`navigator.onLine` plus the `online`/`offline` window events) rather than
 * a ping loop - it costs nothing, and it is the same signal every mutation's
 * own network error already degrades to, so this only makes an existing
 * condition visible sooner rather than inventing a new one.
 *
 * `sticky top-0` rather than `fixed`: both `AuthenticatedLayout` and
 * `AuthShell` render this immediately above their own `sticky top-0` header
 * (the provider chrome bar; nothing, on the auth screens). Wrapping it and
 * that header in one `sticky` ancestor - see `AuthenticatedLayout` - lets
 * them scroll off and stick back together as a unit; two *independent*
 * `sticky top-0` siblings would fight for the same viewport row once both
 * are stuck, each rendering at `top: 0` unaware of the other's height.
 * Self-contained here (rather than requiring every caller to supply that
 * wrapper) since only one of this component's two mount points has another
 * sticky element to coordinate with.
 */
export function OfflineBanner() {
  const isOnline = useOnlineStatus();

  if (isOnline) return null;

  return (
    <div
      role="status"
      aria-live="polite"
      className="sticky top-0 z-40 flex items-center justify-center gap-2 bg-warning px-4 py-2 text-center text-xs font-medium text-bg sm:text-sm"
    >
      <svg
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinecap="round"
        strokeLinejoin="round"
        className="h-4 w-4 shrink-0"
        aria-hidden
      >
        <path d="M2 8.5a16.9 16.9 0 0 1 20 0M5 12.5a12 12 0 0 1 14 0M8.5 16.5a7 7 0 0 1 7 0" />
        <path d="M2 2l20 20" />
        <circle cx="12" cy="20" r="1" fill="currentColor" stroke="none" />
      </svg>
      You&apos;re offline — actions won&apos;t save until your connection is back.
    </div>
  );
}

function subscribeToOnlineStatus(onChange: () => void): () => void {
  window.addEventListener("online", onChange);
  window.addEventListener("offline", onChange);
  return () => {
    window.removeEventListener("online", onChange);
    window.removeEventListener("offline", onChange);
  };
}

function getClientOnlineStatus(): boolean {
  return navigator.onLine;
}

/** Defaults to online during SSR/hydration - `navigator` doesn't exist on the server, and a false "offline" flash on every load would be worse than a brief miss on an actually-offline first paint. */
function getServerOnlineStatus(): boolean {
  return true;
}

/** True once the browser reports no network. */
function useOnlineStatus(): boolean {
  return useSyncExternalStore(subscribeToOnlineStatus, getClientOnlineStatus, getServerOnlineStatus);
}
