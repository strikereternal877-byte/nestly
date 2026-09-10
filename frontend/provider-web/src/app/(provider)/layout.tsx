"use client";

import { usePathname } from "next/navigation";
import { useEffect, useState } from "react";
import type { ReactNode } from "react";
import { GoLiveBanner } from "@/components/GoLiveBanner";
import { OfflineBanner } from "@/components/OfflineBanner";
import { STICKY_BAR_SPACER } from "@/components/patterns";
import { ProviderHeader } from "@/components/ProviderHeader";
import { isJobDetailPath, ProviderSidebar, ProviderTabBar } from "@/components/ProviderSidebar";
import { RequireProviderAuth } from "@/components/RequireProviderAuth";
import { cx } from "@/components/ui";
import { getSessionClaims, subscribeToAuthChanges } from "@/lib/auth";
import { DevicePlatform, registerDeviceToken, storeDeviceTokenId } from "@/lib/device-tokens-api";
import { requestPushToken } from "@/lib/push";
import type { ProviderSessionClaims } from "@/lib/types";

/**
 * Authenticated app shell: header + navigation + content area, shown once
 * signed in. Every route nested under the `(provider)` route group (this
 * segment does not appear in the URL) automatically gets this chrome and
 * the RequireProviderAuth guard. Mirrors admin-web's `(admin)/layout.tsx`.
 *
 * Navigation is a side rail from `md` up and a bottom tab bar below it -
 * providers work from a phone in the field, so the four sections stay one
 * thumb-tap away rather than behind a drawer.
 */
export default function AuthenticatedLayout({ children }: { children: ReactNode }) {
  const pathname = usePathname();
  const [claims, setClaims] = useState<ProviderSessionClaims | null>(null);
  const isJobDetail = isJobDetailPath(pathname);

  useEffect(() => {
    const sync = () => setClaims(getSessionClaims());
    sync();
    return subscribeToAuthChanges(sync);
  }, []);

  // Fires once per mount of the authenticated shell (i.e. once per sign-in,
  // since this layout unmounts on sign-out) - job offers are time-sensitive
  // (task 307), so push registration happens as soon as the provider is in
  // rather than waiting for them to visit a specific screen. No-ops entirely
  // when Firebase is not configured or the browser declines - see
  // lib/push.ts.
  useEffect(() => {
    let cancelled = false;

    void (async () => {
      const token = await requestPushToken();
      if (cancelled || !token) return;

      try {
        const registered = await registerDeviceToken(DevicePlatform.Fcm, token);
        if (!cancelled) storeDeviceTokenId(registered.id);
      } catch {
        // Best-effort: a provider with no working push is still a working
        // provider. Nothing here blocks any other part of the app.
      }
    })();

    return () => {
      cancelled = true;
    };
  }, []);

  return (
    <RequireProviderAuth>
      <div className="flex min-h-screen bg-bg">
        <ProviderSidebar />

        {/* bg-surface (white): matches the MatDash reference's
            `body-wrapper` class (`bg-white dark:bg-darkgray`) exactly. */}
        <div className="flex min-w-0 flex-1 flex-col bg-surface">
          {/* One sticky ancestor for both rows, not two independent
              `sticky top-0` siblings - see OfflineBanner's header comment for
              why that distinction matters once the page is scrolled.
              Task #351: `pt-[env(safe-area-inset-top)]` lives here, on the
              shared ancestor, rather than on `OfflineBanner`/`ProviderHeader`
              individually - whichever of the two is topmost varies (the
              banner only when offline), and this app installs as a standalone
              PWA (manifest.json's `display: "standalone"`), so whichever one
              is first needs the same clearance from a notch/punch-hole
              camera. A single ancestor padding handles both without double-
              padding when both are stacked and visible together. */}
          <div className="sticky top-0 z-40 pt-[env(safe-area-inset-top)]">
            <OfflineBanner />
            <ProviderHeader claims={claims} />
          </div>

          {/* Bottom padding clears whatever is fixed to the viewport's
              bottom edge so the last element on a page is never trapped
              underneath it: the tab bar everywhere else, the job detail
              screen's (taller, up-to-two-button) StickyActionBar there. */}
          {/* bg-bg: the MatDash reference nests a tinted scroll canvas
              behind the page's cards inside the white body-wrapper column -
              that's what makes a borderless white Card read as a distinct
              surface instead of blending into the page. */}
          <main
            className={cx(
              "min-w-0 flex-1 bg-bg px-4 py-6 sm:px-6 lg:px-8",
              isJobDetail ? STICKY_BAR_SPACER : "pb-24 md:pb-6",
            )}
          >
            <div className="mx-auto w-full max-w-5xl">
              {/* docs/OPEN-FIXES-FEATURES.csv "Onboarding checklist and
                  go-live status": replaces the old PendingVerification-only
                  message with every specific prerequisite still missing (KYC,
                  skills, service areas, availability) - renders nothing once
                  the provider is fully go-live ready. */}
              <GoLiveBanner />
              {children}
            </div>
          </main>
        </div>

        <ProviderTabBar />
      </div>
    </RequireProviderAuth>
  );
}
