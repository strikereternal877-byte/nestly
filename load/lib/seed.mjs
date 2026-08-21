/**
 * Builds the fixture the k6 scenarios drive against (task #387) and writes it
 * to `load/results/fixture.json`.
 *
 * Everything here goes through the real admin-api / consumer-api, in the same
 * findOrCreate style as `frontend/customer-web/e2e/setup/seed-catalog.ts`.
 * That file is not imported: it is TypeScript living inside customer-web's npm
 * workspace, so reusing it would make this harness depend on
 * `npm ci` in frontend/customer-web and on a Next.js toolchain it otherwise
 * has nothing to do with. The one thing that does bypass HTTP is the customer
 * pool - see load/lib/load-customers.sql for why.
 *
 * The fixture uses its own city/category/service rather than the E2E City
 * fixture, so that:
 *   - the slot booking policy this harness needs (cutoff 0, 365 days advance)
 *     does not change the policy the Playwright E2E suite relies on, and
 *   - hundreds of load bookings do not land on the slot windows the E2E suite
 *     asserts availability against.
 *
 * The contention slot window is created *fresh on every run* with a run id in
 * its name. slot_booking_counter is keyed by (window, date) and never reset,
 * so reusing a window would mean round 2 of run 2 starting from a counter run
 * 1 already filled - the race would be over before it began and the scenario
 * would silently stop testing anything. A fresh window guarantees every round
 * starts from an empty counter.
 */
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import {
  ADMIN_API,
  CONSUMER_API,
  adminLogin,
  customerLogin,
  findOrCreate,
  get,
  patch,
  post,
  put,
  tokenExpiryMs,
} from "./api.mjs";
import { runScript } from "./db.mjs";
import { CUSTOMER_POOL_SIZE, FIXTURE, PROFILES } from "../config.js";

const HERE = dirname(fileURLToPath(import.meta.url));
const LOAD_DIR = join(HERE, "..");
const RESULTS_DIR = join(LOAD_DIR, "results");
const FIXTURE_PATH = join(RESULTS_DIR, "fixture.json");

const CUSTOMER_PASSWORD = "E2eCustomer!Passw0rd";
const TOKEN_REUSE_MARGIN_MS = 10 * 60 * 1000;

const log = (msg) => console.log(`[seed] ${msg}`);

function isoDate(daysFromToday) {
  const d = new Date();
  d.setUTCHours(0, 0, 0, 0);
  d.setUTCDate(d.getUTCDate() + daysFromToday);
  return d.toISOString().slice(0, 10);
}

async function seedGeography(adminToken) {
  const A = `${ADMIN_API}/api/v1`;

  const state = await findOrCreate(
    adminToken, `${A}/admin/geography/states`, `${A}/admin/geography/states`,
    { name: FIXTURE.stateName, code: FIXTURE.stateCode }, (s) => s.code === FIXTURE.stateCode
  );

  const city = await findOrCreate(
    adminToken, `${A}/admin/geography/cities`, `${A}/admin/geography/cities`,
    { stateId: state.id, name: FIXTURE.cityName },
    (c) => c.name === FIXTURE.cityName && c.stateId === state.id
  );

  const zone = await findOrCreate(
    adminToken, `${A}/admin/geography/zones`, `${A}/admin/geography/zones`,
    { cityId: city.id, name: FIXTURE.zoneName },
    (z) => z.name === FIXTURE.zoneName && z.cityId === city.id
  );

  const pincode = await findOrCreate(
    adminToken, `${A}/admin/geography/pincodes`, `${A}/admin/geography/pincodes`,
    { cityId: city.id, code: FIXTURE.pincode },
    (p) => p.code === FIXTURE.pincode && p.cityId === city.id
  );

  const locality = await findOrCreate(
    adminToken, `${A}/admin/geography/localities`, `${A}/admin/geography/localities`,
    { zoneId: zone.id, pincodeId: pincode.id, name: FIXTURE.localityName },
    (l) => l.name === FIXTURE.localityName && l.zoneId === zone.id
  );

  return { state, city, zone, pincode, locality };
}

