import { test, expect } from "@playwright/test";
import { loadFixture, authenticateAsSeededCustomer } from "./setup/auth";
import { createBookingViaUi } from "./setup/create-booking-via-ui";
import { forceBookingCompleted } from "./setup/force-booking-completed";

/**
 * Task 140d: refund and review submission (SRS 33 UAT flow 4).
 *
 * There is no customer-initiated refund endpoint (refunds are a read-only
 * consequence of cancellation or an admin action, per docs/SRS.md #34's
 * refund-visibility decision) - the refund half of this flow is exercised
 * through cancellation, then verified as visible on the booking detail
 * page, rather than through a dedicated refund submission form that
 * doesn't exist. Review requires a Completed booking, which normally only
 * happens via provider job completion (out of scope here - see
 * force-booking-completed.ts's doc comment).
 */
test.describe("Refund and review submission", () => {
  test("cancelling a booking produces a visible refund on the booking detail page", async ({ page }) => {
    const fixture = loadFixture();
    await authenticateAsSeededCustomer(page, fixture);

    const bookingId = await createBookingViaUi(page, fixture);

    await page.goto(`/bookings/${bookingId}/cancel`);
    await page.locator("#cancel-reason").fill("E2E test: verifying refund visibility.");
    await page.getByRole("button", { name: "Confirm cancellation" }).click();
    await expect(page.getByRole("heading", { name: "Booking cancelled" })).toBeVisible({ timeout: 15_000 });

    await page.goto(`/bookings/${bookingId}`);
    await expect(page.getByText(/Refund/i).first()).toBeVisible({ timeout: 15_000 });
  });

  test("submits a review for a completed booking", async ({ page }) => {
    const fixture = loadFixture();
    await authenticateAsSeededCustomer(page, fixture);

    const bookingId = await createBookingViaUi(page, fixture);
    forceBookingCompleted(bookingId);

    await page.goto(`/bookings/${bookingId}/review`);
    await expect(page.getByRole("heading", { name: "Leave a review" })).toBeVisible({ timeout: 15_000 });

    await page.getByRole("radio", { name: "5 stars" }).click();
    await page.locator("#review-text").fill("E2E test: excellent service, right on time.");
    await page.locator("#review-tags").fill("");
    await page.getByRole("button", { name: "Submit review" }).click();

    await expect(page.getByRole("heading", { name: "Your review" })).toBeVisible({ timeout: 15_000 });
    await expect(page.getByLabel("5 out of 5 stars")).toBeVisible();
    await expect(page.getByText("E2E test: excellent service, right on time.")).toBeVisible();

    // Re-visiting must show the read-only submitted view, not the form again
    // (Review.BookingId's unique index / ReviewService's eligibility check).
    await page.reload();
    await expect(page.getByRole("heading", { name: "Your review" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Submit review" })).toHaveCount(0);
  });
});
