/**
 * Builds the data the provider-web E2E specs (task 385) drive the real job
 * lifecycle against: a paid, fulfilment-ready booking in E2E City with the
 * seeded E2E Test Provider assigned to it, plus a real provider session.
 *
 * Everything here goes through the real HTTP APIs (task 143's convention:
 * exercise the APIs rather than inserting rows directly). There is exactly
 * one direct-DB dependency, and it is pre-existing rather than added by this
 * suite: `database/seed/dev-provider-seed.sql` bootstraps the provider row
 * itself, because provider-api has no password login and never exposes the
 * OTP it generates (see that file's header comment for the full reasoning).
 * This script logs that account in through provider-api's dev-only
 * `POST /api/v1/auth/dev/login-as-provider` backdoor - the same one
 * provider-web's own "Test login" button uses (`src/app/api/dev-login/route.ts`,
 * docs/DEVOPS.md "Dev-only provider test login"), which only exists when the
 * API is running in Development and additionally requires the shared
 * `X-Dev-Auth-Key`.
 *
 * The geography/catalog/serviceability/slot chain is NOT re-implemented here:
 * `seedCatalog()` from customer-web's suite already builds exactly that chain
 * through admin-api and is imported verbatim. It is a dependency-free module
 * (global `fetch` only), so importing it across app folders costs nothing and
 * is strictly better than a second, divergent seeding path that could drift
 * from the one the other two suites and the CI perf job already share.
 */
import { seedCatalog } from "../../../customer-web/e2e/setup/seed-catalog";
import type { CatalogFixture } from "../../../customer-web/e2e/setup/seed-catalog";
import { createPaidBooking } from "../../../customer-web/e2e/setup/seed-booking";

const ADMIN_API = process.env.ADMIN_API_URL ?? "http://localhost:5177";
const PROVIDER_API = process.env.PROVIDER_API_URL ?? "http://localhost:5337";

/**
 * The mobile `database/seed/dev-provider-seed.sql` bootstraps, and the key
 * `backend/provider-api/ProviderApi/appsettings.Development.json` carries.
 * Neither is a secret - both are committed, Development-only values, and the
 * endpoint they unlock is not even mapped outside Development - but both stay
 * overridable so a differently-configured environment can point this suite at
 * its own.
 */
const DEV_PROVIDER_MOBILE = process.env.DEV_PROVIDER_MOBILE ?? "+919888888888";
const DEV_AUTH_KEY = process.env.DEV_AUTH_KEY ?? "dev-only-provider-auth-key-local-1234567890";

/** `BookingStatus.AwaitingFulfilment`'s ordinal - neither API registers a JsonStringEnumConverter, so enums cross the wire as numbers (see backend/shared/Domain/BookingStatus.cs). */
const BOOKING_STATUS_AWAITING_FULFILMENT = 4;

/**
 * `ProviderJobStatus` ordinals (src/lib/jobs-types.ts) that still represent a
 * commitment on the provider's calendar. A job in any of these blocks a new
 * assignment for the same provider on an overlapping slot -
 * `ProviderScheduleConflictService` treats the underlying assignment row as
 * "live" while it is Assigned or Accepted, and Accepted is what every one of
 * EnRoute/Arrived/InProgress/Completed maps from. Rejected/Reassigned/
 * Withdrawn are nobody's commitment any more and do not block.
 */
const BLOCKING_JOB_STATUSES = new Set([0, 1, 4, 5, 7, 8]);

/** `BookingProviderAssignmentStatus` ordinals that are still outstanding - Assigned and Accepted (see that enum's own doc comment). */
const LIVE_ASSIGNMENT_STATUSES = new Set([0, 1]);

export interface ProviderFixture {
  providerAccessToken: string;
  providerRefreshToken: string;
  providerAccessTokenExpiresAtUtc: string;
  /** The booking the lifecycle spec walks Accept -> ... -> Completed. */
  bookingId: string;
  /** `YYYY-MM-DD`, chosen to be free of any other live job for this provider. */
  slotDate: string;
  customerName: string;
  addressLine1: string;
  totalPayable: number;
  serviceName: string;
}

