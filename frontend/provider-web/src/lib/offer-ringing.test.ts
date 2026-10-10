import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { hasOpenOffer, isOfferOpen, offersToRingFor, openJobIdFromPath } from "./offer-ringing.ts";
import type { JobListItem } from "./jobs-types.ts";

/**
 * When a job offer rings: while it is open, except while its own job page is open - and again the moment the
 * provider leaves that page without answering. Run with `npm run test:unit` (Node's built-in runner, no extra
 * dependency).
 */

const NOW = Date.parse("2026-10-03T12:00:00Z");

function offer(bookingId: string, deadline: string | null): JobListItem {
  return { bookingId, responseDeadline: deadline } as unknown as JobListItem;
}

const OPEN = offer("job-open", "2026-10-03T12:30:00Z");
const OTHER = offer("job-other", "2026-10-03T12:10:00Z");
const EXPIRED = offer("job-expired", "2026-10-03T11:59:00Z");

describe("openJobIdFromPath", () => {
  it("reads the booking id from a job detail page", () => {
    assert.equal(openJobIdFromPath("/jobs/3ebd2ad6-7257-4331-8416-61d7a9a7cd8c"), "3ebd2ad6-7257-4331-8416-61d7a9a7cd8c");
    assert.equal(openJobIdFromPath("/jobs/abc/"), "abc");
  });

  it("is null everywhere else", () => {
    assert.equal(openJobIdFromPath("/jobs"), null);
    assert.equal(openJobIdFromPath("/today"), null);
    assert.equal(openJobIdFromPath("/offers"), null);
    assert.equal(openJobIdFromPath("/jobs/abc/review"), null);
    assert.equal(openJobIdFromPath(null), null);
  });
});

describe("offersToRingFor", () => {
  it("rings for every offer when no job page is open", () => {
    assert.deepEqual(offersToRingFor([OPEN, OTHER], null), [OPEN, OTHER]);
  });

  it("is silent for the offer whose own job page is open", () => {
    const ringing = offersToRingFor([OPEN], "job-open");

    assert.deepEqual(ringing, []);
    assert.equal(hasOpenOffer(ringing, NOW), false);
  });

  it("still rings for a different offer while the provider reads one", () => {
    const ringing = offersToRingFor([OPEN, OTHER], "job-open");

    assert.deepEqual(ringing, [OTHER]);
    assert.equal(hasOpenOffer(ringing, NOW), true);
  });

  it("rings again as soon as the provider leaves the page without answering", () => {
    assert.equal(hasOpenOffer(offersToRingFor([OPEN], "job-open"), NOW), false, "reading it: silent");
    assert.equal(hasOpenOffer(offersToRingFor([OPEN], null), NOW), true, "left it unanswered: rings");
  });

  it("an offer that was answered is no longer in the list, so nothing rings", () => {
    assert.equal(hasOpenOffer(offersToRingFor([], null), NOW), false);
  });
});

describe("isOfferOpen / hasOpenOffer", () => {
  it("an offer is open until its deadline passes", () => {
    assert.equal(isOfferOpen(OPEN, NOW), true);
    assert.equal(isOfferOpen(EXPIRED, NOW), false);
    assert.equal(hasOpenOffer([EXPIRED], NOW), false);
  });

  it("an offer with no deadline counts as open (defensive: the API always sets one)", () => {
    assert.equal(isOfferOpen(offer("job-none", null), NOW), true);
  });

  it("the deadline passing while nothing re-renders stops the ring on the next check", () => {
    assert.equal(hasOpenOffer([OTHER], Date.parse("2026-10-03T12:09:59Z")), true);
    assert.equal(hasOpenOffer([OTHER], Date.parse("2026-10-03T12:10:01Z")), false);
  });
});
