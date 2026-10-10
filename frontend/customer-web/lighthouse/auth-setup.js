/**
 * Lighthouse CI `puppeteerScript` for task #350's mobile perf-budget job.
 *
 * The checkout page (`/booking/summary`) is wrapped in `RequireAuth`
 * (see `src/components/RequireAuth.tsx`), which reads a session from
 * `sessionStorage` (see `src/lib/auth.ts`) and client-redirects to `/login`
 * when it's missing. A plain Lighthouse navigation never has that session,
 * so without this script the checkout audit would silently measure the
 * login-redirect skeleton instead of the real checkout page.
 *
 * WHY THIS DOESN'T JUST `page.goto()` AND `page.evaluate()` ON ITS OWN PAGE:
 * that was the original approach here, and it never actually worked (masked
 * behind a crash until 56f3e05e fixed how this script itself gets loaded -
 * `@lhci/cli` v0.15.1 hands this function a `browser`, not a `page`, so
 * `page.goto` simply didn't exist). Even with a `browser.newPage()` added,
 * it still wouldn't work: `@lhci/cli`'s LighthouseRunner spawns the actual
 * Lighthouse audit as a *separate CLI child process* per run
 * (node-runner.js), which connects back to this same browser over its
 * remote-debugging port and always creates its *own* brand-new page for the
 * real navigation whenever it isn't handed a page directly - which the
 * CLI+port flow LHCI uses never does
 * (lighthouse/core/gather/navigation-runner.js:
 * `lhPage = await lhBrowser.newPage()`). sessionStorage is scoped per
 * top-level browsing context (tab), so anything set on a page this script
 * creates is invisible on the separate page Lighthouse's own gather process
 * later creates for the actual audit - confirmed by reading through both
 * packages' installed source rather than assumed.
 *
 * THE FIX: rather than seed one throwaway page, seed *every* page this
 * browser ever opens. `browser.on("targetcreated", ...)` fires for every
 * new page any code creates on this browser - including the ones Lighthouse
 * itself creates for each of `numberOfRuns`'s separate runs, since they all
 * connect to this same long-lived browser instance. `page.evaluateOnNewDocument`
 * registers a script that runs before any of the page's own scripts, on
 * every navigation of that page - so by the time Lighthouse navigates its
 * fresh page to the real target URL, sessionStorage is already seeded.
 * Registered once here, it stays attached for the rest of this `lhci
 * autorun` process's life, covering every run for every audited URL.
 */
module.exports = async (browser) => {
  const token = process.env.LHCI_CUSTOMER_TOKEN;
  const expiresAt = process.env.LHCI_CUSTOMER_TOKEN_EXPIRES;

  if (!token || !expiresAt) {
    // No seeded session available (e.g. a local dry run against mocked/empty
    // data) — leave sessionStorage untouched rather than failing the whole
    // collect run. The checkout URL will then render its real "redirecting
    // to sign in" skeleton, which is still a valid (if less meaningful)
    // audit target.
    return;
  }

  const sessionValues = {
    accessToken: token,
    // The refresh token itself is never exercised by a Lighthouse audit
    // (no request runs long enough to need a refresh) — only its presence
    // matters, since lib/auth.ts's isAuthenticated() only reads the
    // access token + expiry.
    refreshToken: process.env.LHCI_CUSTOMER_REFRESH_TOKEN ?? "lhci-unused-refresh-token",
    tokenExpiresAt: expiresAt,
  };

  browser.on("targetcreated", async (target) => {
    const page = await target.page();
    if (!page) return; // Not a page target (e.g. a background/service worker) - nothing to seed.

    await page.evaluateOnNewDocument((values) => {
      sessionStorage.setItem("nestly.accessToken", values.accessToken);
      sessionStorage.setItem("nestly.refreshToken", values.refreshToken);
      sessionStorage.setItem("nestly.accessTokenExpiresAt", values.tokenExpiresAt);
    }, sessionValues);
  });
};
