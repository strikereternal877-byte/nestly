import { test, expect } from "@playwright/test";
import type { Page } from "@playwright/test";
import { authenticateAsSeededProvider, loadFixture } from "./setup/auth";

/**
 * Task 385: the job lifecycle the 2026-08-18 QA sweep listed as unwalked past
 * "Assigned" - Accept -> En route -> Arrived -> In progress -> Completed,
 * driven entirely through provider-web's own buttons against a real
 * provider-api.
 *
 * One test rather than five, with a `test.step` per transition. These are not
 * independent cases: each one is only reachable from the state the previous
 * one left behind (`BookingLifecycle`'s transition table), and Playwright
 * gives every test a fresh page/context, so five tests would need either a
 * shared mutable page or five separately-seeded bookings. The steps report
 * individually either way, and a failure names the exact transition that
 * broke.
 *
 * The lifecycle deliberately walks the *optional* branch: en-route and
 * arrived are not mandatory (`Assigned -> InProgress` is legal on its own),
 * but they are the two states the sweep called unwalked, and once en route,
 * arrived is the only way forward - `ProviderEnRoute -> InProgress` is not a
 * legal edge.
 */

/** Smallest valid PNG - the endpoint checks size and content type, never pixels. */
const ONE_PIXEL_PNG = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==",
  "base64",
);

test.describe("Provider job lifecycle", () => {
  test("walks a job from Assigned through to Completed", async ({ page }) => {
    // Six real round trips through the API plus a photo upload; the default
    // 30s budget is not enough for the whole walk on a cold server.
    test.setTimeout(180_000);

    const fixture = loadFixture();
    await authenticateAsSeededProvider(page, fixture);
    await page.goto(`/jobs/${fixture.bookingId}`);

    await expect(page.getByRole("heading", { name: fixture.customerName })).toBeVisible({
      timeout: 15_000,
    });

    await test.step("Assigned: accepts the offered job", async () => {
      await expect(statusBadge(page, "Assigned")).toBeVisible();
      await page.getByRole("button", { name: "Accept this job" }).click();
      await expect(statusBadge(page, "Accepted")).toBeVisible({ timeout: 15_000 });

      // The accept/decline block is replaced by the go-to-work card - proof
      // the screen re-read the job rather than only flipping a local flag.
      await expect(page.getByRole("button", { name: "Accept this job" })).toHaveCount(0);
      await expect(page.getByText("Ready to go?")).toBeVisible();
    });

    await test.step("Accepted: marks the job en route", async () => {
      await page.getByRole("button", { name: /On my way/ }).click();
      await expect(statusBadge(page, "On the way")).toBeVisible({ timeout: 15_000 });

      // "Start job" is hidden while en route rather than left to fail with a
      // 422: BookingLifecycle has no ProviderEnRoute -> InProgress edge.
      await expect(page.getByRole("button", { name: "Start job" })).toHaveCount(0);
    });

    await test.step("En route: marks arrival at the address", async () => {
      await page.getByRole("button", { name: /arrived/i }).click();
      await expect(statusBadge(page, "Arrived")).toBeVisible({ timeout: 15_000 });
    });

    await test.step("Arrived: starts the work", async () => {
      await page.getByRole("button", { name: "Start job" }).click();
      await expect(statusBadge(page, "In progress")).toBeVisible({ timeout: 15_000 });

      // Completion is gated on evidence server-side
      // (BookingCompletionProofSupport.EnsureCompletionProofExistsAsync); the
      // UI reflects that gate rather than letting the call fail.
      await expect(page.getByRole("button", { name: "Mark complete" })).toBeDisabled();
      await expect(page.getByText("Submit the completion verification below first.")).toBeVisible();
    });

    await test.step("In progress: submits photo + checklist evidence", async () => {
      // The real input is `hidden` behind a "Take or choose photo" button
      // (it carries `capture="environment"` for the camera on a phone).
      // setInputFiles drives the input directly, which is what the button's
      // click handler does anyway.
      await page.locator('input[type="file"]').setInputFiles({
        name: "completion.png",
        mimeType: "image/png",
        buffer: ONE_PIXEL_PNG,
      });
      await expect(page.getByRole("button", { name: "Remove photo" })).toBeVisible({
        timeout: 30_000,
      });

      // exact: true is load-bearing - the checkbox beside this field is labelled
      // "Mark checklist item 1 as done", which getByLabel's default substring
      // match also resolves, giving a strict-mode violation on two elements.
      await page.getByLabel("Checklist item 1", { exact: true }).fill("Deep cleaned the whole flat");
      await page.getByLabel("Mark checklist item 1 as done").check();

      await page.getByRole("button", { name: "Submit verification" }).click();

      await expect(page.getByText(/^Submitted /)).toBeVisible({ timeout: 30_000 });
      await expect(page.getByText("Deep cleaned the whole flat")).toBeVisible();
      await expect(page.getByRole("button", { name: "Resubmit verification" })).toBeVisible();
    });

    await test.step("In progress: marks the job complete", async () => {
      const complete = page.getByRole("button", { name: "Mark complete" });
      await expect(complete).toBeEnabled({ timeout: 15_000 });
      await complete.click();

      await expect(statusBadge(page, "Completed")).toBeVisible({ timeout: 15_000 });
      await expect(page.getByRole("button", { name: "Mark complete" })).toHaveCount(0);
      // Not asserting "Rate the customer" here: CompleteAsync (ProviderJobService)
      // deliberately leaves the underlying Booking at InProgress - only an
      // admin approving the completion proof (BookingManagementService.
      // ApproveCompletionProofAsync) moves it to Completed, which is what
      // CustomerRatingService's eligibility check actually requires. The
      // provider's own "Mark complete" (tested above via the Assigned
      // badge/list-card status) never unlocks rating by itself. This test is
      // scoped to the provider's own button-driven walk (see its own doc
      // comment) and has no admin-api dependency to drive that separate step.
    });

    await test.step("Completed: the list agrees with the detail screen", async () => {
      await page.goto("/jobs");
      const card = page.locator(`a[href="/jobs/${fixture.bookingId}"]`);
      await expect(card).toBeVisible({ timeout: 15_000 });
      await expect(card.getByText("Completed")).toBeVisible();
    });
  });
});

/**
 * The status pill in the job's own page heading.
 *
 * Scoped rather than matched page-wide for the reason this repo's other
 * suites keep hitting: the same label renders in more than one place. The
 * app shell's `ProviderHeader` is also a `<header>`, so the filter narrows to
 * the one containing the page's `<h1>` (`PageHeading` renders both together);
 * `JobStatusBadge` puts the identical text on every list card too.
 */
function statusBadge(page: Page, label: string) {
  return page
    .locator("header")
    .filter({ has: page.getByRole("heading", { level: 1 }) })
    .getByText(label, { exact: true });
}
