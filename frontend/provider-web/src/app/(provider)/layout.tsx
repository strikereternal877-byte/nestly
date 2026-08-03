"use client";

import { useEffect, useState } from "react";
import type { ReactNode } from "react";
import { ProviderHeader } from "@/components/ProviderHeader";
import { ProviderSidebar, ProviderTabBar } from "@/components/ProviderSidebar";
import { RequireProviderAuth } from "@/components/RequireProviderAuth";
import { getSessionClaims, subscribeToAuthChanges } from "@/lib/auth";
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
  const [claims, setClaims] = useState<ProviderSessionClaims | null>(null);

  useEffect(() => {
    const sync = () => setClaims(getSessionClaims());
    sync();
    return subscribeToAuthChanges(sync);
  }, []);

  return (
    <RequireProviderAuth>
      <div className="flex min-h-screen flex-col bg-bg">
        <ProviderHeader claims={claims} />

        <div className="flex flex-1">
          <ProviderSidebar />

          {/* Bottom padding clears the fixed tab bar so the last element on a
              page is never trapped underneath it. */}
          <main className="min-w-0 flex-1 px-4 py-6 pb-24 sm:px-6 md:pb-6 lg:px-8">
            <div className="mx-auto w-full max-w-5xl">{children}</div>
          </main>
        </div>

        <ProviderTabBar />
      </div>
    </RequireProviderAuth>
  );
}
