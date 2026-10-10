# DEVOPS.md

Docker, CI/CD, deployment, monitoring and operations standards.

## PURPOSE

This document defines the DevOps standards for building, packaging, deploying, and operating the Nestly platform.

It is the single source of truth for containerization, pipelines, environment management, health checks, and observability.

Open platform decisions (cloud provider, CI platform, registry, orchestrator) are tracked in OPEN DECISIONS below and must be resolved before production deployment.

## DEPLOYABLE UNITS

The platform produces four deployable applications plus supporting services:

| Unit | Source | Runtime |
|---|---|---|
| Consumer API | backend/consumer-api | ASP.NET Core (.NET 8) |
| Admin API | backend/admin-api | ASP.NET Core (.NET 8) |
| Customer Web | frontend/customer-web | Next.js (Node.js) |
| Admin Web | frontend/admin-web | Next.js (Node.js) |
| PostgreSQL | managed / container | Database |
| Redis | managed / container | Cache |
| Hangfire | hosted inside API process | Background jobs |

## CONTAINERIZATION

Every deployable unit must have its own Dockerfile.

Rules:

- Use multi-stage builds (SDK image for build, runtime image for execution).
- Use official Microsoft and Node base images with pinned versions.
- Run containers as a non-root user.
- Keep images minimal; no build tools in runtime images.
- Configuration comes from environment variables, never baked into images.
- Provide a docker-compose file for local development (APIs + PostgreSQL + Redis).

## ENVIRONMENTS

Four environments are supported, matching the configuration standards in DOTNET.md:

- Development
- Testing
- Staging
- Production

Rules:

- Same artifact/image is promoted across environments; only configuration differs.
- Secrets are supplied per environment, outside source control.
- Staging must mirror production topology as closely as possible.
- Production configuration changes must be auditable.

## CONFIGURATION AND SECRETS

- All configuration is external (environment variables or mounted config).
- Strongly typed configuration on the application side (see DOTNET.md).
- Secrets (connection strings, JWT keys, OTP hashing pepper, payment gateway keys, SMS/email provider keys) must come from a secret store — never from source code or images.
- Local development may use dotnet user-secrets / .env files that are gitignored.

## CI/CD PIPELINE

Every push to a feature branch and every pull request must run CI.

Required CI stages:

1. Restore and build (backend solution and frontend apps)
2. Unit tests
3. Integration tests (with disposable PostgreSQL/Redis containers)
4. Static analysis / linting (dotnet analyzers, ESLint, TypeScript checks)
5. Docker image build
6. Security/dependency scan

Required CD stages:

1. Publish versioned images to the container registry
2. Deploy to Staging automatically from develop
3. Deploy to Production from main with manual approval
4. Run EF Core migrations as an explicit, ordered deployment step
5. Support rollback to the previous image version

Branch strategy: feature branches → develop (staging) → main (production).

## DATABASE OPERATIONS

- Schema changes only through EF Core migrations (see DATABASE.md).
- Migrations run as a separate deployment step, not implicitly at app startup in Production.
- Backups: automated daily backups with tested restore procedure.
- Seed data scripts live in database/seed and must be idempotent.

## HEALTH CHECKS

Every API must expose:

- Liveness endpoint (process is up)
- Readiness endpoint (database, Redis, and critical dependencies reachable)

Orchestrators and load balancers must use these endpoints for routing and restarts.

## GRACEFUL SHUTDOWN

- Applications must handle SIGTERM and finish in-flight requests before exiting.
- Hangfire background jobs must support cancellation and safe re-execution.
- Payment and booking webhook processing must be idempotent so interrupted work can retry safely (see SRS §11.11, §26.3).

## OBSERVABILITY

Per SRS §29.6, the platform requires:

- Structured application logs (see CLAUDE.md LOGGING rules — never log secrets, tokens, or PII)
- Error logs with diagnostic context
- Audit logs for critical business and admin actions
- Metrics: request rate, latency, error rate, DB pool usage, background job health
- Health checks wired into monitoring
- Alerting for critical failures — payment, booking, and notification failures at minimum

