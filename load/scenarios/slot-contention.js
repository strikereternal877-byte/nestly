/**
 * Scenario 3 - concurrent slot booking under contention (task #387, SRS
 * 29.2 "concurrent checkout traffic during promotions", SRS 29.3 "booking and
 * payment flows must be transactionally safe / duplicate booking risk must be
 * controlled").
 *
 * THIS IS A CORRECTNESS TEST WEARING A PERFORMANCE TEST'S CLOTHES. Latency is
 * incidental here. The question is: when R distinct customers fire
 * POST /bookings at the same capacity-limited slot at the same instant, does
 * the platform sell exactly C seats - no more, and no fewer?
 *
 *   - More than C is an overbooked slot: a real customer with a real
 *     confirmation for a slot nobody can service. Customer-facing failure.
 *   - Fewer than C is a lost seat: the counter was consumed by a request that
 *     did not produce a booking. Revenue silently thrown away, and invisible
 *     without looking at the database.
 *
 * Response codes alone cannot answer either question, so this file is only
 * half the scenario. It asserts the HTTP-visible half (exactly C 201s, exactly
 * R-C clean 409 `Booking.SlotCapacityReached` rejections, zero 5xx) and
 * `load/lib/verify-contention.mjs` asserts the database half afterwards
 * (booking rows, slot_booking_counter, and the two agreeing with each other).
 * Both must pass.
 *
 * SHAPE OF THE RACE
 *
 * `per-vu-iterations` with `vus: R, iterations: rounds` gives R VUs that each
 * run once per round. That alone is not a race - k6 would let them drift apart
 * by tens of milliseconds, which at this scale is enough for the reservation
 * to be effectively serialised and for the scenario to prove nothing. So every
 * VU busy-waits on a wall-clock barrier computed once in `setup()` and shared
 * with all of them, and only then fires. The request body is built before the
 * barrier so the only work after it is the HTTP call itself.
 *
 * Each round targets its own date. slot_booking_counter is keyed by
 * (slot_window_id, slot_date) and never resets, so reusing one date would mean
 * round 2 racing against a counter round 1 had already filled - the race would
 * be over before it started. Distinct dates give `rounds` genuinely
 * independent races in one run, which matters because a concurrency bug that
 * reproduces one time in five is still a concurrency bug.
 *
 * Each racer is a distinct customer, and each sends a fresh idempotency key,
 * exactly like the real customer-web checkout does
 * (frontend/customer-web/src/app/booking/summary/page.tsx). A promotion
 * stampede is many customers arriving at once, not one customer retrying -
 * the latter would collapse into Booking's idempotency dedup path and would
 * be testing something else entirely.
 */
import http from "k6/http";
import { check, sleep } from "k6";
import exec from "k6/execution";
import { Counter, Trend } from "k6/metrics";

const fixture = JSON.parse(open("../results/fixture.json"));
const C = fixture.contention;
const BASE = `${fixture.consumerApi}/api/v1`;

/** Seconds after setup() before round 0 fires - enough for every VU to be initialised and idle at the barrier. */
const BARRIER_LEAD_SEC = 5;

const tBooking = new Trend("contention_booking_request", true);
const cCreated = new Counter("contention_created_201");
const cRejectedCapacity = new Counter("contention_rejected_409_capacity");
const cRejectedOther = new Counter("contention_rejected_other");
const cServerErrors = new Counter("contention_server_errors_5xx");

const EXPECTED_WINNERS = C.capacity * C.rounds;
const EXPECTED_LOSERS = (C.racers - C.capacity) * C.rounds;

export const options = {
  scenarios: {
    race: {
      executor: "per-vu-iterations",
      vus: C.racers,
      iterations: C.rounds,
      maxDuration: `${BARRIER_LEAD_SEC + C.rounds * C.roundIntervalSec + 60}s`,
      gracefulStop: "30s",
      exec: "race",
    },
  },
  thresholds: {
    // These are the point of the scenario. They are exact equalities, not
    // rates with tolerance, because there is no acceptable rate of
    // overbooking.
    contention_created_201: [`count==${EXPECTED_WINNERS}`],
    contention_rejected_409_capacity: [`count==${EXPECTED_LOSERS}`],
    contention_rejected_other: ["count==0"],
    contention_server_errors_5xx: ["count==0"],
    checks: ["rate==1"],
  },
  summaryTrendStats: ["min", "avg", "med", "p(90)", "p(95)", "p(99)", "max"],
};

export function setup() {
  console.log(
    `contention: ${C.racers} racers vs capacity ${C.capacity}, ${C.rounds} rounds ` +
      `(expect ${EXPECTED_WINNERS} winners / ${EXPECTED_LOSERS} clean rejections)`
  );
  console.log(`contention window: ${C.slotWindowName} (${C.slotWindowId})`);
  return { raceEpochMs: Date.now() + BARRIER_LEAD_SEC * 1000 };
}

export function race(data) {
  const round = exec.vu.iterationInScenario;
  const customer = fixture.customers[(exec.vu.idInTest - 1) % fixture.customers.length];

  // Built before the barrier so the barrier is immediately followed by the
  // request and nothing else.
  const payload = JSON.stringify({
    serviceId: fixture.serviceId,
    cityId: fixture.cityId,
    addressId: customer.addressId,
    localityId: fixture.localityId,
    slotWindowId: C.slotWindowId,
    slotDate: C.dates[round],
    quantity: 1,
    addOns: [],
    idempotencyKey: `contention-${fixture.runId}-r${round}-vu${exec.vu.idInTest}`,
  });
  const params = {
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${customer.token}` },
    tags: { endpoint: "booking_create", round: String(round) },
  };

  const fireAtMs = data.raceEpochMs + round * C.roundIntervalSec * 1000;
  // Coarse sleep down to ~50ms, then spin. k6's sleep() is not precise enough
  // on its own to line 60 VUs up inside a few milliseconds, and spinning for
  // the whole interval would burn CPU the API needs.
  const coarseMs = fireAtMs - Date.now() - 50;
  if (coarseMs > 0) sleepMs(coarseMs);
  while (Date.now() < fireAtMs) {
    /* barrier spin */
  }

  const res = http.post(`${BASE}/bookings`, payload, params);
  tBooking.add(res.timings.duration);

  const title = res.status === 409 ? safeTitle(res) : null;

  if (res.status === 201) {
    cCreated.add(1);
  } else if (res.status === 409 && title === "Booking.SlotCapacityReached") {
    cRejectedCapacity.add(1);
  } else if (res.status >= 500) {
    cServerErrors.add(1);
    console.error(`round ${round} vu ${exec.vu.idInTest}: ${res.status} ${res.body}`);
  } else {
    cRejectedOther.add(1);
    console.error(`round ${round} vu ${exec.vu.idInTest}: unexpected ${res.status} ${res.body}`);
  }

  check(res, {
    "either created or cleanly rejected for capacity": (r) =>
      r.status === 201 || (r.status === 409 && safeTitle(r) === "Booking.SlotCapacityReached"),
  });
}

function safeTitle(res) {
  try {
    return res.json("title");
  } catch {
    return null;
  }
}

function sleepMs(ms) {
  // k6's sleep() takes seconds.
  // eslint-disable-next-line no-undef
  require_sleep(ms / 1000);
}

// Imported this way so the helper above stays readable; k6's sleep is a named export.
import { sleep as require_sleep } from "k6";
