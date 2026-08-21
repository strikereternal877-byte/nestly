/**
 * Scenario 1 - catalog browse (task #387, SRS 29.1 "customer catalog pages
 * must be optimized for quick browsing").
 *
 * One iteration is one anonymous browse session walking the real customer
 * funnel end to end: city categories -> category detail -> that category's
 * services -> service detail -> the slot grid for a date. All five endpoints
 * are anonymous on consumer-api, which is what the real top of the funnel is,
 * so no token is involved.
 *
 * Open model on purpose. `constant-arrival-rate` holds *arrival rate* fixed
 * and lets k6 add VUs as latency grows, which is how real browse traffic
 * behaves; a closed VU model would silently throttle itself the moment the
 * API slowed down and would hide exactly the regression this scenario exists
 * to catch. The run is three back-to-back steady steps rather than one rate,
 * so the baseline records a small load curve instead of a single point - see
 * load/config.js.
 *
 * Cache note: consumer-api caches catalog reads through ICacheService
 * (Redis), so from the second iteration onward this is largely a warm-cache
 * measurement. That is deliberate and is what production steady state looks
 * like, but it means these numbers are NOT a cold-cache figure. See
 * load/baseline/BASELINE.md.
 */
import http from "k6/http";
import { check } from "k6";
import { Trend } from "k6/metrics";

import { PROFILES } from "../config.js";

const fixture = JSON.parse(open("../results/fixture.json"));
const PROFILE = __ENV.LOAD_PROFILE || "baseline";
const CFG = PROFILES[PROFILE].catalog;
const BASE = `${fixture.consumerApi}/api/v1`;

const tCategoriesList = new Trend("catalog_categories_list", true);
const tCategoryDetail = new Trend("catalog_category_detail", true);
const tServicesList = new Trend("catalog_services_list", true);
const tServiceDetail = new Trend("catalog_service_detail", true);
const tSlotGrid = new Trend("catalog_slot_grid", true);
const tSession = new Trend("catalog_browse_session", true);

/** Builds one `constant-arrival-rate` scenario per step, run back to back. */
function buildScenarios() {
  const scenarios = {};
  let startSec = 0;
  for (const step of CFG.steps) {
    scenarios[`step_${step.name}`] = {
      executor: "constant-arrival-rate",
      rate: step.rate,
      timeUnit: "1s",
      duration: `${step.durationSec}s`,
      startTime: `${startSec}s`,
      preAllocatedVUs: step.preAllocatedVUs,
      maxVUs: step.maxVUs,
      // gracefulStop 0: a step must not bleed into the next one, or the
      // per-step submetrics stop meaning what they say.
      gracefulStop: "0s",
      tags: { step: step.name },
      exec: "browse",
    };
    startSec += step.durationSec;
  }
  return scenarios;
}

/** One threshold per step, so each step's p95 lands in the summary as its own submetric. */
function stepThresholds() {
  const t = {};
  for (const step of CFG.steps) {
    // Tripwire, not an SRS number - see load/config.js and BASELINE.md.
    t[`http_req_duration{step:${step.name}}`] = ["p(95)<750"];
  }
  return t;
}

export const options = {
  scenarios: buildScenarios(),
  thresholds: {
    // A browse request failing at all is the real regression; latency is
    // secondary. `http_req_failed` counts non-2xx/3xx.
    http_req_failed: ["rate<0.01"],
    checks: ["rate>0.99"],
    ...stepThresholds(),
  },
  summaryTrendStats: ["min", "avg", "med", "p(90)", "p(95)", "p(99)", "max"],
  discardResponseBodies: false,
  noConnectionReuse: false,
};

function timed(trend, res) {
  trend.add(res.timings.duration);
  return res;
}

export function browse() {
  const started = Date.now();
  // Spread across the same date band checkout uses, so the slot grid is not
  // answering the identical query for every VU in the run.
  const date = fixture.checkoutDates[Math.floor(Math.random() * fixture.checkoutDates.length)];

  const categories = timed(
    tCategoriesList,
    http.get(`${BASE}/categories?cityId=${fixture.cityId}`, { tags: { endpoint: "categories_list" } })
  );
  check(categories, {
    "categories 200": (r) => r.status === 200,
    "categories non-empty": (r) => Array.isArray(r.json()) && r.json().length > 0,
  });

  const categoryDetail = timed(
    tCategoryDetail,
    http.get(`${BASE}/categories/${fixture.categorySlug}`, { tags: { endpoint: "category_detail" } })
  );
  check(categoryDetail, { "category detail 200": (r) => r.status === 200 });

  const services = timed(
    tServicesList,
    http.get(`${BASE}/services?categoryId=${fixture.categoryId}`, { tags: { endpoint: "services_list" } })
  );
  check(services, {
    "services 200": (r) => r.status === 200,
    "services non-empty": (r) => Array.isArray(r.json()) && r.json().length > 0,
  });

  const serviceDetail = timed(
    tServiceDetail,
    http.get(`${BASE}/services/${fixture.serviceSlug}`, { tags: { endpoint: "service_detail" } })
  );
  check(serviceDetail, { "service detail 200": (r) => r.status === 200 });

  const slots = timed(
    tSlotGrid,
    http.get(
      `${BASE}/slots?serviceId=${fixture.serviceId}&localityId=${fixture.localityId}&date=${date}`,
      { tags: { endpoint: "slot_grid" } }
    )
  );
  check(slots, {
    "slots 200": (r) => r.status === 200,
    "slots offered": (r) => Array.isArray(r.json()) && r.json().length > 0,
  });

  tSession.add(Date.now() - started);
}

// No handleSummary(): k6's own console summary is kept as-is, and run.sh
// passes --summary-export so the machine-readable baseline artifact comes out
// of k6 itself rather than a hand-rolled reimplementation of its statistics.
