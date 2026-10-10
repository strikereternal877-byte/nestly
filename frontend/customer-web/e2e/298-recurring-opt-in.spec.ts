import { test, expect } from "@playwright/test";
import type { Page } from "@playwright/test";
import { loadFixture, authenticateAsSeededCustomer } from "./setup/auth";
import { BOOKED_DATE_OFFSET_DAYS, createBookingViaUi } from "./setup/create-booking-via-ui";

/**
 * Task 298: "Auto-schedule this service" on the booking flow - a Daily plan
 * (each day's visit paid as it is booked) or a Prepaid plan (every visit paid
 * in the one checkout) - and the recurring-bookings screen it feeds.
 *
 * The assertion that earns the first test is the *date*: the plan must start
 * one full interval after the booking being placed, never on the booked date
 * itself. Starting it on the booked date is the obvious implementation and is
 * wrong - the plan's first occurrence is `NextOccurrenceOnOrAfter(startDate)`
 * server-side, so the scheduler would book a second, duplicate visit for the
 * very day the customer is already paying for, and nothing else in this suite
 * would notice.
 */

/** A daily plan repeats the day after the booking it is bought with. */
const DAILY_INTERVAL_DAYS = 1;

/** How far ahead the skip test asks to resume - inside the 30-day limit, past the plan's next visit. */
const SKIP_UNTIL_OFFSET_DAYS = 10;

/**
 * Formats a date the way `formatCalendarDate` does, but *in the browser*, so
 * the expectation resolves against Chromium's locale rather than Node's. The
 * two are not guaranteed to agree, and a mismatch would fail this test for a
 * reason that has nothing to do with the behaviour under test.
 */
async function formatInPage(page: Page, offsetDays: number): Promise<string> {
  return page.evaluate((days) => {
    const date = new Date();
    date.setHours(0, 0, 0, 0);
    date.setDate(date.getDate() + days);
    return date.toLocaleDateString(undefined, {
      weekday: "short",
      day: "numeric",
      month: "short",
      year: "numeric",
    });
  }, offsetDays);
}

/** `YYYY-MM-DD` for `offsetDays` from today, local - what a date input takes. */
async function isoInPage(page: Page, offsetDays: number): Promise<string> {
  return page.evaluate((days) => {
    const date = new Date();
    date.setDate(date.getDate() + days);
    const pad = (n: number) => String(n).padStart(2, "0");
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
  }, offsetDays);
}

/**
 * Plans come back most-recently-created first, so the plan a test just made is
 * the first card even on a re-run against a dirty database.
 *
 * Filtered on the card's own heading rather than taken as the page's first
 * listitem: the page's BannerBreadcrumb is a list too and its items come
 * first in the DOM, so a bare .first() picks "Home" out of the breadcrumb.
 * Each plan card carries an h2 with the service name; neither the breadcrumb
 * items nor the per-card nested detail list does.
 */
function firstPlanCard(page: Page) {
  return page
    .getByRole("listitem")
    .filter({ has: page.getByRole("heading", { level: 2 }) })
    .first();
}