Correlation IDs must flow from the frontend through APIs into logs.

## SCALABILITY AND AVAILABILITY

- APIs must be stateless so they can scale horizontally (session state in Redis/JWT).
- Customer booking flows are the availability priority (SRS §29.4).
- Static frontend assets should be served via CDN where possible.
- Traffic spikes during promotions must be considered in capacity planning (SRS §6.2).

## DEV-ONLY PROVIDER TEST LOGIN

QA/browser-automation tools that exercise provider-web's authenticated
screens (jobs, availability, earnings, profile) cannot complete a real OTP
login: some automation tools refuse to type OTP codes at all, treating it as
an auth-bypass action, and there is no way to read a generated OTP out of
`SandboxNotificationProvider` through the UI. To unblock that testing without
touching real OTP verification, provider-api exposes one dev-only endpoint
that mints a real session for the seeded `+919888888888` E2E Test Provider
(`database/seed/dev-provider-seed.sql`), skipping OTP entirely.

**NEVER enable this in Staging or Production.** It is gated so that doing so
is a structural impossibility, not a matter of convention:

1. **Route only exists in Development.** `POST /api/v1/auth/dev/login-as-provider`
   is registered inside `if (app.Environment.IsDevelopment())` in
   `backend/provider-api/ProviderApi/Program.cs` — in any other environment
   the route is never mapped, so it 404s. This is on top of, not instead of,
   the usual environment check.
2. **Second gate: a shared secret.** The caller must send the request header
   `X-Dev-Auth-Key` matching `DevAuth:Key` from configuration. That key is
   defined **only** in `appsettings.Development.json` — it does not exist in
   `appsettings.json` or `appsettings.Production.json`, so even a
   misconfigured deployment has nothing to match the header against.
3. **Additive, not a bypass branch.** The endpoint calls a new
   `ProviderLoginService.DevLoginAsync` method that reuses the same
   session-issuing code (`IssueSessionAsync`, token generation) as a real
   login. It does not modify `AuthController`'s `login/otp/verify` endpoint
   or `LoginWithOtpAsync` in any way.
4. **Loud, auditable logging.** Every call logs a structured warning
   (`"SECURITY: dev-only auth bypass used"`) so any accidental exposure
   would show up immediately in logs/alerts.

### Enabling it locally

Backend (`backend/provider-api/ProviderApi`), already set in
`appsettings.Development.json`:

```json
"DevAuth": { "Key": "dev-only-provider-auth-key-local-1234567890" }
```

Frontend (`frontend/provider-web`), in a gitignored `.env.local` (never
commit these):

```
NEXT_PUBLIC_ENABLE_DEV_AUTH=true
DEV_AUTH_KEY=dev-only-provider-auth-key-local-1234567890
```

`NEXT_PUBLIC_ENABLE_DEV_AUTH` controls whether the "Dev sign in (test
provider)" button renders on `/login` — it is unset by default, so the
button does not exist in a normal checkout. `DEV_AUTH_KEY` is deliberately
**not** `NEXT_PUBLIC_*`: it is read server-side only, inside the Next.js
route handler at `frontend/provider-web/src/app/api/dev-login/route.ts`,
which proxies to provider-api with the `X-Dev-Auth-Key` header attached. The
key never reaches the browser bundle.

With both set, clicking the button on `/login` signs in as the seeded E2E
Test Provider and lands on `/jobs` with a normal, fully-working session
(same access/refresh tokens a real OTP login would produce).

## WALLET TOP-UP AND RECURRING-PLAN SETTINGS

Everything below is configuration, not code: set it per environment (`Section__Key`
environment variables on Render, `appsettings.*.json` locally).

