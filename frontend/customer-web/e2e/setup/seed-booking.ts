import type { CatalogFixture } from "./seed-catalog";

const CONSUMER_API = process.env.CONSUMER_API_URL ?? "http://localhost:5257";
const ADMIN_API = process.env.ADMIN_API_URL ?? "http://localhost:5177";

/** `BookingStatus.Confirmed`'s ordinal (backend/shared/Domain/BookingStatus.cs) - neither API registers a JsonStringEnumConverter, so enums cross the wire as numbers. */
const BOOKING_STATUS_CONFIRMED = 3;

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

export interface PaidBookingFixture {
  bookingId: string;
  slotDate: string;
  customerName: string;
  /** Short human-facing code (e.g. "GLX-260825-K7F3M") - what the Bookings list's "Booking #" filter actually searches, never the GUID in bookingId. */
  reference: string;
}

/**
 * Creates one booking as the E2E customer and pays it through the sandbox
 * gateway, landing it `Confirmed` - the minimum real state any E2E suite
 * that just needs *a* real booking to exist can build on, extracted so
 * admin-web's and provider-web's suites do not each carry their own copy
 * of booking-creation-plus-payment (provider-web's suite previously had
 * this inline; admin-web's suite instead assumed a booking already existed
 * from a prior customer-web E2E run - true for a shared, long-lived local
 * dev database, false against ci.yml's `e2e` job, which gives each matrix
 * leg its own fresh, isolated Postgres database).
 *
 * Takes an admin token (not just the customer token already on
 * `CatalogFixture`) because the definitive "did the sandbox payment
 * actually land this Confirmed" check reads the booking back through
 * admin-api, mirroring the one real gateway-decline case that will
 * legitimately produce a non-Confirmed status here (see the thrown error
 * below) rather than trusting the payment-simulate call's own 2xx.
 */
export async function createPaidBooking(
  catalog: CatalogFixture,
  adminToken: string,
  slotDate: string,
): Promise<PaidBookingFixture> {
  const booking = await post(`${CONSUMER_API}/api/v1/bookings`, catalog.customerAccessToken, {
    serviceId: catalog.serviceId,
    cityId: catalog.cityId,
    addressId: catalog.addressId,
    localityId: catalog.localityId,
    slotWindowId: catalog.slotWindowId,
    slotDate,
    quantity: 1,
    addOns: [],
    idempotencyKey: crypto.randomUUID(),
  });

  const order = await post(`${CONSUMER_API}/api/v1/payments/orders`, catalog.customerAccessToken, {
    bookingId: booking.id,
    idempotencyKey: crypto.randomUUID(),
  });
  await post(`${CONSUMER_API}/api/v1/payments/orders/simulate`, catalog.customerAccessToken, {
    gatewayOrderId: order.gatewayOrderId,
  });

  const A = `${ADMIN_API}/api/v1/admin`;
  const confirmed = await get(`${A}/bookings/${booking.id}`, adminToken);
  if (confirmed.status !== BOOKING_STATUS_CONFIRMED) {
    throw new Error(
      `Expected booking ${booking.id} to be Confirmed after the sandbox payment, but it is status ` +
        `${confirmed.status}. The sandbox gateway declines any amount whose paisa component is exactly 13 - ` +
        "check the seeded service price.",
    );
  }

  // AdminBookingDetailResponse nests the customer under `customer.name`
  // (AdminBookingCustomerSnapshot) - unlike AdminBookingListItemResponse,
  // which has a flat `customerName`. There is no flat customerName here.
  return { bookingId: booking.id, slotDate, customerName: confirmed.customer.name, reference: confirmed.reference };
}
