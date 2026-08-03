"use client";

import { useEffect, useState } from "react";
import type { ReactNode } from "react";
import { ProviderHeader } from "@/components/ProviderHeader";
import { ProviderSidebar } from "@/components/ProviderSidebar";
import { RequireProviderAuth } from "@/components/RequireProviderAuth";
import { getSessionClaims, subscribeToAuthChanges } from "@/lib/auth";
import type { ProviderSessionClaims } from "@/lib/types";

/**
 * Authenticated app shell: header + sidebar + content area, shown once
 * signed in. Every route nested under the `(provider)` route group (this
 * segment does not appear in the URL) automatically gets this chrome and
 * the RequireProviderAuth guard. Mirrors admin-web's `(admin)/layout.tsx`.
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
      <div className="flex min-h-screen flex-col">
        <ProviderHeader claims={claims} />
        <div className="flex flex-1">
          <ProviderSidebar />
          <main className="flex-1 p-6">{children}</main>
        </div>
      </div>
    </RequireProviderAuth>
  );
}
