"use client";

import { useRouter } from "next/navigation";
import { useEffect } from "react";
import { ScreenSkeleton } from "@/components/states";
import { isAuthenticated } from "@/lib/auth";

/**
 * The provider portal has no public landing page - send the visitor straight
 * to the Today screen (if a live session exists, the job-at-hand landing
 * screen - docs/OPEN-FIXES-FEATURES.csv "Today / Now screen") or the login
 * screen otherwise. Mirrors admin-web's root page.
 */
export default function RootPage() {
  const router = useRouter();

  useEffect(() => {
    router.replace(isAuthenticated() ? "/today" : "/login");
  }, [router]);

  // The redirect fires in an effect, so this frame is always painted. Showing
  // the Today-screen shape rather than a bare line means the entry point does
  // not flash a text stub before the real screen mounts.
  return (
    <ScreenSkeleton>
      <p role="status" className="text-sm text-fg-muted">
        Taking you to today&apos;s job…
      </p>
    </ScreenSkeleton>
  );
}