interface ProviderSession {
  accessToken: string;
  refreshToken: string;
  accessTokenExpiresAtUtc: string;
}

async function readError(res: Response): Promise<string> {
  return `${res.status} ${await res.text()}`;
}

async function get(url: string, token: string): Promise<any> {
  const res = await fetch(url, { headers: { Authorization: `Bearer ${token}` } });
  if (!res.ok) throw new Error(`GET ${url} failed: ${await readError(res)}`);
  return res.json();
}

async function post(url: string, token: string, body: unknown): Promise<any> {
  const res = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`POST ${url} failed: ${await readError(res)}`);
  return res.status === 204 ? null : res.json();
}

async function adminLogin(): Promise<string> {
  const res = await fetch(`${ADMIN_API}/api/v1/admin/auth/login`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ email: "dev-admin@nestly.local", password: "E2eTest!Passw0rd" }),
  });
  if (!res.ok) throw new Error(`Admin login failed: ${await readError(res)}`);
  return (await res.json()).accessToken;
}

/**
 * Mints a real provider session without an OTP. Fails loudly rather than
 * falling back to anything: a 404 here means provider-api is not running in
 * Development (the route is only mapped there), and a 401 means the
 * `X-Dev-Auth-Key` does not match `DevAuth:Key` - both are setup problems the
 * runner needs told about, not conditions to work around.
 */
export async function loginAsSeededProvider(): Promise<ProviderSession> {
  const res = await fetch(`${PROVIDER_API}/api/v1/auth/dev/login-as-provider`, {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-Dev-Auth-Key": DEV_AUTH_KEY },
    body: JSON.stringify({ mobile: DEV_PROVIDER_MOBILE }),
  });
  if (!res.ok) {
    throw new Error(
      `Provider dev login failed: ${await readError(res)}. ` +
        "Check provider-api is running with ASPNETCORE_ENVIRONMENT=Development, that " +
        "database/seed/dev-provider-seed.sql has been applied, and that DEV_AUTH_KEY matches " +
        "provider-api's DevAuth:Key.",
    );
  }
  return res.json();
}

/** The provider's own view of its calendar - the authoritative answer to "which days are already committed". */
async function listProviderJobs(providerToken: string): Promise<any[]> {
  const response = await get(`${PROVIDER_API}/api/v1/jobs`, providerToken);
  return response.items;
}

function isoDatePlusDays(days: number): string {
  const date = new Date();
  date.setDate(date.getDate() + days);
  return date.toISOString().slice(0, 10);
}

/**
 * The first upcoming day this provider has no live job on.
 *
 * Two days out at the earliest, for the same reason customer-web's
 * `create-booking-via-ui.ts` books index 2 of the date strip: the seeded "E2E
 * Anytime" window opens at 00:00, so tomorrow's slot is only minutes away in
 * real time when the suite runs late in the evening. Capped below the seeded
 * booking policy's 30-day `maxAdvanceDays`.
 *
 * Skipping busy days matters because completed work still occupies the
 * calendar: a Completed job's assignment row stays `Accepted`, which
 * `ProviderScheduleConflictService` counts as live - so without this, the
 * second run of this suite on any given day would fail on a 409
 * ProviderDoubleBooked rather than on anything real.
 */
async function pickFreeSlotDate(providerToken: string): Promise<string> {
  const busy = new Set(
    (await listProviderJobs(providerToken))
      .filter((job) => BLOCKING_JOB_STATUSES.has(job.status))
      .map((job) => job.slotDate as string),
  );

  for (let offset = 2; offset <= 28; offset += 1) {
    const candidate = isoDatePlusDays(offset);
    if (!busy.has(candidate)) return candidate;
  }

  throw new Error(
    "The seeded provider has a live job on every bookable day in the next 28 days - " +
      "no free slot left to seed a fresh E2E job into.",
  );
}

