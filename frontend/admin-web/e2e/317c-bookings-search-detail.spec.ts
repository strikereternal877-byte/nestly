import { test, expect } from "@playwright/test";
import { authenticateAsSeededAdmin, loadFixture } from "./setup/auth";

/**
 * Task 317: bookings list/search -> detail (SRS 12.11.1-3, task 116). Filters
 * on the exact seeded booking's reference (an existing booking if this
 * database already has one from prior customer-web E2E runs / manual QA, or
 * a fresh one this suite pays for itself when it doesn't - see
 * e2e/setup/seed-admin.ts) so the search narrows to exactly one row
 * regardless of what else is in the table, then follows it into the detail
 * page. Reference, not id: the "Booking #" field (bookings/page.tsx) matches
 * `BookingReference`, not the GUID - see seed-booking.ts's
 * PaidBookingFixture.reference.
 */
test.describe("Bookings list and detail", () => {
  test("searches by booking reference and opens the matching booking's detail page", async ({ page }) => {
    const fixture = loadFixture();
    await authenticateAsSeededAdmin(page, fixture);

    await page.goto("/bookings");
    await expect(page.getByRole("heading", { name: "Bookings" })).toBeVisible();

    // "Booking #" filters by the human-facing reference (e.g.
    // "GLX-260825-K7F3M"), not the GUID id - see seed-booking.ts's
    // PaidBookingFixture.reference and bookings/page.tsx's "Booking #" field.
    // Live filtering (no Search button on this page either - see
    // bookings/page.tsx): typing alone triggers the debounced query. The
    // only element anywhere in the DOM whose accessible name contains
    // "Search" is the header's GlobalSearch trigger ("Search pages and
    // settings") - a stray click here used to open that palette, whose
    // modal backdrop then blocked the row-link click below for the rest of
    // the test (same bug already fixed in 317d-admin-user-lifecycle.spec.ts).
    await page.getByLabel("Booking #").fill(fixture.sampleBookingReference);

    // Matched by href rather than by customer name: the results table keeps
    // showing the previous (unfiltered) page via react-query's
    // `placeholderData` while the filtered request is in flight, and several
    // seeded bookings share the customer-web E2E suite's fixed test-customer
    // name - a name-based locator is briefly ambiguous (strict-mode
    // violation, which Playwright does not retry through) during that
    // window. The href is unique to this booking from the first render.
    // `:visible` because the responsive DataTable renders every row twice -
    // once for the desktop table and once for the stacked mobile layout - and
    // hides one with CSS, so the bare href matches two elements.
    const customerLink = page.locator(`a[href="/bookings/${fixture.sampleBookingId}"]:visible`);
    await expect(customerLink).toBeVisible({ timeout: 15_000 });
    // The row's link is the "Booking #" cell (bookings/page.tsx), whose text
    // is the reference - the customer name renders in a separate, unlinked
    // cell next to it, not inside this link.
    await expect(customerLink).toHaveText(fixture.sampleBookingReference);
    await customerLink.click();

    await page.waitForURL(new RegExp(`/bookings/${fixture.sampleBookingId}`));
    await expect(page.getByRole("heading", { name: fixture.sampleBookingCustomerName })).toBeVisible();
    // The detail page's subtitle shows the human-facing reference, not the id.
    await expect(page.getByText(`Booking ${fixture.sampleBookingReference}`)).toBeVisible();

    // "Status timeline" lives on the Timeline tab, not the Overview tab this
    // page opens on by default.
    await page.getByRole("tab", { name: "Timeline" }).click();
    await expect(page.getByText("Status timeline")).toBeVisible();
  });
});