**Wallet top-ups** (`WalletTopUp:*`) - letting a customer add their own money to the
wallet through PayU. **Off by default and must stay off in production until the business
and legal groundwork for holding customers' money is done** (whether a closed wallet
needs regulatory approval, GST/accounting treatment, the terms shown to customers, the
payment provider's own agreement). Starting a top-up needs **both** `WalletTopUp:Enabled`
(deployment) and the admin setting *Settings -> Wallet -> Allow wallet top-up* (runtime
kill switch); the balance cap is the lower of `WalletTopUp:MaxWalletBalance` and the
admin's *Max wallet balance*.

| Key | Default | Meaning |
| --- | --- | --- |
| `WalletTopUp:Enabled` | `false` | Master switch. Off: the API refuses to start a top-up and the screens offer none |
| `WalletTopUp:MinAmount` / `MaxAmount` | `100` / `10000` | Rupees per top-up |
| `WalletTopUp:MaxWalletBalance` | `20000` | Most a wallet may hold, counting top-ups still in flight |
| `WalletTopUp:MaxTopUpsPerDay` | `10` | Per customer per 24h, whatever the outcome (blunts card testing) |
| `WalletTopUp:PendingReuseMinutes` | `15` | A same-amount pending top-up this recent is reused, not duplicated |
| `WalletTopUp:ReconcileAfterMinutes` / `ReconcileUpToDays` | `15` / `7` | When the sweep asks the gateway about a pending top-up, and when it gives up |
| `WalletTopUp:SuggestedAmounts` | `500,1000,2000,5000` | Quick-pick amounts on the screen |

**Recurring plans** (`RecurringBookings:*`, the section the daily job already used):

| Key | Default | Meaning |
| --- | --- | --- |
| `LeadTimeDays` | `3` | How far ahead the daily job creates a pay-as-you-go plan's visits |
| `PrepaidCycleDays` / `PrepaidRenewalLeadDays` / `MaxPrepaidVisits` | `30` / `5` / `60` | Prepaid plans: length of one paid cycle of an "until I cancel" plan, how early its renewal is created, and the most visits in one purchase |
| `PauseAfterUnpaidVisits` | `2` | Consecutive unpaid expired visits that pause a pay-as-you-go plan |
| `MaxSkipDays` / `MaxSkipRangesPerPlan` | `30` / `2` | "Skip visits until" limits |
| `WalletLowBalanceVisits` | `3` | Warn when a wallet-paid plan's balance covers fewer upcoming visits |
| `ProviderReservationHorizonDays` | `30` | Days ahead a plan's regular professional is kept free for the plan's visits; `0` = off |

The customer-web screens mirror a few of these as constants (skip limits, low-balance
and pause thresholds) purely so the UI can stop at the limit; the API is the authority.

**Background jobs.** One new recurring Hangfire job, registered by admin-api only (the sole
`BackgroundJobs:ServerEnabled=true` process): `wallet-top-up-reconciliation`, every 10
minutes. It is registered even with top-ups off - with no pending rows a pass is one empty
indexed query.

**admin-api needs the PayU settings too.** The reconciliation job, the admin's *Reconcile
now* on the Wallet top-ups screen, admin-initiated refunds (`RefundService`) and the
subscription billing job all talk to the payment gateway from **admin-api**. The gateway is
chosen by configuration (`PaymentGatewayRegistration`): with `PayU__MerchantKey`,
`PayU__MerchantSalt` and `PayU__CheckoutReturnBaseUrl` set it is real PayU, otherwise the
sandbox - whose verify always answers "pending" and whose refund always "succeeds" without
moving money. consumer-api has those variables on Render; **admin-api must carry the same
four** (`PayU__MerchantKey`, `PayU__MerchantSalt`, `PayU__CheckoutReturnBaseUrl`,
`PayU__UseProductionEnvironment`) or those four things silently run against the sandbox.
Check by key name in Render -> glavyx-admin-api -> Environment; never paste the values into
the repo or a chat.

