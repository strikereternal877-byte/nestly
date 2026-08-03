"use client";

import { useEffect, useState } from "react";
import type { ReactNode } from "react";
import { PartnerHeader } from "@/components/PartnerHeader";
import { PartnerSidebar, PartnerTabBar } from "@/components/PartnerSidebar";
import { RequirePartnerAuth } from "@/components/RequirePartnerAuth";
import { getSessionClaims, subscribeToAuthChanges } from "@/lib/auth";
import type { PartnerSessionClaims } from "@/lib/types";

/**
 * Authenticated app shell: header + navigation + content area, shown once
 * signed in. Every route nested under the `(partner)` route group (this
 * segment does not appear in the URL) automatically gets this chrome and
 * the RequirePartnerAuth guard. Mirrors admin-web's `(admin)/layout.tsx`.
 *
 * Navigation is a side rail from `md` up and a bottom tab bar below it -
 * partners work from a phone in the field, so the four sections stay one
 * thumb-tap away rather than behind a drawer.
 */
export default function AuthenticatedLayout({ children }: { children: ReactNode }) {
  const [claims, setClaims] = useState<PartnerSessionClaims | null>(null);

  useEffect(() => {
    const sync = () => setClaims(getSessionClaims());
    sync();
    return subscribeToAuthChanges(sync);
  }, []);

  return (
    <RequirePartnerAuth>
      <div className="flex min-h-screen flex-col bg-bg">
        <PartnerHeader claims={claims} />

        <div className="flex flex-1">
          <PartnerSidebar />

          {/* Bottom padding clears the fixed tab bar so the last element on a
              page is never trapped underneath it. */}
          <main className="min-w-0 flex-1 px-4 py-6 pb-24 sm:px-6 md:pb-6 lg:px-8">
            <div className="mx-auto w-full max-w-5xl">{children}</div>
          </main>
        </div>

        <PartnerTabBar />
      </div>
    </RequirePartnerAuth>
  );
}
