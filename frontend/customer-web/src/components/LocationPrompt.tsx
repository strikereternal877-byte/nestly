"use client";

import { useQuery } from "@tanstack/react-query";
import { useState, useSyncExternalStore } from "react";
import { Button, Modal, useToast } from "@/components/ui";
import { useSelectedCity } from "@/hooks/useSelectedCity";
import { API_V1, apiFetch } from "@/lib/api";
import { isPermissionDenied, locateCustomer } from "@/lib/geolocation";
import { openCityPicker, setDetectedAddressLabel, setSelectedCity, setSelectedLocality } from "@/lib/location";
import type { City, LocalitySearchResult } from "@/lib/types";

const PROMPTED_KEY = "nestly.locationPrompted";
const MOBILE_QUERY = "(max-width: 767px)";

/**
 * One-time-per-session mobile prompt to enable device location on first
 * load of the home page, so it can auto-select the customer's city instead
 * of leaving "Select city" showing until they notice and tap it themselves.
 *
 * Desktop never sees this: a location dialog on a pointer-driven session
 * reads as an ad-tech dark pattern rather than a convenience, and nothing
 * about browsing from a laptop implies "where I am right now" the way
 * opening a phone does. Gated by viewport width via `matchMedia`, matching
 * the breakpoint the rest of the shell already treats as "mobile" (see
 * `BottomTabBar`), not by user-agent sniffing.
 *
 * Shows a soft explainer first rather than calling `getCurrentPosition`
 * cold: the native browser permission dialog gives the customer no context,
 * and a reflexive "Block" there takes a manual browser-settings change to
 * undo - a mistake this app can't recover from with a second ask. Choosing
 * "Allow location" here is what triggers the real permission prompt, so it
 * only ever appears after the customer has already agreed once.
 *
 * "Choose manually" - and the fallback when detection succeeds but resolves
 * to a city Glavyx doesn't serve yet - both hand off to the header's
 * existing `CitySelector` via `openCityPicker()` rather than re-implementing
 * a second city list here.
 */
function subscribeToMobileQuery(onChange: () => void): () => void {
  const query = window.matchMedia(MOBILE_QUERY);
  query.addEventListener("change", onChange);
  return () => query.removeEventListener("change", onChange);
}

function getIsMobile(): boolean {
  return window.matchMedia(MOBILE_QUERY).matches;
}

/** Desktop-safe default for SSR - corrected immediately from the real client snapshot above, same as every other useSyncExternalStore default in this codebase. */
function getIsMobileServerSnapshot(): boolean {
  return false;
}

