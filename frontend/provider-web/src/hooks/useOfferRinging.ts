"use client";

import { useEffect, useMemo, useRef } from "react";
import type { JobListItem } from "@/lib/jobs-types";
import { hasOpenOffer, offersToRingFor } from "@/lib/offer-ringing";

/** Chime + vibration cadence. Matches VIBRATION_PATTERN's own total length below, so each cycle's buzz starts right as the previous one's tail pause ends. */
const RING_INTERVAL_MS = 2500;
const VIBRATION_PATTERN = [400, 200, 400, 1500];

/**
 * Loops a ringtone + vibration for as long as at least one offer in
 * `offers` is still open (status Assigned, deadline not yet passed),
 * stopping the instant that stops being true - the provider accepted or
 * declined it (it drops out of the list), or its response window ran out.
 * It is also silent while the provider has that offer's own job page open
 * (`openJobId`), and starts again the moment they leave it unanswered.
 *
 * Called once, from `(provider)/layout.tsx` - the authenticated app shell,
 * not any one screen - fed by that layout's own actively-polled `GET /jobs`
 * query. Deliberately shell-level rather than scoped to `/offers`: an offer
 * lands the instant it is assigned regardless of which screen the provider
 * is working from (mid-visit on a different job, checking earnings, ...),
 * so ringing that only fired while `/offers` itself happened to be mounted
 * would miss exactly the cases where a provider most needs the nudge.
 *
 * docs/OPEN-FIXES-FEATURES.csv's "Job offers with countdown" row already
 * closed the "no dedicated offers surface" gap; this closes the next one
 * behind it, that a provider not looking at that screen right now could
 * still miss an offer entirely with nothing audible or physical to notice.
 * It is NOT a substitute for push - it only ever reaches a provider with
 * the app open in a focused tab - see OffersPage's own doc comment for why
 * standing up real push infra (today's server-side provider is a no-op
 * logging sandbox in every environment) is a separate, larger change kept
 * out of this one.
 *
 * Synthesized via the Web Audio API rather than an embedded audio file: a
 * two-tone chime built from two oscillators needs no binary asset, no
 * licensing, and no network fetch to be ready the instant an offer appears.
 *
 * Re-checks on its own timer rather than only when `offers` changes: an
 * offer's deadline can pass with nothing re-rendering this component (no
 * new fetch happened), and a countdown that silently stopped ringing only
 * on the next unrelated refetch would keep buzzing well past "expired".
 *
 * Browsers block audio from starting with no prior user gesture on the
 * page (autoplay policy) - by design, and not something to work around. In
 * practice a provider reaches this shell by signing in, which satisfies it
 * for the rest of that page load; a tab left open with no interaction since
 * load may stay silent for the first offer, same as any other in-page audio
 * would.
 *
 * navigator.vibrate() enforces its own, separate gesture requirement -
 * confirmed against a real Chrome instance: "Blocked call to
 * navigator.vibrate because user hasn't tapped on the frame ... yet" fires
 * even once the audio gesture requirement above is satisfied (e.g.
 * immediately after the sign-in submit that unblocks audio for the rest of
 * the session). A tap anywhere on the resulting page - opening a nav tab is
 * enough - clears it; nothing here works around that either.
 *
 * Depends on `offers` itself, not a derived boolean: computing "should ring"
 * with Date.now() at render time is an impure read (React may call a render
 * more than once for one commit - Strict Mode, a discarded/retried render -
 * and the answer must not depend on exactly when that happened to run), so
 * the same hasUnexpiredOffer check instead runs inside the effect below,
 * where an impure/time-based read is the normal, correct place for it. This
 * only avoids restarting on every unrelated re-render because the caller
 * (`(provider)/layout.tsx`) already keeps `offers`' reference stable via its
 * own useMemo, and TanStack Query's structural sharing means an unchanged
 * poll response doesn't produce a new array either - an offer's deadline
 * elapsing with nothing new fetched is still caught by this effect's own
 * setInterval re-check, not by a dependency-array restart.
 */
export function useOfferRinging(offers: readonly JobListItem[], openJobId: string | null = null): void {
  const audioContextRef = useRef<AudioContext | null>(null);

  // The offer whose job page is open is not rung for (see lib/offer-ringing.ts): the provider is already reading it.
  // The moment they leave that page without answering, this list includes it again, the effect below restarts and
  // the ring resumes at once. Memoised so an unrelated re-render does not restart the ring.
  const ringingOffers = useMemo(() => offersToRingFor(offers, openJobId), [offers, openJobId]);

  useEffect(() => {
    return () => {
      audioContextRef.current?.close();
    };
  }, []);

  useEffect(() => {
    if (!hasOpenOffer(ringingOffers, Date.now())) return;

    const AudioContextClass =
      window.AudioContext ?? (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;

    function playChime() {
      if (!AudioContextClass) return;

      // Resume, not (re)create: a context is reused across ring cycles (see
      // the cleanup effect above) and browsers suspend a freshly-created or
      // backgrounded-tab context until something resumes it. A no-op when
      // already running, so safe to call on every cycle.
      const audioContext = audioContextRef.current ?? new AudioContextClass();
      audioContextRef.current = audioContext;
      void audioContext.resume();

      const now = audioContext.currentTime;
      [0, 0.18].forEach((offset, index) => {
        const oscillator = audioContext.createOscillator();
        const gain = audioContext.createGain();
        oscillator.type = "sine";
        oscillator.frequency.value = index === 0 ? 880 : 1108.73; // A5, then C#6 - a bright two-note chime, not one harsh tone
        gain.gain.setValueAtTime(0, now + offset);
        gain.gain.linearRampToValueAtTime(0.25, now + offset + 0.02);
        gain.gain.linearRampToValueAtTime(0, now + offset + 0.16);
        oscillator.connect(gain);
        gain.connect(audioContext.destination);
        oscillator.start(now + offset);
        oscillator.stop(now + offset + 0.16);
      });

      if ("vibrate" in navigator) {
        navigator.vibrate(VIBRATION_PATTERN);
      }
    }

    playChime();
    const intervalId = setInterval(() => {
      // The closed-over `offers`, not a ref: this effect only lives as long
      // as `offers` itself hasn't changed (it's the dependency below), so
      // the closure is already current for the whole interval's lifetime -
      // what this re-check catches is pure time passing (a deadline
      // elapsing), not a stale snapshot.
      if (!hasOpenOffer(ringingOffers, Date.now())) {
        clearInterval(intervalId);
        return;
      }
      playChime();
    }, RING_INTERVAL_MS);

    return () => {
      clearInterval(intervalId);
      if ("vibrate" in navigator) {
        navigator.vibrate(0); // cancels any in-flight pattern
      }
    };
  }, [ringingOffers]);
}