**Payment return and webhook.** PayU's single webhook URL (`/payments/webhook/payu`) is
unchanged; `PaymentCallbackRouter` hands an order to the booking-payment handler first and
only an order no booking claims to the wallet top-up service. PayU returns the browser to
`/booking/payment/{id}/return` for a booking and `/wallet/topup/{id}/return` for a top-up.

**Migrations to apply (in order):** `20260930115211_AddPrepaidRecurringPlanAndPaymentGroup`,
`20261001095302_AddWalletTopUpAndPlanPauseFields`,
`20261001095326_SeedPlanPausedAndWalletLowBalanceNotificationTemplates` (inserts the SMS /
email / push templates for the `RecurringPlanPaused` and `WalletLowBalance` events - they
are stored by name, so deployments that already ran an older seed are unaffected), and
`20261001114813_SeedPlanChangedAndWalletShortfallNotificationTemplates` (the same for
`RecurringPlanChanged` - the confirmation of a customer's own pause / skip / time change / cancel -
and `RecurringWalletShortfall`). **Apply it before deploying the code that sends them**: without
the rows those notifications are recorded as `no_template` and the customer is told nothing.

**Cancellation numbers customers now see.** `CancellationPolicy:FreeCancellationWindowHours` (4)
and `LateCancellationFeePercentage` (20) are no longer only enforced: the cancel screen quotes
the cut-off time and the fee, and the plan card's pause / skip / cancel dialogs quote the same two
numbers. Changing them changes what customers are told, immediately and consistently.

**Where the cancellation and reschedule policy comes from.** Admin Settings -> Cancellation and
Settings -> Reschedule are now what the engines enforce (`IBookingPolicyProvider`, read on every
call - no cache, no restart). Until an admin has *saved* a group, the seeded row is ignored and the
configuration above (`CancellationPolicy:*`, `ReschedulePolicy:*`) stays in force, so shipping this
changes no live booking's rules; the first save in Settings switches that group over for every API
host. A stored value that cannot be read or fails the Settings page's own validation is logged
(`Settings group ... holds an invalid value`) and configuration is used instead. The "Allow admin
override" cancellation setting is stored but nothing enforces it yet.

**What every admin setting does.** One rule for all of them: a group nobody has *saved* in Settings changes
nothing, and saving a group saves **every** field on its card (the seeded values are not all neutral - a 30-day
booking horizon and a 2-hour lead time are stricter than anything enforced before - so the card's description
says what applies). Where a more specific setting exists (a city's slot booking policy, a city's pricing
policy) the platform rule sits underneath it and only ever tightens. Read through `IBookingPolicyProvider`
(cancellation, reschedule) and `IPlatformRules` (the rest), with the answer remembered for one request.

| Group | Applied once saved | Recorded, not applied - and why |
|---|---|---|
| Cancellation | free window, late fee % | "Allow admin override" - no admin waive action exists |
| Reschedule | blocking window, max count, late-fee window and %; "collect the late fee from the customer's wallet" (off by default - see below) | - |
| Booking | minimum lead time, booking horizon, same-day on/off (`SlotAvailabilityService`); active bookings per customer (`BookingService`; unpaid bookings, plan visits and AMC redemptions are not counted or capped) | - |
| Slot | same-day cutoff, booking horizon, allow overbooking (`SlotAvailabilityService`; an overbooked seat is still counted) | default duration, default capacity - slots are created one at a time with their own times and capacity |
| Tax | default tax % in a city with no pricing policy of its own (`PriceCalculationService`; shows within 45 s, the price cache TTL) | "prices include tax" - changes every total, commission and refund, and `docs/GST.md` says the tax posture needs a CA's sign-off first; registration number - there is no tax invoice |
| Wallet | share of one booking the wallet may pay (`BookingSummaryService`, rounded down to the paisa); add-money switch and balance cap (already, `WalletTopUpService`) | credit expiry - money added, refunds and manual adjustments never expire, coins and referral credits have their own programme expiry |
| Coupon | coupons on/off (refuses every code, `Coupon.Disabled`); different coupons held across live bookings (`Coupon.ActiveLimitReached`); maximum percentage when a coupon is created or its percentage raised (`Coupon.DiscountAboveLimit` - coupons above it keep working) | stacking - a booking takes one coupon |
| Feature flags | UI visibility only, by design (never gates the booking, payment or fulfilment flow); serviceability auto-management switch | - |