export function LocationPrompt() {
  const { city } = useSelectedCity();
  const isMobile = useSyncExternalStore(subscribeToMobileQuery, getIsMobile, getIsMobileServerSnapshot);
  const [visible, setVisible] = useState(false);
  const [status, setStatus] = useState<
    "idle" | "awaiting-permission" | "locating" | "no-match" | "unsupported" | "failed" | "denied"
  >("idle");
  const pushToast = useToast();

  // "Adjusting state when a prop changes" (react.dev/learn/you-might-not-
  // need-an-effect), not an effect, for the same reason as OfflineBanner's
  // dismissed-reset: this only needs to fire once, the first render where
  // (mobile, no city yet, not already prompted) all hold - not on every
  // render where they still do - and comparing against a tracked previous
  // value during render is what limits it to that one transition.
  const shouldPrompt =
    isMobile && city === null && typeof window !== "undefined" && !sessionStorage.getItem(PROMPTED_KEY);
  const [hasPrompted, setHasPrompted] = useState(false);
  if (shouldPrompt && !hasPrompted) {
    setHasPrompted(true);
    // Marked as soon as the prompt is shown, not on a choice being made -
    // dismissing (Escape, backdrop click) still counts as "already asked
    // this session" so a refresh can't turn this into a nag.
    sessionStorage.setItem(PROMPTED_KEY, "1");
    setVisible(true);
  }

  const citiesQuery = useQuery({
    queryKey: ["geography", "cities"],
    queryFn: () => apiFetch<City[]>(`${API_V1}/geography/cities`),
    enabled: visible,
  });

  function chooseManually() {
    setVisible(false);
    openCityPicker();
  }

  async function allow() {
    if (!navigator.geolocation) {
      setStatus("unsupported");
      return;
    }

    setStatus("locating");

    let position: GeolocationPosition;
    try {
      // One tap is enough: this waits for the customer to answer the browser's
      // own prompt (see lib/geolocation.ts) instead of racing a timer against it.
      position = await locateCustomer(undefined, {
        onPhase: (phase) => setStatus(phase === "awaiting-permission" ? "awaiting-permission" : "locating"),
      });
    } catch (error) {
      // A real "no", or permission never answered, or both the high-accuracy
      // and coarse fixes timed out - none of them is "you're outside our
      // service area", so each gets its own message and a logged cause
      // instead of collapsing into "no-match" like every failure used to.
      console.error("Location prompt: couldn't get a GPS fix.", error);
      setStatus(isPermissionDenied(error) ? "denied" : "failed");
      return;
    }

    let cities: City[];
    try {
      // citiesQuery.data alone races the fulfilment-window: it fires only
      // once this modal opens (`enabled: visible`), and on a cold consumer-api
      // instance (Render free tier - can take several seconds to wake) it can
      // still be loading by the time geolocation+reverse-geocode resolve. The
      // old `citiesQuery.data ?? []` read that gap as an empty city list and
      // reported a spurious "no-match" even for a customer standing in a
      // served city. Falling back to an explicit refetch closes it without
      // re-fetching when the data already arrived (`??` short-circuits).
      cities = citiesQuery.data ?? (await citiesQuery.refetch()).data ?? [];
    } catch (error) {
      console.error("Location prompt: couldn't load the serviceable-city list.", error);
      setStatus("failed");
      return;
    }

    try {
      const geocoded = await reverseGeocode(position.coords);
      const matchedCity = geocoded ? matchCity(geocoded.address, cities) : null;
      if (!matchedCity) {
        setStatus("no-match");
        return;
      }

      setSelectedCity(matchedCity);
      setVisible(false);

      // The header pill always gets the raw detected address (per product
      // decision: a customer whose real area isn't a seeded serviceable
      // locality should still see where they actually are, not just the
      // city - see `setDetectedAddressLabel`'s own doc comment for why this
      // is deliberately cosmetic-only). Falls back to the bare city name if
      // Nominatim returned no `display_name` to build it from.
      if (geocoded?.displayName) {
        setDetectedAddressLabel(buildDetectedAddressLabel(geocoded.displayName, matchedCity.name));
      }

      // Best-effort only, and deliberately after closing the dialog: the
      // customer's city is already resolved and usable, so a slow or failed
      // area lookup must never leave them staring at a spinner over what
      // already succeeded. The toast fires only once, after this settles
      // either way, and always names the real matched city/area (not the
      // cosmetic detected address above) - it's confirming what will
      // actually drive serviceability, distinct from what the pill shows.
      let detectedLabel = matchedCity.name;
      try {
        const localities = await apiFetch<LocalitySearchResult[]>(
          `${API_V1}/geography/cities/${matchedCity.id}/localities`,
        );
        const matchedLocality = geocoded ? matchLocality(geocoded.address, localities) : null;
        if (matchedLocality) {
          setSelectedLocality({
            id: matchedLocality.id,
            name: matchedLocality.name,
            pincodeId: matchedLocality.pincodeId,
          });
          detectedLabel = `${matchedCity.name} - ${matchedLocality.name}`;
          // setSelectedLocality above clears the cosmetic label as a side
          // effect (see lib/location.ts) so a manual area pick can't leave
          // a stale one behind - restore it now that the auto-detect flow,
          // not a manual pick, is what just called it.
          if (geocoded?.displayName) {
            setDetectedAddressLabel(buildDetectedAddressLabel(geocoded.displayName, matchedCity.name));
          }
        }
      } catch {
        // City alone is still a fully usable selection - see comment above.
      }
      pushToast("success", `Location detected: ${detectedLabel}`);
    } catch (error) {
      console.error("Location prompt: an unexpected error interrupted matching.", error);
      setStatus("failed");
    }
  }

  if (!visible) return null;

  const busy = status === "locating" || status === "awaiting-permission";

  return (
    <Modal open={visible} onClose={() => setVisible(false)} title="Enable your location" size="sm">
      <div className="flex flex-col gap-4">
        <p className="text-sm text-fg-muted">
          {status === "awaiting-permission"
            ? "Tap Allow on your browser's location prompt - we'll carry on as soon as you do."
            : status === "locating"
              ? "Getting your location - this can take a few seconds on a real GPS fix..."
              : status === "no-match"
                ? "We couldn't match that to a city we serve yet - pick one manually instead."
                : status === "denied"
                  ? "Location is blocked for this site - pick a city manually instead. You can allow it later in your browser's site settings."
                  : status === "failed"
                    ? "We couldn't detect your location just now - pick a city manually instead."
                    : status === "unsupported"
                      ? "Your browser doesn't support location access here - pick a city manually instead."
                      : "Allow location access so we can show services available near you."}
        </p>
        <div className="flex flex-col gap-2">
          {status !== "unsupported" && (
            <Button fullWidth loading={busy} disabled={busy} onClick={allow}>
              Allow location
            </Button>
          )}
          <Button fullWidth variant="secondary" disabled={status === "locating"} onClick={chooseManually}>
            Choose city manually
          </Button>
        </div>
      </div>
    </Modal>
  );
}

