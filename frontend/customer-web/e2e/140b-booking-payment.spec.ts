import { test, expect } from "@playwright/test";
import { loadFixture, authenticateAsSeededCustomer } from "./setup/auth";
import { createBookingViaUi } from "./setup/create-booking-via-ui";

/** Task 140b: slot selection -> booking -> payment (SRS 33 UAT flow 2). */
test.describe("Slot selection to booking to payment", () => {
  test("books the seeded service end to end and reaches the confirmation page", async ({ page }) => {
    const fixture = loadFixture();
    await authenticateAsSeededCustomer(page, fixture);

    const bookingId = await createBookingViaUi(page, fixture);
    expect(bookingId).toMatch(/^[0-9a-f-]{36}$/);

    // The page shows the short human-facing reference ("GLX-YYMMDD-XXXXX"),
    // not the raw GUID captured above - the URL is still keyed by the GUID
    // (asserted via waitForURL below), only the display text changed.
    const referencePattern = /^Booking ID: GLX-\d{6}-[0-9A-Z]{5}$/;
    await expect(page.getByText(referencePattern)).toBeVisible();

    await page.getByRole("link", { name: "View booking details" }).click();
    await page.waitForURL(new RegExp(`/bookings/${bookingId}$`));
    await expect(page.getByRole("heading", { name: fixture.serviceName })).toBeVisible();
    await expect(page.getByText(referencePattern)).toBeVisible();
  });

  test("the new booking appears in the Upcoming bookings list", async ({ page }) => {
    const fixture = loadFixture();
    await authenticateAsSeededCustomer(page, fixture);

    const bookingId = await createBookingViaUi(page, fixture);

    await page.goto("/bookings");
    await expect(page.getByRole("tablist", { name: "Booking status" })).toBeVisible();
    const upcomingTab = page.getByRole("tab", { name: "Upcoming" });
    await expect(upcomingTab).toHaveAttribute("aria-selected", "true");

    await expect(page.getByRole("link").filter({ hasText: fixture.serviceName }).first()).toBeVisible({
      timeout: 15_000,
    });
    await page.goto(`/bookings/${bookingId}`);
    await expect(page.getByText(/^Booking ID: GLX-\d{6}-[0-9A-Z]{5}$/)).toBeVisible();
  });
});
