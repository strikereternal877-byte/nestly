/**
 * Load profiles for the Nestly load harness (task #387).
 *
 * Pure data, no I/O and no environment access, so this file is importable
 * from both Node (the seeding/verification scripts) and k6 (the scenarios).
 * Each side reads its own environment (`process.env` / `__ENV`) and overrides
 * what it needs.
 *
 * On the numbers: SRS 29.1 states performance requirements qualitatively
 * ("optimized for quick browsing", "should be low-latency") and 29.2 asks for
 * "concurrent checkout traffic during promotions" without a target rate.
 * There is therefore no committed number to assert against, and inventing one
 * and calling it a requirement would be worse than useless. So:
 *
 *   - The arrival rates below are chosen to be *reproducible on the machine
 *     recording the baseline*, not to model a forecast production peak. Their
 *     job is to make a regression visible, not to certify capacity.
 *   - The latency thresholds in each scenario are set from the recorded
 *     baseline with headroom (see load/baseline/BASELINE.md), and are labelled
 *     as regression tripwires rather than as SRS requirements.
 *   - The contention scenario's assertions are the exception: those are
 *     correctness invariants, they come from the code's own contract
 *     (SlotWindow.MaxBookingsPerSlot, enforced by SlotCapacityRepository), and
 *     they are absolute at any load.
 */

export const PROFILES = {
  /** Fast sanity pass - proves the harness works end to end. Not a baseline. */
  smoke: {
    catalog: {
      steps: [{ name: "low", rate: 5, durationSec: 15, preAllocatedVUs: 10, maxVUs: 30 }],
    },
    checkout: { rate: 2, durationSec: 15, preAllocatedVUs: 10, maxVUs: 30 },
    contention: { capacity: 3, racers: 12, rounds: 2, roundIntervalSec: 8 },
  },

  /** The recorded baseline profile. */
  baseline: {
    catalog: {
      // Three back-to-back steady steps rather than one rate: a single point
      // tells you the p95 at one load, a curve tells you where the knee is,
      // and a regression that only shows up under the higher step is exactly
      // the kind this harness exists to catch.
      steps: [
        { name: "low", rate: 10, durationSec: 40, preAllocatedVUs: 20, maxVUs: 100 },
        { name: "mid", rate: 30, durationSec: 40, preAllocatedVUs: 50, maxVUs: 200 },
        { name: "high", rate: 60, durationSec: 40, preAllocatedVUs: 100, maxVUs: 400 },
      ],
    },
    // Writes real bookings and real payment orders, so it runs at one modest
    // steady rate rather than a ramp - ~480 bookings per run is already
    // enough rows to notice in a dev database.
    checkout: { rate: 8, durationSec: 60, preAllocatedVUs: 30, maxVUs: 150 },
    // 60 customers racing for 10 seats, five independent times.
    contention: { capacity: 10, racers: 60, rounds: 5, roundIntervalSec: 10 },
  },
};

/** Number of pooled load customers the harness provisions and logs in. */
export const CUSTOMER_POOL_SIZE = 64;

/** Fixture identifiers. Kept distinct from the E2E fixture so the two suites cannot disturb each other. */
export const FIXTURE = {
  stateName: "Load Test State",
  stateCode: "LTS",
  cityName: "Load Test City",
  zoneName: "Load Test Zone",
  pincode: "570001",
  localityName: "Load Test Locality",
  categoryName: "Load Harness Services",
  categorySlug: "load-harness-services",
  serviceName: "Load Harness Standard Clean",
  serviceSlug: "load-harness-standard-clean",
  servicePrice: 999,
  /** Uncapped on purpose - the checkout scenario measures checkout, not capacity contention. */
  checkoutWindowName: "Load Checkout Anytime",
};
