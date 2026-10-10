"use client";

import { useEffect, useRef, useState } from "react";
import { recordProviderLocation } from "@/lib/jobs-api";
import { JobStatus } from "@/lib/jobs-types";

/** Mirrors the trackable subset a booking accepts a location fix in - ProviderLocationIngestService.RecordAsync's own BookingLifecycle.IsTrackable + Accepted-assignment guard on the backend. */
const SHAREABLE_STATUSES: ReadonlySet<JobStatus> = new Set([
  JobStatus.Accepted,
  JobStatus.EnRoute,
  JobStatus.Arrived,
  JobStatus.InProgress,
]);

export function isLocationShareable(status: JobStatus): boolean {
  return SHAREABLE_STATUSES.has(status);
}

/** Sent no more often than this even while moving - stays comfortably above the server's own ~15s minimum interval so a client-throttled fix is never the one the server drops. */
const MIN_SEND_INTERVAL_MS = 20_000;
/** While stationary (see MOVEMENT_THRESHOLD_METRES), a fix is only sent this rarely - "coalesce while stationary" from the task, so a parked vehicle doesn't hold the ingest throttle open for no reason. */
const STATIONARY_SEND_INTERVAL_MS = 60_000;
/** Below this displacement since the last SENT fix, the provider is considered stationary. */
const MOVEMENT_THRESHOLD_METRES = 15;
/** Backoff after a failed POST - doubles per consecutive failure, capped, so a flaky network doesn't turn into a fix-per-second retry storm draining the battery it's supposed to be sane about. */
const BACKOFF_BASE_MS = 5_000;
const BACKOFF_MAX_MS = 120_000;

export type LocationSharingStatus = "idle" | "requesting" | "sharing" | "denied" | "unsupported" | "error";

/**
 * The provider side of the real location feed (task 282): while `active`
 * (the caller passes `isLocationShareable(job.status)`), runs
 * `navigator.geolocation.watchPosition` and POSTs fixes to task 269's ingest
 * endpoint at a battery-sane cadence - client-side throttled independently of
 * the server's own throttle (see MIN_SEND_INTERVAL_MS), coalesced while
 * stationary, and backed off on error.
 *
 * Foreground-only, deliberately: `watchPosition` stops firing the moment the
 * tab is backgrounded or the phone locks on every mobile browser, and this
 * hook does nothing to fight that (no wake lock, no service worker). The
 * caller's UI copy is expected to say so - this hook only reports whether
 * sharing is currently active, not that it stays active in the background.
 *
 * `active` is the caller's job status, kept live by {@link useJobStatusLive}
 * (a `BookingStatusChanged` subscription that invalidates the job query) -
 * that is what makes "stop cleanly on complete/cancel" work even when the
 * transition happens server-side (an admin cancellation) rather than through
 * this provider's own Start/Complete buttons.
 */
export function useLocationSharing(jobId: string, active: boolean): { status: LocationSharingStatus } {
  const [status, setStatus] = useState<LocationSharingStatus>("idle");
  const lastSent = useRef<{ atMs: number; latitude: number; longitude: number } | null>(null);
  const consecutiveFailures = useRef(0);
  const backoffUntilMs = useRef(0);

  useEffect(() => {
    if (!active) {
      // Part of the same subscribe-or-not decision as the watchPosition
      // setup below, not a standalone prop-change adjustment - reporting
      // "idle" here is this effect synchronizing status with whether the
      // subscription it owns is running.
      // eslint-disable-next-line react-hooks/set-state-in-effect
      setStatus("idle");
      return;
    }

    if (!("geolocation" in navigator)) {
      setStatus("unsupported");
      return;
    }

    setStatus("requesting");
    lastSent.current = null;
    consecutiveFailures.current = 0;
    backoffUntilMs.current = 0;

    let cancelled = false;

    const handlePosition = (position: GeolocationPosition) => {
      if (cancelled) return;
      setStatus("sharing");

      const now = Date.now();
      if (now < backoffUntilMs.current) return;

      const { latitude, longitude, accuracy } = position.coords;
      const previous = lastSent.current;
      const elapsedMs = previous ? now - previous.atMs : Infinity;
      const movedMetres = previous ? haversineMetres(previous.latitude, previous.longitude, latitude, longitude) : Infinity;
      const stationary = movedMetres < MOVEMENT_THRESHOLD_METRES;
      const requiredIntervalMs = stationary ? STATIONARY_SEND_INTERVAL_MS : MIN_SEND_INTERVAL_MS;

      if (elapsedMs < requiredIntervalMs) return;

      recordProviderLocation(jobId, {
        latitude,
        longitude,
        accuracyMetres: accuracy ?? null,
        recordedAtUtc: new Date(position.timestamp).toISOString(),
      })
        .then(() => {
          if (cancelled) return;
          consecutiveFailures.current = 0;
          lastSent.current = { atMs: now, latitude, longitude };
        })
        .catch(() => {
          if (cancelled) return;
          // A throttled-but-accepted fix (202) resolves here too, same as
          // 200 - only a real failure (network, 403/409 from a status the
          // server sees as no longer trackable) lands in this branch, so
          // backing off is the right response either way: retry sooner on a
          // blip, much later on a job the server has already moved past.
          consecutiveFailures.current += 1;
          const delay = Math.min(BACKOFF_BASE_MS * 2 ** (consecutiveFailures.current - 1), BACKOFF_MAX_MS);
          backoffUntilMs.current = now + delay;
        });
    };

    const handleError = (error: GeolocationPositionError) => {
      if (cancelled) return;
      setStatus(error.code === error.PERMISSION_DENIED ? "denied" : "error");
    };

    const watchId = navigator.geolocation.watchPosition(handlePosition, handleError, {
      enableHighAccuracy: true,
      maximumAge: 10_000,
      timeout: 20_000,
    });

    return () => {
      cancelled = true;
      navigator.geolocation.clearWatch(watchId);
    };
  }, [jobId, active]);

  return { status };
}

/** Great-circle distance in metres - same formula as the backend's GeoDistance helper (task 265), duplicated here only because that's a C# assembly this client cannot import. */
function haversineMetres(lat1: number, lon1: number, lat2: number, lon2: number): number {
  const R = 6_371_000;
  const toRad = (deg: number) => (deg * Math.PI) / 180;
  const dLat = toRad(lat2 - lat1);
  const dLon = toRad(lon2 - lon1);
  const a =
    Math.sin(dLat / 2) ** 2 + Math.cos(toRad(lat1)) * Math.cos(toRad(lat2)) * Math.sin(dLon / 2) ** 2;
  return 2 * R * Math.asin(Math.sqrt(a));
}
