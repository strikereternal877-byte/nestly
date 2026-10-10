import { defineConfig, devices } from "@playwright/test";

/**
 * E2E suite (tasks 140a-140d, SRS 33 UAT flows): real browser tests driving
 * this app against a real backend (consumer-api on :5257) and a real
 * Postgres/Redis, per docs/TESTING.md END-TO-END TESTING ("critical business
 * journeys rather than exhaustive UI coverage"). Requires the stack already
 * running - see e2e/README.md - `globalSetup` only creates the test catalog
 * data, it does not start docker-compose/dotnet/next.
 */
export default defineConfig({
  testDir: "./e2e",
  globalSetup: "./e2e/setup/global-setup.ts",
  fullyParallel: false,
  // 2 retries in CI only: shared CI runners occasionally lose a race against
  // cold JIT/EF-query-compilation on first hit and time out - not a flaky
  // assertion, a flaky *environment*. Local runs get the immediate,
  // no-retry failure a real bug should produce. Mirrors admin-web's and
  // provider-web's configs.
  retries: process.env.CI ? 2 : 0,
  workers: 1,
  reporter: [["list"]],
  use: {
    baseURL: process.env.CUSTOMER_WEB_URL ?? "http://localhost:3000",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] },
    },
  ],
});
