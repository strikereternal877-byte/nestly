import type { JobListItem } from "./jobs-types";

/**
 * Which job offers should make the phone ring right now.
 *
 * Kept free of React and of any runtime import (types only) so the rule can be unit tested with Node's own runner:
 * `useOfferRinging` is the thin shell that plays the sound.
 *
 * The rule: an offer rings while it is open (no deadline, or a deadline still ahead) - except while the provider is
 * looking at that very job. The ring exists to get them to the job; once they are reading it the ring is only noise
 * on top of the Accept / Decline buttons. If they leave the page without answering the offer is open again, so the
 * ring resumes at once: a provider who reads an offer and walks away must not leave it silently ticking down to the
 * point where it moves to someone else.
 */

/** The booking id in `/jobs/{bookingId}` (not `/jobs` itself, and not a deeper path), else null. */
export function openJobIdFromPath(pathname: string | null): string | null {
  return /^\/jobs\/([^/]+)\/?$/.exec(pathname ?? "")?.[1] ?? null;
}

/** No deadline at all is defensive-only (the API always sets one on assignment): treated as "not expired". */
export function isOfferOpen(offer: JobListItem, nowMs: number): boolean {
  return !offer.responseDeadline || new Date(offer.responseDeadline).getTime() > nowMs;
}

/** The offers to ring for: every open offer except the one whose job page is open right now. */
export function offersToRingFor(offers: readonly JobListItem[], openJobId: string | null): JobListItem[] {
  return offers.filter((offer) => offer.bookingId !== openJobId);
}

export function hasOpenOffer(offers: readonly JobListItem[], nowMs: number): boolean {
  return offers.some((offer) => isOfferOpen(offer, nowMs));
}
