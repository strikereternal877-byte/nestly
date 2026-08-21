/**
 * Scenario 2 - checkout (task #387, SRS 29.1 "booking checkout and price
 * calculation should be low-latency", SRS 29.2 "concurrent checkout traffic
 * during promotions").
 *
 * One iteration is one authenticated customer completing the money path:
 *
 *   POST /bookings/summary   price calculation + slot/serviceability revalidation
 *   POST /bookings           the write - transactional, reserves slot/coupon capacity
 *   POST /payments/orders    provider-eligibility gate, then a gateway order
 *
 * Each of the three is measured separately, because they fail and slow down
 * for entirely different reasons and a single end-to-end number would hide
 * which one moved.
 *
 * WHAT THIS DOES NOT MEASURE, and it matters for reading the baseline:
 *   - The payment gateway is a fake in every environment, including
 *     Production (see docs/PRODUCTION-READINESS.md #375). The
 *     /payments/orders timing here is therefore Nestly's own work - the
 *     provider-eligibility chain, the transaction insert - plus an
 *     essentially free in-process "gateway" call. Real checkout will be
 *     slower by a real gateway round trip that this number contains none of.
 *   - Nothing here completes a payment. The webhook/simulate path is not
 *     driven, so no booking moves past PaymentPending and no provider
 *     assignment is created.
 *
 * The slot window this scenario books against is uncapped on purpose
 * (`maxBookingsPerSlot: null`, see load/lib/seed.mjs). Capacity contention is
 * scenario 3's subject; mixing it in here would turn a checkout latency
 * regression and a capacity rejection into the same number.
 *
 * Rate limiting: POST /payments/orders is behind the "payment" policy,
 * IP-partitioned. Run the API with RateLimiting__Payment__PermitLimit raised
 * (see load/README.md) or this scenario measures 429s. The threshold below
 * fails the run loudly if that happens rather than quietly reporting fast
 * rejections as fast checkouts.
 */
import http from "k6/http";
import { check } from "k6";
import exec from "k6/execution";
import { Counter, Trend } from "k6/metrics";

import { PROFILES } from "../config.js";

const fixture = JSON.parse(open("../results/fixture.json"));
const PROFILE = __ENV.LOAD_PROFILE || "baseline";
const CFG = PROFILES[PROFILE].checkout;
const BASE = `${fixture.consumerApi}/api/v1`;

const tSummary = new Trend("checkout_summary", true);
const tCreate = new Trend("checkout_create_booking", true);
const tPayment = new Trend("checkout_payment_order", true);
const tTotal = new Trend("checkout_total", true);

const cBookingsCreated = new Counter("checkout_bookings_created");
const cPaymentOrders = new Counter("checkout_payment_orders_created");
const cRateLimited = new Counter("checkout_rate_limited_429");
const cServerErrors = new Counter("checkout_server_errors_5xx");

export const options = {
  scenarios: {
    checkout: {
      executor: "constant-arrival-rate",
      rate: CFG.rate,
      timeUnit: "1s",
      duration: `${CFG.durationSec}s`,
      preAllocatedVUs: CFG.preAllocatedVUs,
      maxVUs: CFG.maxVUs,
      exec: "checkout",
    },
  },
  thresholds: {
    http_req_failed: ["rate<0.01"],
    checks: ["rate>0.99"],
    // A 429 or a 5xx anywhere invalidates the run - see the header comment.
    checkout_rate_limited_429: ["count==0"],
    checkout_server_errors_5xx: ["count==0"],
    // Tripwires set from the recorded baseline with headroom, not SRS numbers.
    checkout_summary: ["p(95)<800"],
    checkout_create_booking: ["p(95)<1200"],
    checkout_payment_order: ["p(95)<1200"],
  },
  summaryTrendStats: ["min", "avg", "med", "p(90)", "p(95)", "p(99)", "max"],
};

function classify(res) {
  if (res.status === 429) cRateLimited.add(1);
  if (res.status >= 500) cServerErrors.add(1);
  return res;
}

export function checkout() {
  // One pooled customer per VU. Distinct customers matter even here: booking
  // carries a per-customer idempotency key, and a single customer replaying
  // checkout would exercise the dedup path rather than the checkout path.
  const customer = fixture.customers[(exec.vu.idInTest - 1) % fixture.customers.length];
  const date = fixture.checkoutDates[exec.scenario.iterationInTest % fixture.checkoutDates.length];
  const auth = {
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${customer.token}` },
  };

  const request = {
    serviceId: fixture.serviceId,
    cityId: fixture.cityId,
    addressId: customer.addressId,
    localityId: fixture.localityId,
    slotWindowId: fixture.checkoutSlotWindowId,
    slotDate: date,
    quantity: 1,
    addOns: [],
  };

  const started = Date.now();

  const summary = classify(
    http.post(`${BASE}/bookings/summary`, JSON.stringify(request), {
      ...auth,
      tags: { endpoint: "booking_summary" },
    })
  );
  tSummary.add(summary.timings.duration);
  if (!check(summary, { "summary 200": (r) => r.status === 200 })) return;

  const idempotencyKey = `load-${fixture.runId}-${exec.vu.idInTest}-${exec.vu.iterationInScenario}`;
  const created = classify(
    http.post(`${BASE}/bookings`, JSON.stringify({ ...request, idempotencyKey }), {
      ...auth,
      tags: { endpoint: "booking_create" },
    })
  );
  tCreate.add(created.timings.duration);
  if (!check(created, { "booking 201": (r) => r.status === 201 })) return;
  cBookingsCreated.add(1);

  const bookingId = created.json("id");
  const order = classify(
    http.post(
      `${BASE}/payments/orders`,
      JSON.stringify({ bookingId, idempotencyKey: `${idempotencyKey}-pay` }),
      { ...auth, tags: { endpoint: "payment_order" } }
    )
  );
  tPayment.add(order.timings.duration);
  if (check(order, { "payment order 201": (r) => r.status === 201 })) {
    cPaymentOrders.add(1);
  }

  tTotal.add(Date.now() - started);
}
