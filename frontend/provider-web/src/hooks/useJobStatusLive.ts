"use client";

import * as signalR from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect } from "react";
import { API_BASE_URL } from "@/lib/api";
import { getAccessToken } from "@/lib/auth";

/**
 * Task 282's use of `@microsoft/signalr` on provider-web (added to this app
 * for the first time here - it was already present in customer-web and
 * admin-web). Joins the booking's group on `/hubs/tracking` (task 273's hub,
 * shared by all three apps) and invalidates the `["provider-job", jobId]`
 * cache on `BookingStatusChanged`, copying customer-web's
 * `useBookingTracking` connection lifecycle (accessTokenFactory,
 * withAutomaticReconnect, join/leave on mount/unmount, re-join on
 * onreconnected) rather than inventing a second client pattern for the same
 * hub.
 *
 * This is what makes {@link useLocationSharing}'s "stop cleanly on
 * complete/cancel" hold even when the transition happens server-side (an
 * admin cancellation) rather than through this provider's own Start/Complete
 * buttons: without it, `job.status` - and therefore whether the location
 * feed is `active` - would only ever change in response to this provider's
 * own mutations, and a feed left running past a cancellation is exactly the
 * kind of leak task 278's authorization/privacy pass exists to catch on the
 * server if a client ever fails to stop on its own.
 */
export function useJobStatusLive(jobId: string) {
  const queryClient = useQueryClient();

  useEffect(() => {
    if (!jobId) return;

    // withCredentials: false - this app authenticates the hub via
    // accessTokenFactory's bearer token (?access_token= on the handshake),
    // never cookies, so the browser must not send credentials on the CORS
    // preflight. Same pattern as ChatPanel's /hubs/chat connection: the
    // shared CORS policy (AddNestlyCors) deliberately omits
    // AllowCredentials for exactly this reason, and the SignalR JS client
    // defaults withCredentials to true, so leaving this unset here (unlike
    // ChatPanel) broke the preflight with "Access-Control-Allow-Credentials
    // ... must be 'true'" and silently killed live job-status sync.
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(`${API_BASE_URL}/hubs/tracking`, {
        accessTokenFactory: () => getAccessToken() ?? "",
        withCredentials: false,
      })
      .withAutomaticReconnect()
      // Explicit floor, not just the client's own default: @microsoft/
      // signalr's Information level logs the fully-resolved connection URL
      // ("WebSocket connected to wss://...?access_token=<JWT>...") to the
      // browser console on every connect/reconnect - the token the
      // handshake needs per SignalR's own negotiate protocol (there is no
      // way to send it via a WebSocket header instead) then ends up
      // readable in devtools/session recordings on top of the transport
      // logs it was already in. Warning and above never include the URL.
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    connection.on("BookingStatusChanged", (payload: { bookingId: string }) => {
      if (payload.bookingId !== jobId) return;
      queryClient.invalidateQueries({ queryKey: ["provider-job", jobId] });
    });

    connection.onreconnected(() => {
      connection.invoke("JoinBooking", jobId).catch(() => {});
    });

    connection
      .start()
      .then(() => connection.invoke("JoinBooking", jobId))
      .catch(() => {
        // Live updates degrade to whatever refetches this screen already
        // triggers on its own actions; nothing here is load-bearing.
      });

    return () => {
      connection.invoke("LeaveBooking", jobId).catch(() => {});
      connection.stop();
    };
  }, [jobId, queryClient]);
}