async function seedCatalog(adminToken, city, pincode) {
  const A = `${ADMIN_API}/api/v1`;

  const category = await findOrCreate(
    adminToken, `${A}/admin/catalog/categories`, `${A}/admin/catalog/categories`,
    {
      name: FIXTURE.categoryName, slug: FIXTURE.categorySlug,
      description: "Seeded by the load harness (task #387).",
      iconUrl: null, bannerUrl: null, sortOrder: 0, seoTitle: null, seoMetaDescription: null,
    },
    (c) => c.slug === FIXTURE.categorySlug
  );
  await post(`${A}/admin/catalog/categories/${category.id}/activate`, adminToken, null);

  const service = await findOrCreate(
    adminToken, `${A}/admin/catalog/services?categoryId=${category.id}`, `${A}/admin/catalog/services`,
    {
      categoryId: category.id, name: FIXTURE.serviceName, slug: FIXTURE.serviceSlug,
      description: "Seeded by the load harness (task #387).",
      shortDescription: "Standard clean", price: FIXTURE.servicePrice,
      inclusions: "Standard clean", exclusions: "Nothing",
      cancellationPolicy: "Free cancellation up to 2 hours before the slot.",
      reschedulePolicy: "Free reschedule up to 2 hours before the slot.",
      durationMinutes: 60, sortOrder: 0, seoTitle: null, seoMetaDescription: null,
      pricingType: "Fixed", isTaxApplicable: true, isAddOnAllowed: false, isQuantityAllowed: false,
      isInspectionBased: false, isSlotRequired: true, isAddressRequired: true, isCustomerNoteAllowed: true,
    },
    (s) => s.slug === FIXTURE.serviceSlug
  );
  await post(`${A}/admin/catalog/services/${service.id}/activate`, adminToken, null);

  // Neither mapping table has a unique constraint on the pair we key off, so
  // these must be checked before creating or every run adds a duplicate.
  const categoryCity = await get(`${A}/admin/serviceability-mappings/category-city?categoryId=${category.id}`, adminToken);
  if (!categoryCity.some((m) => m.cityId === city.id)) {
    await post(`${A}/admin/serviceability-mappings/category-city`, adminToken, { categoryId: category.id, cityId: city.id });
  }

  const servicePincode = await get(`${A}/admin/serviceability-mappings/service-pincode?serviceId=${service.id}`, adminToken);
  if (!servicePincode.some((m) => m.pincodeId === pincode.id)) {
    await post(`${A}/admin/serviceability-mappings/service-pincode`, adminToken, { serviceId: service.id, pincodeId: pincode.id });
  }

  return { category, service };
}

async function seedSlots(adminToken, city, runId, contentionCapacity) {
  const A = `${ADMIN_API}/api/v1`;

  // Full-day windows: SlotAvailabilityService filters on start time vs
  // now + cutoff, so a narrow window goes stale depending on what time of day
  // the harness happens to run - which would make the baseline
  // non-reproducible for a reason that has nothing to do with performance.
  const checkoutWindow = await findOrCreate(
    adminToken, `${A}/admin/slots/windows?cityId=${city.id}`, `${A}/admin/slots/windows`,
    {
      cityId: city.id, name: FIXTURE.checkoutWindowName,
      startTime: "00:00:00", endTime: "23:59:00",
      maxBookingsPerSlot: null, daysOfWeek: [0, 1, 2, 3, 4, 5, 6],
    },
    (w) => w.name === FIXTURE.checkoutWindowName
  );
  await post(`${A}/admin/slots/windows/${checkoutWindow.id}/activate`, adminToken, null);
  // Idempotency guard: if a previous run (or a human) capped this window, the
  // checkout scenario would start reporting 409s as if checkout were broken.
  if (checkoutWindow.maxBookingsPerSlot !== null) {
    await patch(`${A}/admin/slots/windows/${checkoutWindow.id}/capacity`, adminToken, { maxBookingsPerSlot: null });
  }

  const contentionWindowName = `Load Contention ${runId}`;
  const contentionWindow = await post(`${A}/admin/slots/windows`, adminToken, {
    cityId: city.id, name: contentionWindowName,
    startTime: "00:00:00", endTime: "23:59:00",
    maxBookingsPerSlot: contentionCapacity, daysOfWeek: [0, 1, 2, 3, 4, 5, 6],
  });
  await post(`${A}/admin/slots/windows/${contentionWindow.id}/activate`, adminToken, null);

  // maxAdvanceDays has to cover the furthest checkout/contention date the
  // scenarios pick, or the booking is rejected for a reason unrelated to load.
  await put(`${A}/admin/slots/booking-policies`, adminToken, {
    cityId: city.id, cutoffMinutes: 0, maxAdvanceDays: 365,
  });

  return { checkoutWindow, contentionWindow, contentionWindowName };
}

