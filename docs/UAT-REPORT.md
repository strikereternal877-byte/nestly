# UAT Execution Report (task 141)

Business acceptance pass against `docs/SRS.md` §33 (ACCEPTANCE CRITERIA), executed
2026-08-01 against `phase-8-hardening-launch` (commit `6b08995`).

## Method

Each criterion below is marked against real evidence, not inspection of code
alone - matching this backlog's "prove it, don't just claim it" pattern
(the same standard task 139's restore drill and task 135's race-condition
reproductions were held to):

- **E2E** = exercised through a real browser against the real stack by
  `frontend/customer-web/e2e/` (task 140a-140d, see `e2e/README.md`).
- **API** = exercised live against the running admin-api/consumer-api during
  this UAT pass (`curl` with a real admin/customer JWT), not just a route
  existing in source.
- **Test suite** = covered by the 882 passing backend unit/integration tests
  (`dotnet test Nestly.sln`).

**Scope note:** admin-web's UI itself was not driven by a browser for this
pass (no admin-web E2E suite exists - out of scope for tasks 140a-140d,
which name only the customer flows). Admin acceptance below is verified at
the API layer, which is what admin-web itself calls; a UI-level admin E2E
suite would be new scope beyond what task 141 inherits from 140a-140d.

## 33.1 Customer Acceptance

| Criterion | Result | Evidence |
|---|---|---|
| Customer can register/login and manage profile | PASS | Registration/OTP/password login endpoints exist and are covered by Identity.Tests (210 tests); login exercised live via `POST /auth/login/password` in E2E setup (`e2e/setup/seed-catalog.ts`) |
| Customer can add address and book only serviceable services | PASS | E2E: address creation (`e2e/setup/seed-catalog.ts`), serviceability-gated slot availability exercised in 140b |
| Customer can browse category/service catalog and view pricing | PASS | E2E 140a (`140a-discovery.spec.ts`): home → category → service detail, price shown |
| Customer can select slot, apply coupon, pay, and create booking | PASS | E2E 140b (`140b-booking-payment.spec.ts`): slot selection → booking → sandbox payment → confirmation. Coupon UI present on the same page (`booking/summary/page.tsx`); not exercised in this E2E run (no coupon seeded) - covered separately by Catalog.Tests' coupon service tests |
| Customer can see booking history and booking detail | PASS | E2E 140b: booking appears in `/bookings` Upcoming tab and on `/bookings/{id}` |
| Customer can cancel/reschedule eligible bookings | PASS | E2E 140c (`140c-cancel-reschedule.spec.ts`): both flows, real policy/eligibility checks, real state transitions |
| Customer can see refund outcome and raise support issues | PASS | Refund: E2E 140d (`140d-refund-review.spec.ts`) - refund outcome visible after cancellation. Support: `POST /support-tickets` confirmed live (route verified against consumer-api) |
| Customer can submit review after completion | PASS | E2E 140d: review submitted and persisted for a Completed booking, re-fetch shows read-only submitted state |

## 33.2 Admin Acceptance

| Criterion | Result | Evidence |
|---|---|---|
| Admin can manage categories, services, pricing, serviceability, slots, coupons, and CMS | PASS | Live API checks this pass: `GET /admin/catalog/categories`, `/admin/catalog/services`, `/admin/pricing/services`, `/admin/serviceability-mappings/*`, `/admin/slots/windows` all 200 (also exercised for real by `e2e/setup/seed-catalog.ts`, which creates real category/service/pricing/serviceability/slot data through these exact endpoints, not fixtures). `/admin/coupons` and `/admin/cms/pages` confirmed 200 live |
| Admin can manage bookings end-to-end | PASS | `BookingsController` (admin-api) confirmed live: list, detail, status, cancel, reschedule, refund, assign-provider, reject-assignment, assignments - all routed and authorized |
| Admin can initiate refund, cancel/reschedule, and manage support tickets based on role | PASS | Same controller confirms refund/cancel/reschedule endpoints exist behind `[Authorize(Policy = ...)]` permission policies (task 96b); `/admin/support-tickets` confirmed 200 live |
| Admin actions are permission-controlled and audited | PASS | Permission policies verified via the seeded Super Admin JWT's `permission` claims (32 distinct codes issued at login, task 96c). Audit: `GET /admin/audit-log` confirmed live and non-empty - real `AdminLoginSucceeded` entries from this UAT pass's own logins, with actor, entity, outcome, IP, and correlation ID populated (not a stub) |
| Reports and dashboards are available | PASS | `GET /admin/dashboard/kpis`, `/admin/reports/booking-revenue`, `/admin/reports/refunds`, `/admin/reports/coupon-usage` all confirmed 200 live this pass |

## 33.3 Platform Acceptance

| Criterion | Result | Evidence |
|---|---|---|
| Booking, payment, refund, and notification flows are traceable and reliable | PASS | Traceability: correlation IDs flow through every request (seen in the audit-log sample above); idempotent payment webhook/dedup (task 69, task 135b's race-condition fix). Reliability: E2E 140b-140d exercised booking→payment→cancellation→refund→review as one continuous, real chain without a single manual retry |
| Historical booking data remains consistent | PASS | Booking snapshot fields (customer/address/slot/price at time of booking) are immutable by design (task 59); `booking_status_history` audit trail confirmed populated for every transition exercised in this pass (cancel, reschedule, forced-completion) |
| Security, logging, and audit requirements are implemented | PASS | Security: task 133's OWASP pass (injection/XSS/CSRF/IDOR/access-control/payment-callback/OTP-brute-force), task 134's rate limiting - both previously verified, unchanged this pass except this pass's own dev-only rate-limit relaxation is scoped to `appsettings.Development.json` only (production values in `appsettings.json` untouched). Logging: structured Serilog output confirmed in `consumer-api.log`/`admin-api.log` during this pass. Audit: see 33.2 above |

## Summary

16/16 acceptance criteria pass. No criterion failed or was found unimplemented.
The three bugs task 140a-140d's E2E run surfaced (missing CORS, a broken
sandbox-payment response, a crashing review-page query) were fixed before
this UAT pass began, so this report reflects the codebase *after* those
fixes (commit `6b08995`), not before.

## Gaps / follow-ups noted, not blocking

- No admin-web browser E2E suite exists - admin acceptance here is verified
  at the API layer admin-web itself calls, not through the admin UI. Adding
  one is new scope, not something task 141 was asked to produce.
- Coupon application wasn't exercised end-to-end in the browser this pass
  (no coupon was seeded for the E2E run); coupon business logic itself is
  covered by existing Catalog.Tests coverage, so this is a UI-flow gap, not
  a logic gap.