interface NominatimAddress {
  city?: string;
  town?: string;
  village?: string;
  county?: string;
  state_district?: string;
  neighbourhood?: string;
  suburb?: string;
  quarter?: string;
  city_district?: string;
  postcode?: string;
}

interface NominatimReverseResponse {
  address?: NominatimAddress;
  display_name?: string;
}

interface ReverseGeocodeResult {
  address: NominatimAddress;
  displayName: string | null;
}

/**
 * Reverse-geocodes a GPS fix into an OpenStreetMap address breakdown via
 * Nominatim. Deliberately not Google's Geocoding API: that requires a billed
 * API key (`NEXT_PUBLIC_GOOGLE_MAPS_API_KEY`, which this app also uses -
 * optionally - for the tracking screen's map tiles, see `lib/googleMaps.ts`),
 * so a customer with no key configured could never get a match here
 * regardless of permission being granted. Nominatim needs no key and no
 * billing account for this call volume (one request per customer who opts
 * in, at most once a session). `zoom=18` (street level) is requested rather
 * than a coarser zoom so the same single response carries both the city-
 * level fields `matchCity` needs and the neighbourhood/suburb-level fields
 * `matchLocality` needs - one network round trip serves both matches.
 * Resolves to `null` - never throws - on any network failure, collapsing
 * into the same "fall back to manual" path every other failure mode uses.
 */
async function reverseGeocode(coords: GeolocationCoordinates): Promise<ReverseGeocodeResult | null> {
  try {
    const response = await fetch(
      `https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat=${coords.latitude}&lon=${coords.longitude}&zoom=18&addressdetails=1`,
    );
    if (!response.ok) return null;

    const data = (await response.json()) as NominatimReverseResponse;
    if (!data.address) return null;
    return { address: data.address, displayName: data.display_name ?? null };
  } catch (error) {
    // Still degrades to the manual-pick path, not a thrown error - see this
    // function's own doc comment - but logged rather than silent, so a real
    // Nominatim outage/rate-limit is distinguishable from "nothing matched".
    console.error("Location prompt: reverse-geocoding failed.", error);
    return null;
  }
}

/**
 * Turns Nominatim's full `display_name` (e.g. "Genus Power Infrastructures
 * Ltd, SPL3, Sitapura, Sanganer Tehsil, Jaipur, Rajasthan, 302022, India")
 * into the text shown after "City - " in the header pill: drops the
 * trailing "India" boilerplate and the matched city's own name, since the
 * city is already the prefix the customer sees before the dash - showing it
 * twice ("Jaipur - ..., Jaipur, Rajasthan") would read as a mistake, not
 * detail. Everything else (building/plot, road, area, state, pincode)
 * survives; the header pill's own `truncate` class is what turns a long
 * remainder into the same "text…" clipping every other value there gets.
 */
