import { test, expect } from "@playwright/test";
import { loadFixture, authenticateAsSeededCustomer } from "./setup/auth";
import { createBookingViaUi } from "./setup/create-booking-via-ui";

/**
 * Task 140c: cancellation and reschedule flows (SRS 33 UAT flow 3). Each
 * test books its own fresh booking (rather than sharing one across tests)
 * since cancelling a booking makes it ineligible for reschedule and vice
 * versa - BookingLifecycle only allows one terminal-ish transition per
 * booking from Confirmed.
 */
test.describe("Cancellation and reschedule", () => {
  test("cancels a confirmed booking and shows the refund outcome", async ({ page }) => {
    const fixture = loadFixture();
    await authenticateAsSeededCustomer(page, fixture);

    const bookingId = await createBookingViaUi(page, fixture);

    await page.goto(`/bookings/${bookingId}`);
    await page.getByRole("link", { name: "Cancel booking" }).click();
    await page.waitForURL(new RegExp(`/bookings/${bookingId}/cancel`));

    await expect(page.getByRole("heading", { name: "Cancel booking" })).toBeVisible();
    await expect(page.getByText("Refund amount")).toBeVisible({ timeout: 15_000 });

    // Before deciding, the screen says why cancelling is free, when that stops, and what a late cancel would cost.
    await expect(page.getByText(/Cancelling now is free - free cancellation lasts until/)).toBeVisible();
    await expect(page.getByText(/after that, \d+(\.\d+)?% of the amount you paid is kept as a fee/i)).toBeVisible();

    await page.locator("#cancel-reason").fill("E2E test: no longer needed.");
    await page.getByRole("button", { name: "Confirm cancellation" }).click();

    await expect(page.getByRole("heading", { name: "Booking cancelled" })).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText("Refund amount")).toBeVisible();
    await expect(page.getByText("Within free cancellation window")).toBeVisible();
    // ...and after, it explains the result rather than leaving a bare Yes/No.
    await expect(page.getByText(/You cancelled in time, so no cancellation fee was charged \(free cancellation lasted until/)).toBeVisible();

    await page.getByRole("button", { name: "Back to booking" }).click();
    await page.waitForURL(new RegExp(`/bookings/${bookingId}$`));
  });

  test("reschedules a confirmed booking to a new slot", async ({ page }) => {
    const fixture = loadFixture();
    await authenticateAsSeededCustomer(page, fixture);

    const bookingId = await createBookingViaUi(page, fixture);

    await page.goto(`/bookings/${bookingId}`);
    await page.getByRole("link", { name: "Reschedule booking" }).click();
    await page.waitForURL(new RegExp(`/bookings/${bookingId}/reschedule`));

    await expect(page.getByRole("heading", { name: "Reschedule booking" })).toBeVisible();

    // The rules are stated up front, with real times: when rescheduling stops being free, when it stops being
    // possible, how many are left, and what it does to a later cancellation.
    await expect(page.getByText(/Rescheduling is free until/)).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText(/You can reschedule up to 2 hours before your slot/)).toBeVisible();
    await expect(page.getByText("You have 2 of 2 reschedules left.")).toBeVisible();

    // Move three days out (index 3) rather than two, where the booking
    // already is - a same-day-window slot can go stale mid-run depending
    // on wall-clock time, same reasoning as create-booking-via-ui.ts.
    const dateStrip = page.locator("h3", { hasText: "Date" }).locator("xpath=following-sibling::div[1]//button");
    await expect(dateStrip.nth(3)).toBeVisible({ timeout: 15_000 });
    await dateStrip.nth(3).click();

    const slotButton = page.getByRole("button", { name: /E2E Anytime/ });
    await expect(slotButton).toBeVisible({ timeout: 15_000 });
    await slotButton.click();
    await expect(slotButton).toHaveAttribute("aria-pressed", "true");

    await page.locator("#reschedule-reason").fill("E2E test: prefer a different day.");

    const confirmButton = page.getByRole("button", { name: "Confirm reschedule" });
    await expect(confirmButton).toBeEnabled();
    await confirmButton.click();

    // The customer is told what happened rather than silently bounced back to the booking.
    await expect(page.getByRole("heading", { name: "Booking rescheduled" })).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText(/No fee - you rescheduled in time/)).toBeVisible();
    await expect(page.getByText("1 of 2")).toBeVisible();
    await expect(page.getByText("A professional will be assigned closer to the time")).toBeVisible();

    await page.getByRole("button", { name: "Back to booking" }).click();
    await page.waitForURL(new RegExp(`/bookings/${bookingId}$`), { timeout: 15_000 });
    await expect(page.getByRole("heading", { name: fixture.serviceName })).toBeVisible();
  });
});
