/** The two ways to auto-schedule a service - see {@link BookingDraft.repeatPlanKind}. */
export type RepeatPlanKind = "daily" | "prepaid";

/**
 * Booking draft persistence (task 228).
 *
 * Quantity, add-ons, address, date and slot are plain component state on the
 * booking summary screen with nothing else backing them, so navigating away
 * mid-review used to wipe every selection silently - most commonly via the
 * "+ Add a new address" link, which leaves for /addresses/new and always
 * redirects to /addresses on save rather than back, so even a single detour to
 * add an address cost the customer their quantity/add-on choices.
 *
 * The draft also survives *creating* the booking, and carries the resulting
 * `pendingBookingId` from then until payment succeeds. Clearing it at creation
 * time meant a customer who pressed Back from the payment screen - to re-check
 * the address before paying, the most ordinary hesitation there is - found a
 * blank page, had to re-pick everything, and got a second, separate booking
 * for their trouble while the first sat unpaid holding a slot.
 *
 * Lives in sessionStorage: the draft is scoped to the tab the customer is
 * booking in, and should not outlive it. It is only ever read after mount,
 * never during the initial render, so server-rendered markup and the client's
 * first paint stay identical - sessionStorage doesn't exist on the server
 * anyway, same reasoning as lib/location.ts's isBrowser guard.
 */
export interface BookingDraft {
  quantity: number;
  addOnIds: string[];
  addressId: string | null;
  date: string;
  slotWindowId: string | null;
  /** Set once a booking has been created from this draft but not yet paid for. */
  pendingBookingId?: string | null;
  /**
   * The "repeat this booking" opt-in (task 298), carried for the same reason
   * as everything above: a detour off the summary page must not quietly drop
   * the customer's choice to make this a standing visit.
   *
   * `recurringPlanId` is set once the plan has actually been created, and is
   * what stops a customer who presses Back from the payment screen and taps
   * "Proceed to book" again from ending up with two identical standing plans —
   * the booking itself is protected from that by its idempotency key, but the
   * plan create has no such key server-side.
   */
  repeatEnabled?: boolean;
  /** Mirrors `RecurringBookingRecurrenceFrequency`; stored as its ordinal, as the API takes it. */
  repeatFrequency?: number | null;
  repeatCount?: string | null;
  /** "Until I cancel" - an open-ended plan with no visit count. */
  repeatUntilCancelled?: boolean;
  /**
   * Which kind of plan the customer picked: "daily" (a visit every day, each one paid as it is booked,
   * from the wallet) or "prepaid" (every visit paid for now, in one payment). A draft saved before there
   * were two kinds has neither and reads as "prepaid", which is what it always was.
   */
  repeatPlanKind?: RepeatPlanKind;
  /**
   * A daily plan's "pay each day's visit from my wallet" choice. Null/absent means the customer has not
   * decided, which the page reads as "yes" whenever the wallet has a balance.
   */
  planWalletAuto?: boolean | null;
  recurringPlanId?: string | null;
  /**
   * Null for a service with no variants (Phase 3 catalog redesign). Part of
   * the draft's content, not its key - switching variants on the same
   * service page still resumes the same draft rather than forking a second
   * sessionStorage entry.
   */
  serviceVariantId?: string | null;
}

function draftKey(serviceSlug: string): string {
  return `nestly.booking-draft.${serviceSlug}`;
}

export function readDraft(serviceSlug: string): BookingDraft | null {
  if (typeof window === "undefined") return null;
  try {
    const raw = sessionStorage.getItem(draftKey(serviceSlug));
    return raw ? (JSON.parse(raw) as BookingDraft) : null;
  } catch {
    return null;
  }
}

export function writeDraft(serviceSlug: string, draft: BookingDraft): void {
  if (typeof window === "undefined") return;
  try {
    sessionStorage.setItem(draftKey(serviceSlug), JSON.stringify(draft));
  } catch {
    // Storage can be unavailable (private mode, blocked cookies) - the
    // booking still works, it just won't survive a detour off this page.
  }
}

export function clearDraft(serviceSlug: string): void {
  if (typeof window === "undefined") return;
  try {
    sessionStorage.removeItem(draftKey(serviceSlug));
  } catch {
    // See above.
  }
}
