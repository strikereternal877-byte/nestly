import { test, expect } from "@playwright/test";
import { authenticateAsSeededAdmin, loadFixture } from "./setup/auth";

/**
 * Task 317: admin-user list-search -> detail -> write flow (SRS 12.2.1,
 * tasks 97a-97d). Deactivate/reactivate is the write flow rather than the
 * unlock control (task notes for #317) because getting a real account into
 * "locked out" state means driving several real failed logins first, which
 * would make this spec slower and flakier for no extra UI coverage - both
 * controls render from the same `AdminUserStatusBadge` +
 * `Account actions` card.
 *
 * Runs against the dedicated e2e-admin-user@nestly.local account (see
 * e2e/setup/seed-admin.ts), never against the dev-admin Super Admin session
 * this suite signs in as - deactivating your own signed-in account would be
 * self-defeating and is blocked by the UI anyway (`isSelf` in the detail
 * page). Ends the run back in "Active" so a re-run starts from the same
 * state seed-admin.ts already guarantees.
 */
test.describe("Admin user list, detail and activation lifecycle", () => {
  test("finds the seeded admin user by email and opens its detail page", async ({ page }) => {
    const fixture = loadFixture();
    await authenticateAsSeededAdmin(page, fixture);

    await page.goto("/admin-users");
    // level: 1 - the DataTable's own card title is a second, nested "Admin
    // users" heading (h2), which would otherwise make this locator ambiguous.
    await expect(page.getByRole("heading", { name: "Admin users", level: 1 })).toBeVisible();

    // Live filtering (no Search button on this page - see admin-users/page.tsx):
    // typing alone triggers the debounced query. There is also no "Search"
    // button anywhere else in the DOM to click by name - the only such match
    // is the header's GlobalSearch trigger ("Search pages and settings"),
    // which a stray click here used to open, stalling the rest of the test
    // behind its modal backdrop.
    await page.getByLabel("Email").fill(fixture.seededAdminUserEmail);

    // Scoped to the row's name link (not its "Manage" action, which shares
    // the same href) - matched by href to survive the table briefly showing
    // the previous (unfiltered) page while the filtered request is in flight.
    // `:visible` because the responsive DataTable renders every row twice -
    // once for the desktop table and once for the stacked mobile layout - and
    // hides one with CSS, so the bare href matches two elements.
    const nameLink = page.locator(`a[href="/admin-users/${fixture.seededAdminUserId}"]:visible`).getByText(
      fixture.seededAdminUserFullName,
      { exact: true }
    );
    await expect(nameLink).toBeVisible({ timeout: 15_000 });
    await nameLink.click();

    await page.waitForURL(new RegExp(`/admin-users/${fixture.seededAdminUserId}`));
    await expect(page.getByRole("heading", { name: fixture.seededAdminUserFullName })).toBeVisible();
    await expect(page.getByText("Active", { exact: true })).toBeVisible();
  });

  test("deactivates then reactivates the seeded admin user", async ({ page }) => {
    const fixture = loadFixture();
    await authenticateAsSeededAdmin(page, fixture);

    await page.goto(`/admin-users/${fixture.seededAdminUserId}`);
    await expect(page.getByRole("heading", { name: fixture.seededAdminUserFullName })).toBeVisible();

    await page.getByRole("button", { name: "Deactivate account" }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Deactivate", exact: true }).click();
    await expect(page.getByText("Inactive", { exact: true })).toBeVisible({ timeout: 15_000 });

    await page.getByRole("button", { name: "Activate account" }).click();
    await expect(page.getByText("Active", { exact: true })).toBeVisible({ timeout: 15_000 });
  });
});
