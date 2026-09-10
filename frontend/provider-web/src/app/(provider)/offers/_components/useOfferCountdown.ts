"use client";

import { useEffect, useState } from "react";

/** Below this much time left, the countdown escalates from warning to danger styling. */
const URGENT_THRESHOLD_MS = 5 * 60 * 1000;

export interface OfferCountdown {
  /** "4:32" style remaining time, or "Expired" once the deadline has passed. */
  label: string;
  /** True inside the last five minutes - mirrors the urgency this offer's clock deserves. */
  isUrgent: boolean;
  isExpired: boolean;
}

function computeCountdown(deadline: string | null, now: number): OfferCountdown {
  if (!deadline) return { label: "—", isUrgent: false, isExpired: false };

  const remainingMs = new Date(deadline).getTime() - now;
  const isExpired = remainingMs <= 0;
  if (isExpired) return { label: "Expired", isUrgent: false, isExpired: true };

  const totalSeconds = Math.floor(remainingMs / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return {
    label: `${minutes}:${seconds.toString().padStart(2, "0")}`,
    isUrgent: remainingMs <= URGENT_THRESHOLD_MS,
    isExpired: false,
  };
}

/**
 * Live "mm:ss remaining" countdown to a job offer's `responseDeadline`
 * (docs/OPEN-FIXES-FEATURES.csv, "Provider Web, Proposed new page, Job
 * offers with countdown": "the 15 minute response deadline is visible only
 * after opening the job" - a static timestamp read on a detail page, not a
 * ticking clock). Recomputed once a second off `Date.now()` so it counts
 * down on screen instead of only ever showing a fixed "Respond by ..." time
 * (which is what `/today` and `/jobs/[id]` still show today - see this
 * screen's page-level comment for why not reusing their static rendering).
 *
 * Deliberately local `setInterval` state rather than a query refetch: the
 * deadline itself does not change tick to tick (only whether it has passed
 * does), so there is nothing to re-fetch from the server every second - only
 * the client's read of "how much of it is left" needs to move.
 */
export function useOfferCountdown(deadline: string | null): OfferCountdown {
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    if (!deadline) return;
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, [deadline]);

  return computeCountdown(deadline, now);
}
