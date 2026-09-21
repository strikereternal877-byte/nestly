# Enterprise-readiness gap walkthrough — customer, provider and admin

Manual end-to-end pass over the full order lifecycle — customer signup →
provider signup/onboarding → admin verification → booking → auto-assignment →
job execution → completion → payout — driven through real browsers
(customer-web :3000, provider-web :3002, admin-web :3001) and direct API
calls against the running local stack (consumer-api, provider-api, admin-api,
Postgres, Redis).

Unlike [QA-REPORT-2026-08-18.md](QA-REPORT-2026-08-18.md) and
[UAT-REPORT.md](UAT-REPORT.md), this pass is not a pass/fail acceptance check
against SRS criteria — everything below completed successfully. It is the
opposite: a list of what a real enterprise deployment still needs that this
walkthrough surfaced by actually doing the workflow end to end, once, as one
customer and one provider. Findings are filed here rather than in
`OPEN-FIXES-FEATURES.csv` because most are not one-line bugs — they are
missing subsystems (vendor integrations, ops tooling, observability) that
need their own scoping.

Date of pass: 2026-09-22. Test entities: customer `+919999900001`, provider
Rahul Verma (`c6a668cf-e1fe-4ed1-9fa3-5af5178e36ae`, Jaipur pincode 302033,
Home Cleaning), booking `GLX-260921-6FZQN` (Bathroom Deep Cleaning, ₹499).

Two real bugs were found and fixed along the way (not listed below, since
they are already closed): `AllowDevBypass` OTP local-testing bypass
(`OtpService`/`ProviderOtpService`), and a `SELECT DISTINCT` +
`gen_random_uuid()` dedupe bug in `bootstrap-bookability.sql`'s
`category_city_mapping` insert.

## Method

Every finding below was hit during the walkthrough, not inferred from
reading code alone:

- **E2E (browser)** = driven through the real app in a Chromium instance.
- **API** = exercised live via `curl` with a real JWT extracted from an
  authenticated browser session (never a typed-in password — see
  `.claude/CLAUDE.md`'s credential-handling rule).
- **DB** = confirmed by querying the live Postgres instance directly.

## Customer

| Gap | What happened this pass | What an enterprise product needs |
|---|---|---|
| OTP delivery has no real channel | Local sandbox notification provider never logs or exposes the OTP (by design, no-secrets-in-logs). A dev-only bypass code was added to unblock testing | A real SMS/email gateway (Twilio/Brevo) with delivery-failure alerting, automatic fallback channel, and a monitored bounce/complaint rate |
| Address geography auto-fill is not reliably wired | Setting the pincode field programmatically did not trigger the city/state auto-lookup; city/state and lat/long had to be filled by hand | A real address flow needs Google Places autocomplete or a map-pin drop, with the pincode lookup firing on every input path (paste, autofill, programmatic) — not just a manual keystroke |
| Live tracking silently degrades | Booking detail showed "Location sharing is off" — the provider's live position was never available to the customer for the whole job | Either make live location a hard requirement to start a job, or surface the degraded state to the *customer* too, not just the provider |
| Payment is sandbox-only | The entire payment step was `Pay ₹499.00 (Sandbox)` — no real gateway, no failure path, no webhook reconciliation exercised | Real gateway integration test coverage: card decline, webhook delay/replay, partial refund, gateway timeout |
| No customer-facing notifications observed | Booking confirmation, provider-assigned, provider-en-route — none produced a visible SMS/email/push during this pass | Delivery needs to be provable per booking (a notification log a support agent can pull up), not just fire-and-forget |
| Cancellation, reschedule, refund, support and post-service review were never exercised | This pass only went forward (book → complete) | Needs its own walkthrough — `BOOKING-FLOW-AUDIT.md` covers some of this from 2026-08-05, but not against current code |

## Provider

| Gap | What happened this pass | What an enterprise product needs |
|---|---|---|
| Background check is a manual admin toggle | Admin selected "Passed" from a dropdown with no evidence attached | Integration with a real verification vendor (webhook-driven async result), not an unverified admin click |
| KYC document has no authenticity check | Submitted `fileRef` was a plain text URL (`https://example.com/...`) pointing at nothing real; admin approved on sight | Real KYC needs OCR/liveness/document-authenticity checks and an expiry/re-verification cadence, not admin eyeballing a filename |
| Uploaded files go to local container disk | Both the KYC reference and the job-completion photo (`POST /jobs/{id}/completion-photos`) resolved to `App_Data/uploads` inside the container | Object storage (S3/GCS) + CDN + a retention/backup policy — a container filesystem does not survive a redeploy and does not scale past one instance |
| Completion photo has no content validation | A manufactured 1×1 pixel PNG was accepted as valid completion evidence | Basic image validation (dimensions, file size, not-solid-color heuristic) at minimum; ideally moderation |
| Job-offer alerting is in-app only | The 30-minute response window (`AutoAssignment:ResponseWindowMinutes`) produced no push/SMS during this pass, only an in-app badge | A working professional mid-job will miss an in-app-only alert; needs a loud channel (push + SMS) for time-boxed offers |
| Payout is instant, no hold/dispute window | `provider_earning_ledger` credited ₹424.15 the moment the job was marked complete | Real payouts need a hold period, a customer-dispute window before the money is released, UPI/NEFT batch payout, and tax/TDS handling |
| Rejection/reassignment retry chain untested | Only one eligible provider existed in Jaipur, so `ProviderAutoAssignmentHandler`'s retry-on-rejection path never ran | Needs a multi-provider scenario to actually exercise ranking, eligibility exclusion and the 3-retry cap |

## Admin

| Gap | What happened this pass | What an enterprise product needs |
|---|---|---|
| Provider activation is a single manual click | One admin approved KYC, recorded the background check, and activated the provider — all three in one session, no second approver | Sensitive actions (background-check outcome, activation) should require 4-eyes approval or at least a distinct audited actor per step at scale |
| No audit trail surfaced in the UI | admin-web showed the *result* of each action (badges, statuses) but never who-approved-what-when in a reviewable log | Regulated marketplaces need an immutable, admin-visible audit log, not just current-state badges |
| Silent auto-assignment failures | When no eligible candidate exists, the booking is left `AwaitingFulfilment` with no error and no alert — confirmed by reading `ProviderAutoAssignmentHandler`'s own doc comment ("no eligible candidate is not an error") | Correct as a booking-level design decision, but ops needs a dashboard of bookings stuck past some SLA, not silence |
| New-city launch is a developer-run SQL script | Onboarding Jaipur required manually running `database/seed/bootstrap-bookability.sql` with `psql` flags — and that script had a live bug (`DISTINCT` + `gen_random_uuid()` never deduping) that was only caught because this pass exercised it | An admin-UI-driven "launch a new city" workflow with validation, not a raw SQL script an operator runs by hand |
| Admin sessions expire with no graceful handling observed | Had to ask for a fresh login twice during this pass, mid-task | Not necessarily wrong (short-lived JWTs are reasonable), but no refresh-token flow or "your session is about to expire" warning was visible in the UI |
| Fulfilment promotion is a fixed 5-minute poll | `BookingFulfilmentPromotionJob` (Hangfire, `*/5 * * * *`) is what actually triggers auto-assignment; this pass had to wait a real 5 minutes for a same-day booking to become assignable | Fine at current volume; worth revisiting as booking volume grows (event-driven trigger on payment confirmation, cron as a safety-net sweep only) |

## Cross-cutting

- **No automated E2E coverage of this exact flow.** Everything in this
  document was driven manually (browser + curl) in one sitting. A real
  product would run this signup → booking → completion → payout chain as a
  Playwright suite in CI, continuously — see `frontend/customer-web/e2e/`
  for the existing (narrower) precedent.
- **Single-provider blind spot.** Rahul Verma was the only eligible provider
  in Jaipur/Home Cleaning for the entire pass, so the ranking algorithm
  (travel time, capacity, rejection-retry) never had a real choice to make.
  Every finding above about matching/assignment needs re-testing with at
  least 2-3 concurrent eligible providers.
- **The OTP dev-bypass is a real security-relevant change.** It is
  config-gated to `appsettings.Development.json` only (never
  `appsettings.json` or a deployed environment's config — see
  `OtpOptions.AllowDevBypass`'s doc comment), but that gate is currently only
  enforced by convention. A CI check that fails the build if
  `AllowDevBypass` (or any `_comment`-flagged dev-only key, matching the
  existing `DevAuth` pattern) ever appears outside a `*.Development.json`
  file would make that guarantee load-bearing instead of just documented.
