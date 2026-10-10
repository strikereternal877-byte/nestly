"use client";

import { useState, useSyncExternalStore } from "react";
import { Alert, IconButton } from "@/components/ui";

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

/**
 * Explicit offline state (task #355). Before this, a customer who lost
 * connectivity got whatever each individual data-fetching hook happened to
 * render for its own failed request — a generic "something went wrong" on
 * one screen, a silently-stale list on another — with nothing telling them
 * the actual, single cause. This is a `navigator.onLine` + `online`/`offline`
 * listener mounted once near the app root, not a retry/queue/offline-cache
 * architecture: it only ever answers one question ("is the browser currently
 * reporting a connection?") and lets every screen keep failing however it
 * already does.
 *
 * `navigator.onLine` is a browser-reported signal, not a real reachability
 * check (a captive portal or a dead upstream link both report `true`), so
 * this deliberately undersells itself as "You're offline" rather than
 * "You have no internet" — it can go wrong in the optimistic direction, never
 * the alarming one.
 */
export function OfflineBanner() {
  const isOnline = useSyncExternalStore(subscribeToOnlineStatus, getClientOnlineStatus, getServerOnlineStatus);
  const [dismissed, setDismissed] = useState(false);

  // "Adjusting state when a prop changes", not an effect (react.dev/learn/
  // you-might-not-need-an-effect#adjusting-some-state-when-a-prop-changes):
  // setState during render, guarded by comparing against the last-seen
  // value, is what reacts to reconnecting-then-dropping-again without
  // running on every render - a fresh drop deserves a fresh banner even if
  // the last one was dismissed, but this must not immediately undo a
  // dismissal for the *same* outage.
  const [prevIsOnline, setPrevIsOnline] = useState(isOnline);
  if (isOnline !== prevIsOnline) {
    setPrevIsOnline(isOnline);
    if (!isOnline) setDismissed(false);
  }

  if (isOnline || dismissed) return null;

  return (
    // Task #351: `top-[4.5rem]` tracked `SiteHeader`'s fixed height so this
    // sits flush below it; that header now grows by
    // `env(safe-area-inset-top)` on a notched phone, so this offset must
    // grow by the same amount to stay flush rather than overlapping it.
    <div className="fixed inset-x-0 top-[calc(4.5rem+env(safe-area-inset-top))] z-30 px-4 pt-2 sm:px-6">
      <div className="mx-auto w-full max-w-7xl">
        <Alert
          tone="warning"
          action={
            <IconButton label="Dismiss" onClick={() => setDismissed(true)}>
              <svg
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                strokeWidth="2"
                strokeLinecap="round"
                className="h-4 w-4"
                aria-hidden
              >
                <path d="M18 6 6 18M6 6l12 12" />
              </svg>
            </IconButton>
          }
        >
          You&apos;re offline — some pages may not load until your connection comes back.
        </Alert>
      </div>
    </div>
  );
}