function buildDetectedAddressLabel(displayName: string, cityName: string): string {
  return displayName
    .split(",")
    .map((segment) => segment.trim())
    .filter((segment) => segment.length > 0 && segment.toLowerCase() !== cityName.toLowerCase() && segment.toLowerCase() !== "india")
    .join(", ");
}

/**
 * Whole-word containment rather than raw substring: Nominatim/seed names are
 * routinely multi-word ("Vaishali Nagar", "Malviya Nagar", "Shastri Nagar"),
 * and a plain `.includes()` lets a single common word shared by many
 * unrelated areas ("Nagar", "Colony", "Road") match any of them. Requiring
 * every word of the shorter name to appear as a whole word in the longer one
 * still matches genuine partial results (OSM returning "Mansarovar" for a
 * seeded "Mansarovar Extension", or vice versa) without matching two areas
 * that merely share one generic word.
 */
function namesMatch(a: string, b: string): boolean {
  const wordsOf = (value: string) => value.split(/[^a-z0-9]+/).filter(Boolean);
  const [shorter, longer] = wordsOf(a).length <= wordsOf(b).length ? [wordsOf(a), wordsOf(b)] : [wordsOf(b), wordsOf(a)];
  return shorter.length > 0 && shorter.every((word) => longer.includes(word));
}

/** Matches a reverse-geocoded address against Glavyx's serviceable cities. */
function matchCity(address: NominatimAddress, cities: City[]): City | null {
  if (cities.length === 0) return null;

  const candidateNames = [address.city, address.town, address.village, address.county, address.state_district]
    .filter((value): value is string => Boolean(value))
    .map((value) => value.toLowerCase());

  return cities.find((city) => candidateNames.some((candidate) => namesMatch(candidate, city.name.toLowerCase()))) ?? null;
}

/**
 * Matches a reverse-geocoded address against the admin-seeded areas within
 * one already-matched city.
 *
 * Tried finest-grained field first: `neighbourhood` is the closest OSM
 * equivalent to a seeded Locality, `suburb` and `quarter` progressively
 * coarser, and `city_district` coarser still - it can span several actual
 * seeded localities, so a match against it alone is the least trustworthy
 * signal and is only consulted once every finer field has come up empty.
 * Whichever field is tried, every locality is checked against it before
 * moving on to the next, coarser field - so a `neighbourhood` match for a
 * *different* candidate locality is still preferred over a `city_district`
 * match, rather than the two being pooled together as equally good.
 *
 * Postcode is a fallback signal only, tried after every name field: OSM's
 * crowd-sourced `postcode` tagging in India is often imprecise or
 * street-level rather than the official India Post PIN (spot-checked
 * against this app's own seed data - the same coordinates that clearly sit
 * inside a seeded "Mansarovar" area came back tagged with a different
 * postcode than that area's seeded PIN code), so it is not trusted to
 * override a name-based result the way it used to.
 *
 * No match (customer is inside a serviceable city but an area Glavyx hasn't
 * onboarded yet) is a normal outcome, not a failure - the caller leaves the
 * city-only selection in place rather than inventing an unserviceable area.
 */
function matchLocality(address: NominatimAddress, localities: LocalitySearchResult[]): LocalitySearchResult | null {
  if (localities.length === 0) return null;

  const fieldsByGranularity = [address.neighbourhood, address.suburb, address.quarter, address.city_district];
  for (const field of fieldsByGranularity) {
    if (!field) continue;
    const candidate = field.toLowerCase();
    const match = localities.find((locality) => namesMatch(candidate, locality.name.toLowerCase()));
    if (match) return match;
  }

  if (address.postcode) {
    return localities.find((locality) => address.postcode === locality.pincodeCode) ?? null;
  }

  return null;
}