test.describe("Auto-schedule this service (recurring plan opt-in)", () => {
  test("a daily plan starts the day after the booking and can be skipped, retimed, paused, resumed and cancelled", async ({
    page,
  }) => {
    const fixture = loadFixture();
    await authenticateAsSeededCustomer(page, fixture);

    // Placed on today + BOOKED_DATE_OFFSET_DAYS, repeating daily from the day
    // after - NOT from the booked date. 4 days in all, so 3 plan visits.
    await createBookingViaUi(page, fixture, { kind: "daily", visits: 4, payFromWallet: false });

    const expectedNextVisit = await formatInPage(page, BOOKED_DATE_OFFSET_DAYS + DAILY_INTERVAL_DAYS);
    const bookedDate = await formatInPage(page, BOOKED_DATE_OFFSET_DAYS);

    await page.goto("/recurring-bookings");
    await expect(page.getByRole("heading", { name: "Recurring bookings" })).toBeVisible();

    const planCard = firstPlanCard(page);
    await expect(planCard.getByRole("heading", { name: fixture.serviceName })).toBeVisible({
      timeout: 15_000,
    });
    await expect(planCard).toContainText("Every day");
    await expect(planCard).toContainText("Active");
    await expect(planCard).toContainText("Each visit, as it's booked");
    // The card says when a visit is, not just which day, and what is already booked.
    await expect(planCard).toContainText("E2E Anytime 00:00");
    await expect(planCard.getByText("Already booked")).toBeVisible();

    // The whole point: the next visit is a day after the booking, not on it.
    // (The "Started" row carries the same date, since the plan's start date
    // *is* its first occurrence - hence a count rather than a bare contains.)
    await expect(planCard.getByText(expectedNextVisit)).toHaveCount(2);
    await expect(planCard).not.toContainText(bookedDate);

    // Bounded by the 3 repeat visits asked for, not by the booking itself.
    await expect(planCard).toContainText("0 of 3");
    await planCard.getByRole("button", { name: "Show upcoming" }).click();
    // Exact: the card also carries a "Next visit" detail row, and a substring
    // match picks up its wrapper as well once this section is expanded.
    await expect(planCard.getByText("Next visits", { exact: true })).toBeVisible({ timeout: 15_000 });
    await expect(planCard.locator("li").filter({ hasText: "(projected)" })).toHaveCount(3);

    // Skip visits until a date: the plan stays Active and its next visit moves out.
    await planCard.getByRole("button", { name: "Skip visits" }).click();
    const skipDialog = page.getByRole("dialog", { name: "Skip visits until a date" });
    await expect(skipDialog).toBeVisible();
    await skipDialog.getByLabel("Visits resume on").fill(await isoInPage(page, SKIP_UNTIL_OFFSET_DAYS));
    // Nothing of the plan is booked before that date, so the dialog says there is nothing to cancel instead of
    // offering a checkbox - and says that skipping itself is free.
    await expect(skipDialog.getByText("No visits are booked before that date, so there is nothing to cancel.")).toBeVisible();
    await expect(skipDialog.getByText(/Skipping is free/)).toBeVisible();
    await expect(skipDialog.getByRole("checkbox")).toHaveCount(0);
    await skipDialog.getByRole("button", { name: "Skip visits" }).click();
    await expect(skipDialog).toBeHidden({ timeout: 15_000 });
    const skippingUntil = await formatInPage(page, SKIP_UNTIL_OFFSET_DAYS);
    await expect(planCard.getByText("Skipping visits until")).toBeVisible({ timeout: 15_000 });
    await expect(planCard.getByText(skippingUntil).first()).toBeVisible();
    await expect(planCard).toContainText("Active");

    // Change time: the plan's other windows are offered, with the current one marked.
    await planCard.getByRole("button", { name: "Change time" }).click();
    const timeDialog = page.getByRole("dialog", { name: "Change visit time" });
    await expect(timeDialog).toBeVisible();
    await expect(timeDialog.getByRole("radio", { name: /E2E Anytime/ })).toBeVisible({ timeout: 15_000 });
    await expect(timeDialog.getByText("Current")).toBeVisible();
    await expect(timeDialog.getByText(/Changing the time is free/)).toBeVisible();
    // The only window is the one it already has, so there is nothing to change to yet.
    await expect(timeDialog.getByRole("button", { name: "Change time" })).toBeDisabled();
    await timeDialog.getByRole("button", { name: "Never mind" }).click();
    await expect(timeDialog).toBeHidden();

    // Pause / resume / cancel - the management actions the row calls for. Pausing says what it does and does not do
    // before it happens.
    await planCard.getByRole("button", { name: "Pause" }).click();
    const pauseDialog = page.getByRole("dialog", { name: "Pause this plan?" });
    await expect(pauseDialog).toBeVisible();
    await expect(pauseDialog.getByText(/Pausing is free/)).toBeVisible();
    await expect(pauseDialog.getByText("You have no visits booked right now")).toBeVisible();
    await pauseDialog.getByRole("button", { name: "Pause plan" }).click();
    await expect(planCard).toContainText("Paused", { timeout: 15_000 });
    await expect(planCard).toContainText("No new visits are booked while it's paused");

    await planCard.getByRole("button", { name: "Resume" }).click();
    await expect(planCard).toContainText("Active", { timeout: 15_000 });

    await planCard.getByRole("button", { name: "Cancel plan" }).click();
    await page.getByRole("button", { name: "Yes, cancel plan" }).click();
    await expect(planCard).toContainText("Cancelled", { timeout: 15_000 });
  });

  test("a daily plan can be set to pay each day's visit from the wallet", async ({ page }) => {
    const fixture = loadFixture();
    await authenticateAsSeededCustomer(page, fixture);

    await createBookingViaUi(page, fixture, { kind: "daily", visits: 3, payFromWallet: true });

    await page.goto("/recurring-bookings");
    const planCard = firstPlanCard(page);
    await expect(planCard.getByRole("heading", { name: fixture.serviceName })).toBeVisible({
      timeout: 15_000,
    });
    await expect(planCard).toContainText("Each visit, from your wallet");

    await planCard.getByRole("button", { name: "Cancel plan" }).click();
    await page.getByRole("button", { name: "Yes, cancel plan" }).click();
    await expect(planCard).toContainText("Cancelled", { timeout: 15_000 });
  });

  test("a prepaid plan is paid for in the one checkout and cancels its remaining visits", async ({ page }) => {
    const fixture = loadFixture();
    await authenticateAsSeededCustomer(page, fixture);

    await createBookingViaUi(page, fixture, { kind: "prepaid", frequency: "Every day", visits: 3 });

    await page.goto("/recurring-bookings");
    const planCard = firstPlanCard(page);
    await expect(planCard.getByRole("heading", { name: fixture.serviceName })).toBeVisible({
      timeout: 15_000,
    });
    await expect(planCard).toContainText("Every day");
    await expect(planCard).toContainText("Paid in advance");

    // Its visits already exist and are paid for, so the per-visit controls of a pay-as-you-go plan are absent.
    await expect(planCard.getByRole("button", { name: "Skip visits" })).toHaveCount(0);
    await expect(planCard.getByRole("button", { name: "Change time" })).toHaveCount(0);

    await planCard.getByRole("button", { name: "Cancel remaining visits" }).click();
    await page.getByRole("button", { name: "Yes, cancel them" }).click();
    await expect(planCard).toContainText("Cancelled", { timeout: 15_000 });
  });

  test("the plan-type tiles switch between a daily plan and a prepaid plan", async ({ page }) => {
    const fixture = loadFixture();
    await authenticateAsSeededCustomer(page, fixture);

    await page.goto(`/booking/summary?serviceSlug=${fixture.serviceSlug}`);
    await expect(page.getByRole("heading", { name: "Review your booking" })).toBeVisible();

    await page.getByRole("checkbox", { name: "Auto-schedule this service" }).check();

    // Prepaid is the default - it is what auto-scheduling always was - and it lets the customer pick a cadence.
    const prepaid = page.getByRole("radio", { name: /^Prepaid plan/ });
    const daily = page.getByRole("radio", { name: /^Daily plan/ });
    await expect(prepaid).toHaveAttribute("aria-checked", "true");
    for (const label of ["Every day", "Every week", "Every 2 weeks", "Every month"]) {
      await expect(page.getByRole("radio", { name: label, exact: true })).toBeVisible();
    }
    await expect(page.getByText("All of them are paid for in this one payment.")).toBeVisible();

    // A daily plan is always daily, so there is no cadence to pick; it says how each day is paid instead.
    await daily.click();
    await expect(daily).toHaveAttribute("aria-checked", "true");
    await expect(page.getByRole("radio", { name: "Every week", exact: true })).toHaveCount(0);
    await expect(
      page.getByRole("checkbox", { name: "Pay each day's visit from my wallet automatically" }),
    ).toBeVisible();
    await expect(page.getByText("A visit that isn't paid isn't carried out")).toBeVisible();

    // And back again: the cadence the customer had is still there.
    await prepaid.click();
    await expect(page.getByRole("radio", { name: "Every week", exact: true })).toBeVisible();
  });
});
