import { test, expect } from "@playwright/test";

/**
 * Task 317: sign-in through the real admin-web login form (SRS 25.2). No
 * OTP step here - unlike customer-web's dual-channel login, admin sign-in is
 * a plain email/password form (frontend/admin-web/src/app/login/page.tsx),
 * so this drives it directly rather than pre-seeding a session, and does not
 * need a dev-only test-auth bypass the way provider-web's mobile-OTP login
 * did (task "dev-only test-auth path for provider-web QA").
 */
test.describe("Admin sign-in", () => {
  test("signs in with the seeded Super Admin account and reaches the dashboard", async ({ page }) => {
    await page.goto("/login");
    await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();

    await page.getByLabel("Email").fill("dev-admin@nestly.local");
    // exact: true - a bare substring match also resolves the "Show password"
    // toggle button (its own aria-label contains "password"), which is a
    // Playwright strict-mode violation.
    await page.getByLabel("Password", { exact: true }).fill("E2eTest!Passw0rd");
    await page.getByRole("button", { name: "Sign in" }).click();

    await page.waitForURL(/\/dashboard/);
    await expect(page.getByRole("heading", { name: "Dashboard" })).toBeVisible();
  });

  test("rejects an incorrect password with an inline error, without navigating", async ({ page }) => {
    await page.goto("/login");

    await page.getByLabel("Email").fill("dev-admin@nestly.local");
    await page.getByLabel("Password", { exact: true }).fill("WrongPassword!123");
    await page.getByRole("button", { name: "Sign in" }).click();

    await expect(page.getByText("Invalid email or password.")).toBeVisible();
    await expect(page).toHaveURL(/\/login/);
  });
});
