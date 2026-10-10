"use client";

import { useEffect, useRef, useState } from "react";
import { loadGoogleMaps } from "@/lib/googleMaps";

/**
 * The provider/destination map for admin-web's live ops view (task 284) -
 * a trimmed port of customer-web's TrackingMap (task 280): same lazy loader,
 * same CSS-variable marker styling, same graceful no-key fallback. Simpler
 * on purpose - an ops view snapshot does not need the customer screen's
 * glide animation between fixes, since {@link useAdminBookingTrackingLive}
 * just re-renders this component with the latest position on each ping.
 */
export function TrackingMap({
  providerLocation,
  destination,
  className,
}: {
  providerLocation: { latitude: number; longitude: number } | null;
  destination: { latitude: number; longitude: number };
  className?: string;
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const mapRef = useRef<google.maps.Map | null>(null);
  const providerMarkerRef = useRef<google.maps.marker.AdvancedMarkerElement | null>(null);
  const [status, setStatus] = useState<"loading" | "ready" | "unavailable">("loading");

  useEffect(() => {
    let cancelled = false;

    loadGoogleMaps().then((maps) => {
      if (cancelled) return;
      if (!maps || !containerRef.current) {
        setStatus("unavailable");
        return;
      }

      const map = new maps.Map(containerRef.current, {
        center: { lat: destination.latitude, lng: destination.longitude },
        zoom: 14,
        disableDefaultUI: true,
        zoomControl: true,
        mapId: "nestly-admin-tracking",
      });
      mapRef.current = map;

      new maps.marker.AdvancedMarkerElement({
        map,
        position: { lat: destination.latitude, lng: destination.longitude },
        content: buildPinElement("destination"),
        title: "Customer address",
      });

      setStatus("ready");
    });

    return () => {
      cancelled = true;
      mapRef.current = null;
      providerMarkerRef.current = null;
    };
    // destination is the booking's immutable address snapshot - never changes under a mounted card.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    const map = mapRef.current;
    if (!map || !providerLocation) return;

    const position = { lat: providerLocation.latitude, lng: providerLocation.longitude };

    if (!providerMarkerRef.current) {
      loadGoogleMaps().then((maps) => {
        if (!maps || !mapRef.current) return;
        providerMarkerRef.current = new maps.marker.AdvancedMarkerElement({
          map: mapRef.current,
          position,
          content: buildPinElement("provider"),
          title: "Provider",
        });
      });
      return;
    }

    providerMarkerRef.current.position = position;
    // Deliberately the two primitives, not providerLocation itself: the
    // effect only ever reads .latitude/.longitude off it, and depending on
    // the object would re-run on every parent re-render that passes a new
    // but value-equal providerLocation reference, thrashing the marker.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [providerLocation?.latitude, providerLocation?.longitude]);

  if (status === "unavailable") {
    return (
      <div
        className={
          "flex min-h-[10rem] flex-col items-center justify-center gap-1.5 rounded-xl border border-line bg-surface-2 px-4 py-6 text-center " +
          (className ?? "")
        }
      >
        <p className="text-sm font-medium text-fg">Map unavailable</p>
        <p className="text-xs text-fg-subtle">No Maps API key configured - coordinates are shown below instead.</p>
      </div>
    );
  }

  return (
    <div className={"relative min-h-[10rem] overflow-hidden rounded-xl border border-line " + (className ?? "")}>
      {status === "loading" ? <div className="absolute inset-0 z-10 animate-pulse bg-surface-2" aria-hidden /> : null}
      <div ref={containerRef} className="h-full min-h-[10rem] w-full" />
    </div>
  );
}

function buildPinElement(kind: "provider" | "destination"): HTMLElement {
  const pin = document.createElement("div");
  pin.style.width = kind === "provider" ? "1.15rem" : "0.9rem";
  pin.style.height = pin.style.width;
  pin.style.borderRadius = "9999px";
  pin.style.border = "2px solid rgb(var(--surface))";
  pin.style.boxShadow = "0 1px 4px rgb(var(--fg) / 0.35)";
  pin.style.background = kind === "provider" ? "rgb(var(--brand-600))" : "rgb(var(--fg-muted))";
  return pin;
}
