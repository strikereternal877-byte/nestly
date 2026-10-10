import { test, expect } from "@playwright/test";
import { authenticateAsSeededProvider, loadFixture } from "./setup/auth";

/**
 * Task 385: the provider's entry point - the auth guard, the job list, and
 * its filters, against the job `globalSetup` seeded into `Assigned`.
 *
 * Runs before 385b by filename, deliberately: 385b walks that same job all
 * the way to Completed, and these assertions are about how a job looks while
 * it is still waiting for a response.
 */
test.describe("Provider jobs list", () => {
  test("sends a signed-out visitor to the sign-in screen", async ({ page }) => {
    // No `authenticateAsSeededProvider` here - this is the guard under test
    // (`RequireProviderAuth`), and sessionStorage starts empty in a fresh
    // browser context.
    await page.goto("/jobs");

    await page.waitForURL(/\/login/, { timeout: 15_000 });
    await expect(page.getByRole("heading", { name: "Provider sign in" })).toBeVisible();
  });

  test("lists the newly assigned job with everything needed to answer it", async ({ page }) => {
    const fixture = loadFixture();
    await authenticateAsSeededProvider(page, fixture);
    await page.goto("/jobs");

    await expect(page.getByRole("heading", { name: "Jobs", exact: true })).toBeVisible();

    // Scoped to this job's own card rather than the page: other bookings from
    // earlier runs legitimately sit in this list, and several of them share a
    // customer name and address with this one.
    const card = page.locator(`a[href="/jobs/${fixture.bookingId}"]`);
    await expect(card).toBeVisible({ timeout: 15_000 });
    await expect(card.getByText("Assigned")).toBeVisible();
    await expect(card.getByText(fixture.customerName)).toBeVisible();
    await expect(card.getByText(fixture.addressLine1)).toBeVisible();
    // The card renders `{date} · {start}–{end}`, where jobs/page.tsx formats
    // each time as a bare `value.slice(0, 5)` (src/lib/format.ts formatTime).
    // Asserting the HH:MM–HH:MM shape rather than an exact string keeps this
    // from re-implementing that formatting in the test, where it would drift
    // silently the day the card starts showing 12-hour times.
    await expect(card).toContainText(/\d{2}:\d{2}–\d{2}:\d{2}/);
  });

  test("filters the list by status and by day", async ({ page }) => {
    const fixture = loadFixture();
    await authenticateAsSeededProvider(page, fixture);
    await page.goto("/jobs");

    const card = page.locator(`a[href="/jobs/${fixture.bookingId}"]`);
    await expect(card).toBeVisible({ timeout: 15_000 });

    await page.getByLabel("Status").selectOption("Assigned");
    await page.getByRole("button", { name: "Search" }).click();
    await expect(card).toBeVisible({ timeout: 15_000 });

    await page.getByLabel("Date").fill(fixture.slotDate);
    await page.getByRole("button", { name: "Search" }).click();
    await expect(card).toBeVisible({ timeout: 15_000 });

    // Asserting this job's card disappears, rather than asserting the empty
    // state appears: the seeded provider may genuinely have other jobs on the
    // next day, and this test is about the filter reaching the API, not about
    // the shape of the rest of the database.
    await page.getByLabel("Date").fill(dayAfter(fixture.slotDate));
    await page.getByRole("button", { name: "Search" }).click();
    await expect(card).toHaveCount(0, { timeout: 15_000 });

    // exact: true - a bare substring match also resolves the empty state's
    // own "Clear filters" button (shown because the date filter above just
    // produced zero results), a Playwright strict-mode violation.
    await page.getByRole("button", { name: "Clear", exact: true }).click();
    await expect(card).toBeVisible({ timeout: 15_000 });
  });

  test("opens the job detail from a list card", async ({ page }) => {
    const fixture = loadFixture();
    await authenticateAsSeededProvider(page, fixture);
    await page.goto("/jobs");

    const card = page.locator(`a[href="/jobs/${fixture.bookingId}"]`);
    await expect(card).toBeVisible({ timeout: 15_000 });
    await card.click();

    await page.waitForURL(new RegExp(`/jobs/${fixture.bookingId}$`), { timeout: 15_000 });
    await expect(page.getByRole("heading", { name: fixture.customerName })).toBeVisible({
      timeout: 15_000,
    });
  });
});

/** Local-parts arithmetic on a `YYYY-MM-DD` string - `new Date(iso)` would parse it as UTC midnight and shift the day in IST. */
function dayAfter(isoDate: string): string {
  const [year, month, day] = isoDate.split("-").map(Number);
  const next = new Date(year, month - 1, day + 1);
  return [
    next.getFullYear(),
    String(next.getMonth() + 1).padStart(2, "0"),
    String(next.getDate()).padStart(2, "0"),
  ].join("-");
}
