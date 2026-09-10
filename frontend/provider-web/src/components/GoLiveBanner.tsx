"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { Alert } from "@/components/ui";
import { getGoLiveCheckCopy } from "@/lib/go-live";
import { getGoLiveStatus } from "@/lib/profile-api";

/**
 * Persistent "why am I not getting work" banner
 * (docs/OPEN-FIXES-FEATURES.csv "Provider Web, Proposed new page, Onboarding
 * checklist and go-live status"): shown across every authenticated screen
 * (`(provider)/layout.tsx`, the same spot that used to carry a
 * PendingVerification-only message) so a provider is never left staring at
 * an empty jobs list with no explanation. Names every specific prerequisite
 * still missing - KYC, skills, service areas, availability - rather than a
 * generic "you're not live yet," and disappears entirely once
 * {@link GoLiveStatus.isGoLiveReady} is true.
 *
 * Best-effort: a failed or still-loading fetch renders nothing rather than
 * blocking or flashing an error on every single screen of the app - the
 * provider still sees the equivalent information on `/profile`'s
 * `GoLiveChecklistSection` (which does surface its own error state).
 */
export function GoLiveBanner() {
  const query = useQuery({ queryKey: ["provider-go-live-status"], queryFn: getGoLiveStatus });

  if (!query.data || query.data.isGoLiveReady) return null;

  const missing = query.data.checks.filter((check) => !check.isComplete);
  if (missing.length === 0) return null;

  return (
    <div className="mb-4">
      <Alert tone="warning" title="You're not receiving jobs yet">
        <ul className="flex flex-col gap-1">
          {missing.map((check) => {
            const copy = getGoLiveCheckCopy(check);
            return (
              <li key={check.key}>
                {copy.bannerMessage}{" "}
                <Link href={copy.href} className="font-medium underline underline-offset-2">
                  {copy.linkLabel}
                </Link>
              </li>
            );
          })}
        </ul>
      </Alert>
    </div>
  );
}