**Late-reschedule fee collection - a switch, off by default.** `ReschedulePolicy:CollectLateFeeFromWallet`
(`false` in `appsettings.json`, `true` only in the consumer-api's `appsettings.Development.json`) and, once an admin
has saved Settings -> Reschedule, that card's "Collect the late fee from the customer's wallet" toggle, which wins.
**Off:** a late reschedule is only *recorded* (the lateness and the fee under the policy are on the booking, as before
collection existed) - nothing is debited, no wallet is needed, nobody is turned away, and the customer screens say
"recorded on your booking". **On:** the fee is debited from the customer's wallet in the same step as the move and a
customer whose wallet cannot cover it cannot reschedule at that time. Turn it on only once customers can add money
(`WalletTopUp:Enabled`), otherwise a customer with an empty wallet can only cancel. A fee collected while it was on stays
credited against a later cancellation after it is turned off. A reschedule setting saved before the toggle existed reads
as off. (Note the Settings page shows the stored value, not a configuration override: if production is switched on by
configuration, the toggle will read off until an admin saves the card.)

**The admin follow-up release (wallet top-up list, plan controls).** One more EF migration,
`AddWalletTopUpReviewFlag` (two nullable columns on `wallet_top_up`; additive, so the previous
version keeps working while it is applied). No new configuration - but see *admin-api needs the PayU
settings too* above: *Reconcile now* only does anything real when admin-api has them.

**Rolling out the plans / wallet / reschedule release.** The release carries five EF migrations
(`20260930115211_AddPrepaidRecurringPlanAndPaymentGroup`, `20261001095302_AddWalletTopUpAndPlanPauseFields`,
`20261001095326_SeedPlanPausedAndWalletLowBalanceNotificationTemplates`,
`20261001114813_SeedPlanChangedAndWalletShortfallNotificationTemplates`, `20261001214823_AddRescheduleFeeCollection`).
All five are additive - new tables, new nullable / defaulted columns, new template rows - so they can be applied while the
previous version is still serving. **Apply them to the production database before the code deploys** (production's CD
applies migrations in parallel with Render's deploy, with no ordering guarantee; the new code reads the new columns and
the notification templates, and without the template rows plan confirmations are recorded as `no_template`). Use
`database/scripts/apply-migrations.sh "<connection string>"` against Neon, and make sure `Migrations__ApplyOnStartup` is
not set on the three Render services (three services racing the same pending migrations is a documented failure mode).
Then merge. Defaults on first deploy: late-fee collection **off**, every Settings group **unsaved** (so nothing the
engines enforce changes), wallet top-up as configured. Rolling the code back later is safe - the previous version ignores
the new columns and tables.

**Running two checkouts against one local database.** If a second checkout (for example
`nestly-monthly`) shares the local `nestly` database, give each its own ports and run only
one Hangfire server: start the other admin-api with `BackgroundJobs__ServerEnabled=false`
so it does not register jobs in, or compete on, the shared Hangfire storage.

## OPEN DECISIONS

Decided:

- CI platform: **GitHub Actions** (`.github/workflows/ci.yml` — backend build/test with disposable Postgres/Redis, frontend lint/build, Docker image builds)

To be finalized before production setup:

1. Cloud provider / hosting platform
2. Container registry
3. Orchestrator (managed containers vs Kubernetes)
4. Secret store implementation
5. Monitoring/alerting stack
6. CDN / media storage provider
