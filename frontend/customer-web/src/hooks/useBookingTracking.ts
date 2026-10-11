"use client";

import * as signalR from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect } from "react";
import { API_BASE_URL } from "@/lib/api";
import { getAccessToken } from "@/lib/auth";
import { BookingStatus } from "@/lib/types";
import type { BookingTrackingResponse } from "@/lib/types";

/**
 * Task 279. Live half of the booking detail page: joins the booking's group
 * on `/hubs/tracking` (task 273's hub, shared by all three apps) and
 * invalidates the `["booking", id]` REST cache on `BookingStatusChanged` -
 * copying ChatWidget's connection lifecycle (accessTokenFactory,
 * withAutomaticReconnect, join/leave on mount/unmount, re-join on
 * onreconnected) rather than inventing a second client pattern.
 *
 * Invalidate, not patch: `BookingStatusChangedBroadcast` carries only the
 * booking id and the two status values (see its doc comment on the wire's PII
 * rule) - not the new status label, timeline entry, provider assignment or
 * any of the other fields the page renders off the same transition. Patching
 * the cache from a payload that thin would mean rendering a stale timeline
 * next to an up-to-date badge; a refetch is the only way to get a consistent
 * booking back.
 *
 * REST is still the source of truth: this hook never blocks rendering and
 * never introduces a "waiting for the socket" state. If the connection never
 * opens, the booking simply stops updating until the caller's own
 * `refetchInterval` polling ticks over instead - see the `useQuery` call on
 * the booking detail page.
 */
export function useBookingTracking(bookingId: string | undefined) {
  const queryClient = useQueryClient();

  useEffect(() => {
    if (!bookingId) return;

    // withCredentials: false - auth rides the query-string token (see
    // accessTokenFactory), not cookies, matching ChatWidget's connection so
    // the negotiate request doesn't need credentialed CORS the API doesn't grant.
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(`${API_BASE_URL}/hubs/tracking`, {
        accessTokenFactory: () => getAccessToken() ?? "",
        withCredentials: false,
      })
      .withAutomaticReconnect()
      .build();

    connection.on("BookingStatusChanged", (payload: { bookingId: string }) => {
      if (payload.bookingId !== bookingId) return;
      queryClient.invalidateQueries({ queryKey: ["booking", bookingId] });
    });

    connection.onreconnected(() => {
      connection.invoke("JoinBooking", bookingId).catch(() => {});
    });

    const started = connection
      .start()
      .then(() => connection.invoke("JoinBooking", bookingId))
      .catch(() => {
        // Live delivery degrades to REST-only polling; nothing here is load-bearing.
      });

    return () => {
      // Stop only once start has settled: stopping mid-negotiation makes
      // SignalR log an error (dev StrictMode remounts every effect, so the
      // Next.js overlay showed one issue per hub on each visit).
      started
        .then(() => connection.invoke("LeaveBooking", bookingId))
        .catch(() => {})
        .finally(() => connection.stop());
    };
  }, [bookingId, queryClient]);
}

/**
 * Task 281. The tracking screen's own hub client - a second connection
 * rather than a shared one, matching how each page (ChatWidget,
 * useBookingTracking above) already owns its own connection's lifecycle
 * rather than a single global socket threaded through the app. Unlike
 * {@link useBookingTracking}, this one patches the `["booking-tracking", id]`
 * cache directly for `ProviderLocationUpdated`/`EtaUpdated` instead of
 * invalidating: both broadcasts are field-for-field a subset of
 * `BookingTrackingResponse`'s `providerLocation`/`eta` shape (see
 * `TrackingBroadcastContracts.cs`'s doc comment - they were deliberately kept
 * narrow enough to patch with), so there is a fix to apply directly rather
 * than a REST round trip to wait on. That is what lets the provider marker
 * glide between fixes instead of only moving once every poll interval.
 *
 * `BookingStatusChanged` still invalidates both the booking and tracking
 * queries - a status transition can flip whether the booking is trackable at
 * all (see `isBookingTrackable`), which is not a field either cache can patch
 * its way to.
 */
export function useLiveBookingTracking(bookingId: string | undefined) {
  const queryClient = useQueryClient();

  useEffect(() => {
    if (!bookingId) return;

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(`${API_BASE_URL}/hubs/tracking`, {
        accessTokenFactory: () => getAccessToken() ?? "",
        withCredentials: false,
      })
      .withAutomaticReconnect()
      .build();

    connection.on("BookingStatusChanged", (payload: { bookingId: string }) => {
      if (payload.bookingId !== bookingId) return;
      queryClient.invalidateQueries({ queryKey: ["booking", bookingId] });
      queryClient.invalidateQueries({ queryKey: ["booking-tracking", bookingId] });
    });

    connection.on(
      "ProviderLocationUpdated",
      (payload: { bookingId: string; latitude: number; longitude: number; recordedAtUtc: string }) => {
        if (payload.bookingId !== bookingId) return;
        queryClient.setQueryData<BookingTrackingResponse>(["booking-tracking", bookingId], (current) =>
          current
            ? {
                ...current,
                providerLocation: {
                  latitude: payload.latitude,
                  longitude: payload.longitude,
                  recordedAtUtc: payload.recordedAtUtc,
                },
              }
            : current,
        );
      },
    );

    connection.on(
      "EtaUpdated",
      (payload: { bookingId: string; etaSeconds: number; etaComputedAtUtc: string }) => {
        if (payload.bookingId !== bookingId) return;
        queryClient.setQueryData<BookingTrackingResponse>(["booking-tracking", bookingId], (current) =>
          current
            ? { ...current, eta: { etaSeconds: payload.etaSeconds, etaComputedAtUtc: payload.etaComputedAtUtc } }
            : current,
        );
      },
    );

    connection.onreconnected(() => {
      connection.invoke("JoinBooking", bookingId).catch(() => {});
    });

    const started = connection
      .start()
      .then(() => connection.invoke("JoinBooking", bookingId))
      .catch(() => {
        // Live delivery degrades to REST-only polling; nothing here is load-bearing.
      });

    return () => {
      // Stop only once start has settled: stopping mid-negotiation makes
      // SignalR log an error (dev StrictMode remounts every effect, so the
      // Next.js overlay showed one issue per hub on each visit).
      started
        .then(() => connection.invoke("LeaveBooking", bookingId))
        .catch(() => {})
        .finally(() => connection.stop());
    };
  }, [bookingId, queryClient]);
}

/** Mirrors Nestly.Domain.BookingLifecycle.TrackableStatuses - see its doc comment for why this exact set. */
const TRACKABLE_STATUSES: ReadonlySet<BookingStatus> = new Set([
  BookingStatus.Assigned,
  BookingStatus.ProviderEnRoute,
  BookingStatus.ProviderArrived,
  BookingStatus.InProgress,
]);

export function isBookingTrackable(status: BookingStatus): boolean {
  return TRACKABLE_STATUSES.has(status);
}
