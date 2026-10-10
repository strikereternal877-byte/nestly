/**
 * Builds the admin-user test data the E2E specs (task 317) drive through the
 * real admin-web UI against. Runs entirely through admin-api HTTP calls
 * (same convention as customer-web/e2e/setup/seed-catalog.ts, task 143:
 * exercise the real APIs rather than inserting rows directly) - the only
 * direct-DB seed is `database/seed/dev-admin-seed.sql`, which bootstraps the
 * one admin account this script logs in with (AdminUser is "provisioned by
 * a Super Admin rather than self-registered" - there is no admin
 * self-registration endpoint for a first admin to use).
 *
 * Idempotent by construction: the seeded admin user is looked up by email
 * before being created, and is (re)activated on every run so the write-flow
 * spec (004-admin-user-lifecycle.spec.ts) always starts from a known
 * "Active" state regardless of how the previous run ended.
 */
import { seedCatalog } from "../../../customer-web/e2e/setup/seed-catalog";
import { createPaidBooking } from "../../../customer-web/e2e/setup/seed-booking";

const ADMIN_API = process.env.ADMIN_API_URL ?? "http://localhost:5177";

export interface AdminFixture {
  adminAccessToken: string;
  adminRefreshToken: string;
  adminAccessTokenExpiresAtUtc: string;
  seededAdminUserId: string;
  seededAdminUserEmail: string;
  seededAdminUserFullName: string;
  sampleBookingId: string;
  sampleBookingCustomerName: string;
  /** What the Bookings list's "Booking #" filter actually searches - see seed-booking.ts's PaidBookingFixture. */
  sampleBookingReference: string;
}

interface AdminLoginResponse {
  accessToken: string;
  refreshToken: string;
  accessTokenExpiresAtUtc: string;
}

async function adminLogin(): Promise<AdminLoginResponse> {
  const res = await fetch(`${ADMIN_API}/api/v1/admin/auth/login`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ email: "dev-admin@nestly.local", password: "E2eTest!Passw0rd" }),
  });
  if (!res.ok) throw new Error(`Admin login failed: ${res.status} ${await res.text()}`);
  return res.json();
}

async function get(url: string, token: string): Promise<any> {
  const res = await fetch(url, { headers: { Authorization: `Bearer ${token}` } });
  if (!res.ok) throw new Error(`GET ${url} failed: ${res.status} ${await res.text()}`);
  return res.json();
}

async function post(url: string, token: string, body: unknown): Promise<any> {
  const res = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`POST ${url} failed: ${res.status} ${await res.text()}`);
  return res.status === 204 ? null : res.json();
}

const SEEDED_EMAIL = "e2e-admin-user@nestly.local";

export async function seedAdmin(): Promise<AdminFixture> {
  const session = await adminLogin();
  const A = `${ADMIN_API}/api/v1/admin`;

  const existing = (await get(`${A}/admin-users?email=${encodeURIComponent(SEEDED_EMAIL)}`, session.accessToken))
    .items.find((u: any) => u.email === SEEDED_EMAIL);

  const seededAdminUser =
    existing ??
    (await post(`${A}/admin-users`, session.accessToken, {
      email: SEEDED_EMAIL,
      fullName: "E2E Admin User",
      password: "E2eAdminUser!Passw0rd",
      roleId: null,
    }));

  // Always leave it Active - the lifecycle spec deactivates and reactivates
  // it, and a prior run that failed mid-test could otherwise leave it
  // Inactive for this run. Status is AdminUserStatus.Active = 0 on the wire
  // (no JsonStringEnumConverter on this type - see admin-users-types.ts).
  if (seededAdminUser.status !== 0) {
    await post(`${A}/admin-users/${seededAdminUser.id}/activate`, session.accessToken, null);
  }

  // A real booking to exercise the list-search -> detail flow against. A
  // long-lived local dev database usually already has one from manual QA or
  // a prior customer-web E2E run, so this reuses whatever is newest rather
  // than creating a fresh one every time; ci.yml's `e2e` job gives this
  // suite's own leg a fresh, isolated database with nothing in it yet, so
  // the fallback below is what actually runs there.
  const bookings = await get(`${A}/bookings?page=1&pageSize=1`, session.accessToken);
  let sampleBooking = bookings.items[0];
  if (!sampleBooking) {
    const catalog = await seedCatalog();
    const twoDaysOut = new Date();
    twoDaysOut.setDate(twoDaysOut.getDate() + 2);
    const seeded = await createPaidBooking(catalog, session.accessToken, twoDaysOut.toISOString().slice(0, 10));
    sampleBooking = { id: seeded.bookingId, customerName: seeded.customerName, reference: seeded.reference };
  }

  return {
    adminAccessToken: session.accessToken,
    adminRefreshToken: session.refreshToken,
    adminAccessTokenExpiresAtUtc: session.accessTokenExpiresAtUtc,
    seededAdminUserId: seededAdminUser.id,
    seededAdminUserEmail: SEEDED_EMAIL,
    seededAdminUserFullName: "E2E Admin User",
    sampleBookingId: sampleBooking.id,
    sampleBookingCustomerName: sampleBooking.customerName,
    sampleBookingReference: sampleBooking.reference,
  };
}