/**
 * Creates a booking, pays for it through the sandbox gateway, moves it into
 * the fulfilment queue and assigns the seeded provider to it - i.e. produces
 * exactly the state a provider actually finds a job in: `Assigned`, awaiting
 * their response.
 *
 * Every step is the real endpoint the corresponding UI calls. The one that
 * looks like a shortcut is not: `Confirmed -> AwaitingFulfilment` normally
 * happens on `BookingFulfilmentPromotionJob`'s recurring sweep, which only
 * promotes bookings inside its lead-time window, so a booking two days out
 * would sit `Confirmed` for as long as this suite is prepared to wait.
 * Admin's own generic status endpoint performs the same transition through
 * the same `BookingLifecycle` table - it is the documented manual path, not
 * a test-only backdoor.
 */
export async function seedAssignedJob(
  catalog: CatalogFixture,
  providerToken: string,
): Promise<{ bookingId: string; slotDate: string }> {
  const adminToken = await adminLogin();
  const providerId = await providerIdFor(providerToken);
  const slotDate = await pickFreeSlotDate(providerToken);

  // Booking creation plus sandbox payment is shared with admin-web's suite -
  // see seed-booking.ts for why. catalog.customerAccessToken is what it logs
  // in with, so this needs no separate customerLogin() call.
  const booking = await createPaidBooking(catalog, adminToken, slotDate);

  const A = `${ADMIN_API}/api/v1/admin`;
  await post(`${A}/bookings/${booking.bookingId}/status`, adminToken, {
    newStatus: BOOKING_STATUS_AWAITING_FULFILMENT,
    reason: "E2E test setup: releasing the booking into the fulfilment queue.",
  });

  // Automatic assignment (Phase 14) fires on the transition above and may
  // already have matched a provider - sometimes this very one, since it is an
  // eligible provider in E2E City. Assign explicitly only when it picked
  // somebody else, in which case this supersedes the outstanding row
  // (PROVIDER.md OPEN DECISIONS #5 - only one live row per booking). What
  // this must NOT do is assign on top of an auto-assignment to the same
  // provider: history is kept rather than overwritten, so that leaves this
  // provider holding two rows for one booking - one Reassigned, one
  // Assigned - and `GET /jobs` lists a row per assignment, showing the same
  // job twice.
  const assignments: any[] = await get(`${A}/bookings/${booking.bookingId}/assignments`, adminToken);
  const live = assignments.find((a) => LIVE_ASSIGNMENT_STATUSES.has(a.status));
  if (live?.providerId !== providerId) {
    await post(`${A}/bookings/${booking.bookingId}/assign-provider`, adminToken, {
      providerId,
      responseDeadline: new Date(Date.now() + 4 * 60 * 60 * 1000).toISOString(),
    });
  }

  return { bookingId: booking.bookingId, slotDate };
}

/**
 * The signed-in provider's own id, read off the profile endpoint rather than
 * hardcoded from `dev-provider-seed.sql`: the seed pins an id today, but the
 * account is looked up by phone number, so an environment that already had
 * that number would answer with a different id.
 */
async function providerIdFor(providerToken: string): Promise<string> {
  const profile = await get(`${PROVIDER_API}/api/v1/profile`, providerToken);
  return profile.id;
}

export async function seedProviderFixture(): Promise<ProviderFixture> {
  const catalog = await seedCatalog();
  const session = await loginAsSeededProvider();
  const { bookingId, slotDate } = await seedAssignedJob(catalog, session.accessToken);

  // Read the job back through provider-api rather than assembling the
  // expected values from the seeding inputs: the specs assert on what the
  // provider's screen renders, and these are the snapshots that screen is
  // rendered from.
  const job = await get(`${PROVIDER_API}/api/v1/jobs/${bookingId}`, session.accessToken);

  return {
    providerAccessToken: session.accessToken,
    providerRefreshToken: session.refreshToken,
    providerAccessTokenExpiresAtUtc: session.accessTokenExpiresAtUtc,
    bookingId,
    slotDate,
    customerName: job.customerNameSnapshot,
    addressLine1: job.addressLine1Snapshot,
    totalPayable: job.totalPayableSnapshot,
    serviceName: catalog.serviceName,
  };
}
