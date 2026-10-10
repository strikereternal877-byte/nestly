/**
 * Lighthouse CI config for task #350 (backlog: mobile Core Web Vitals gate).
 *
 * Wired into `.github/workflows/ci.yml`'s `lighthouse-mobile` job, which
 * runs a real consumer-api + admin-api + Postgres and seeds one real
 * category/service/address/customer-session through those APIs (see
 * `e2e/setup/seed-catalog.ts`, reused from the existing 140a-140d E2E
 * suite) before starting `next build && next start` and pointing this
 * config at it. This is a deliberate choice over auditing mocked/stubbed
 * API responses: these three pages fetch live data from consumer-api at
 * render time, so a budget that never talks to a real backend risks
 * passing on a page that would 500/empty-state against the real API. See
 * the workflow job's own header comment for the full tradeoff writeup,
 * including what is NOT covered by this (e.g. CDN/production network
 * topology, provider-side job load).
 *
 * Scope: home, service detail, and checkout (`/booking/summary` — chosen
 * over `/booking/payment/[id]` because summary is the page a customer
 * actually configures and commits a booking from; payment is a thinner
 * "confirm and pay" step reached only after summary succeeds, and would
 * additionally require a real booking to already exist for every audit).
 *
 * Metrics budgeted: LCP and CLS map directly onto Lighthouse's own lab
 * metrics of the same name. INP has **no lab equivalent in a plain
 * navigation-mode Lighthouse run** — real INP is measured from an actual
 * user interaction, either as CrUX field data or via a Lighthouse
 * timespan/user-flow that scripts an interaction, neither of which a
 * `lhci autorun` page-load audit does. Total Blocking Time (TBT) is
 * budgeted instead as the standard lab proxy for input responsiveness
 * (per Lighthouse/web.dev guidance) — this is a deliberate, documented
 * substitution, not a mislabeled INP number.
 *
 * Thresholds are web.dev's published "good" Core Web Vitals thresholds
 * (LCP <= 2.5s, CLS <= 0.1) plus Lighthouse's own "good" TBT threshold
 * (<= 200ms, the same 200ms boundary INP itself uses) rather than an
 * invented number.
 *
 * Mobile + throttling: `formFactor: "mobile"` plus Lighthouse's own default
 * navigation-mode throttling (simulated mid-tier mobile CPU + a slow-4G-class
 * network profile) is used as-is — no custom throttling numbers are
 * invented, per the task brief.
 */
const BASE_URL = process.env.LHCI_BASE_URL ?? "http://localhost:3000";
const SERVICE_SLUG = process.env.LHCI_SERVICE_SLUG ?? "e2e-deep-clean";

/** web.dev "good" thresholds; TBT's 200ms boundary is Lighthouse's INP-proxy threshold — see file header. */
const BUDGET = {
  lcpMs: 2500,
  clsScore: 0.1,
  tbtMsAsInpProxy: 200,
};

module.exports = {
  ci: {
    collect: {
      url: [
        `${BASE_URL}/`,
        `${BASE_URL}/services/${SERVICE_SLUG}`,
        `${BASE_URL}/booking/summary?serviceSlug=${SERVICE_SLUG}`,
      ],
      // Median-of-3 rather than a single run: navigation-mode Lighthouse
      // metrics (especially TBT) are noisy enough on shared CI runners that
      // a single run would make this gate flaky in exactly the way the task
      // brief warns against ("fails the build instead of shipping
      // silently" only holds if the gate itself is trustworthy).
      numberOfRuns: 3,
      settings: {
        formFactor: "mobile",
        screenEmulation: {
          mobile: true,
          width: 390,
          height: 844,
          deviceScaleFactor: 3,
          disabled: false,
        },
      },
      // GitHub's ubuntu-latest runners have disabled unprivileged user
      // namespaces (AppArmor), which Chrome's own sandbox needs - without
      // this, the browser process dies immediately with
      // "FATAL:zygote_host_impl_linux.cc No usable sandbox!" before
      // Lighthouse ever gets a page to audit. The CI job's own container is
      // already the isolation boundary here, same reasoning every other
      // CI-run Chrome (Playwright's `--with-deps chromium` included) relies
      // on. `settings.chromeFlags` (the usual place for this) is silently
      // ignored whenever `puppeteerScript` is set - LHCI logs "WARNING:
      // collect.settings.chromeFlags option will be ignored" and launches
      // through Puppeteer directly instead of chrome-launcher - so this has
      // to go through `puppeteerLaunchOptions.args`, Puppeteer's own launch
      // option, to actually reach the browser process auth-setup.js's `page`
      // comes from.
      puppeteerLaunchOptions: {
        args: ["--no-sandbox", "--disable-gpu"],
      },
      // A relative path, not require.resolve()'s absolute one: LHCI's own
      // PuppeteerManager.invokePuppeteerScriptForUrl loads this via
      // `require(path.join(process.cwd(), scriptPath))` - path.join, unlike
      // path.resolve, does not special-case an already-absolute second
      // argument, so an absolute scriptPath gets cwd prepended onto it
      // anyway, doubling it into a path that can never exist
      // ("…/frontend/customer-web/home/runner/…/frontend/customer-web/
      // lighthouse/auth-setup.js") - confirmed against the installed
      // @lhci/cli source. process.cwd() here is frontend/customer-web (this
      // config's own directory - see ci.yml's `working-directory` for this
      // step), so a path relative to it is exactly what a relative path
      // from this file already looks like.
      puppeteerScript: "./lighthouse/auth-setup.js",
    },
    assert: {
      // No `preset` — intentionally scoped to exactly the three budgeted
      // metrics below, not a general Lighthouse category-score gate (which
      // would fail the build on unrelated audits, e.g. SEO/PWA/best-practices,
      // that are out of scope for task #350).
      assertions: {
        "largest-contentful-paint": ["error", { maxNumericValue: BUDGET.lcpMs }],
        "cumulative-layout-shift": ["error", { maxNumericValue: BUDGET.clsScore }],
        "total-blocking-time": ["error", { maxNumericValue: BUDGET.tbtMsAsInpProxy }],
      },
    },
    upload: {
      target: "filesystem",
      outputDir: "./lhci-reports",
    },
  },
};