/** Logs in the pooled customers and makes sure each has a serviceable address. Reuses cached, unexpired tokens. */
async function seedCustomerPool(size, cityName, stateName, cached) {
  log(`provisioning ${size} load customers`);
  const sql = await readFile(join(HERE, "load-customers.sql"), "utf8");
  await runScript(sql, { count: size });

  const cachedById = new Map((cached ?? []).map((c) => [c.email, c]));
  const pool = [];
  const now = Date.now();

  for (let i = 1; i <= size; i += 1) {
    const email = `load-customer-${String(i).padStart(4, "0")}@nestly.local`;
    const prior = cachedById.get(email);

    let token = prior?.token;
    if (!token || tokenExpiryMs(token) - now < TOKEN_REUSE_MARGIN_MS) {
      // Reuse matters: POST /auth/login/password is IP-partitioned at 100/min
      // (RateLimiting:Login), so re-logging in the whole pool on every run of
      // a three-scenario harness would start 429ing on the second run.
      token = await customerLogin(email, CUSTOMER_PASSWORD);
    }

    let addressId = prior?.addressId;
    if (!addressId) {
      const addresses = await get(`${CONSUMER_API}/api/v1/addresses`, token);
      const existing = addresses.find((a) => a.pincode === FIXTURE.pincode);
      addressId = existing
        ? existing.id
        : (await post(`${CONSUMER_API}/api/v1/addresses`, token, {
            label: "Load Home",
            line1: `${i} Load Street`,
            line2: null,
            landmark: null,
            pincode: FIXTURE.pincode,
            city: cityName,
            state: stateName,
            latitude: 12.2958,
            longitude: 76.6394,
            contactName: `Load Customer ${String(i).padStart(4, "0")}`,
            contactMobile: `+9170${String(i).padStart(8, "0")}`,
            isDefault: true,
          })).id;
    }

    pool.push({ email, token, addressId });
    if (i % 16 === 0) log(`  ${i}/${size} customers ready`);
  }

  return pool;
}

async function readCachedFixture() {
  try {
    return JSON.parse(await readFile(FIXTURE_PATH, "utf8"));
  } catch {
    return null;
  }
}

export async function seed({ profile = "baseline", runId = new Date().toISOString().replace(/[:.]/g, "-") } = {}) {
  const config = PROFILES[profile];
  if (!config) throw new Error(`Unknown profile: ${profile}`);

  const cached = await readCachedFixture();

  log(`admin login -> ${ADMIN_API}`);
  const adminToken = await adminLogin();

  log("geography");
  const { state, city, pincode, locality } = await seedGeography(adminToken);

  log("catalog + serviceability");
  const { category, service } = await seedCatalog(adminToken, city, pincode);

  log("slot windows + booking policy");
  const { checkoutWindow, contentionWindow, contentionWindowName } =
    await seedSlots(adminToken, city, runId, config.contention.capacity);

  log("provider coverage for the load city");
  // No extra quoting: psql is exec'd without a shell, so the value arrives
  // verbatim and `:'city_name'` inside the script does the quoting.
  await runScript(await readFile(join(HERE, "load-providers.sql"), "utf8"), { city_name: city.name });

  const customers = await seedCustomerPool(
    CUSTOMER_POOL_SIZE, city.name, state.name, cached?.customers
  );

  // Checkout spreads across a band of future dates rather than hammering one:
  // an uncapped window has no counter row, but booking still writes rows keyed
  // by slot_date, and one date would make the write pattern less like real
  // traffic than it needs to be.
  const checkoutDates = Array.from({ length: 14 }, (_, i) => isoDate(i + 2));
  // Each contention round gets its own date, so each round is an independent
  // race against a counter that starts at zero.
  const contentionDates = Array.from({ length: config.contention.rounds }, (_, i) => isoDate(i + 30));

  const fixture = {
    runId,
    profile,
    seededAtUtc: new Date().toISOString(),
    consumerApi: CONSUMER_API,
    adminApi: ADMIN_API,
    cityId: city.id,
    cityName: city.name,
    localityId: locality.id,
    pincodeId: pincode.id,
    pincode: FIXTURE.pincode,
    categoryId: category.id,
    categorySlug: FIXTURE.categorySlug,
    serviceId: service.id,
    serviceSlug: FIXTURE.serviceSlug,
    checkoutSlotWindowId: checkoutWindow.id,
    checkoutDates,
    contention: {
      slotWindowId: contentionWindow.id,
      slotWindowName: contentionWindowName,
      capacity: config.contention.capacity,
      racers: config.contention.racers,
      rounds: config.contention.rounds,
      roundIntervalSec: config.contention.roundIntervalSec,
      dates: contentionDates,
    },
    customers,
  };

  await mkdir(RESULTS_DIR, { recursive: true });
  await writeFile(FIXTURE_PATH, `${JSON.stringify(fixture, null, 2)}\n`, "utf8");
  log(`wrote ${FIXTURE_PATH}`);
  return fixture;
}

if (import.meta.url === `file://${process.argv[1]}`) {
  const profile = process.env.LOAD_PROFILE ?? "baseline";
  const runId = process.env.LOAD_RUN_ID;
  await seed({ profile, ...(runId ? { runId } : {}) });
}
