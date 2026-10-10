import { defineConfig, devices } from "@playwright/test";

/**
 * E2E suite (task 385): real browser tests driving provider-web against a
 * real provider-api (:5337), admin-api (:5177), consumer-api (:5257) and a
 * real Postgres/Redis, per docs/TESTING.md END-TO-END TESTING ("critical
 * business journeys rather than exhaustive UI coverage"). Mirrors
 * frontend/customer-web/playwright.config.ts and
 * frontend/admin-web/playwright.config.ts exactly (same runner, same
 * conventions, same globalSetup/fixture pattern) - see e2e/README.md for how
 * to start the stack. `globalSetup` only seeds test data through those APIs,
 * it does not start docker-compose/dotnet/next.
 *
 * The two `use` options the sibling configs don't have are deliberate: the
 * job detail screen starts `navigator.geolocation.watchPosition` the moment a
 * job is en route/arrived/in progress (src/hooks/useLocationSharing.ts), and
 * without a granted permission and a fixed position that card renders its
 * "denied" branch on every run. Granting both exercises the real
 * location-ingest path instead of a permission error.
 */
export default defineConfig({
  testDir: "./e2e",
  globalSetup: "./e2e/setup/global-setup.ts",
  fullyParallel: false,
  // 2 retries in CI only: the ci.yml `e2e` job runs 3 .NET APIs + Next.js +
  // Chromium together on one small runner, so a single request occasionally
  // loses a race against cold JIT/EF-query-compilation on first hit and
  // times out - not a flaky assertion, a flaky *environment*. Local runs get
  // the immediate, no-retry failure a real bug should produce.
  retries: process.env.CI ? 2 : 0,
  workers: 1,
  reporter: [["list"]],
  use: {
    baseURL: process.env.PROVIDER_WEB_URL ?? "http://localhost:3002",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    permissions: ["geolocation"],
    geolocation: { latitude: 12.9716, longitude: 77.5946 },
  },
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] },
    },
  ],
});
