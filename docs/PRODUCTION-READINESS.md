# PRODUCTION-READINESS.md

What stands between `main` and a production deployment, with file:line evidence
for every claim.

## STATUS

Assessed **2026-08-20** against `main` at commit `7f07b4a`.

`tasks.csv` is exhausted — 701 rows, 651 `done` and 50 `decomposed`, nothing
open. This document exists because backlog completeness and production
readiness are different things, and with the backlog closed the second one is
now the only thing left to measure.

The findings below are filed as `tasks.csv` **#375–#391, "Phase 26 - Production
Readiness"**.

### Method

Unlike [LAUNCH-READINESS-AUDIT.md](LAUNCH-READINESS-AUDIT.md), which could not
build the solution, this pass **built and tested the working tree**:

| Check | Result |
|---|---|
| `dotnet build Nestly.sln` | **0 errors, 0 warnings** |
| `dotnet test Nestly.sln` | **2073 passed, 0 failed** |

Per-project: `Catalog.Tests` 1698 · `Identity.Tests` 355 ·
`CustomerManagement.Tests` 12 · `Performance.Tests` 8.

Everything else below is static evidence — the presence, absence and wiring of
code, configuration and workflows. Where a finding says a thing does not exist,
it was searched for across `backend/`, `frontend/`, `deploy/` and
`.github/workflows/` and was absent.

### What this document is not

It is not a QA report. Runtime behaviour is owned by
[QA-REPORT-2026-08-18.md](QA-REPORT-2026-08-18.md) and `tasks.csv` #374, whose
verdicts stand unchanged. Nothing here contradicts them: the product works, in
a development environment, against fakes. This document is about what happens
when the fakes are removed.

---

## 1. THE SHAPE OF THE PROBLEM

The platform is feature-complete and healthy. Three APIs (423 endpoints), three
Next.js apps, 93 domain entities, 76 migrations, 2073 green tests, and CI/CD
workflows for build, staging deploy, production deploy, rollback and database
backup.

What it does not have is **a production environment, a way to deploy its user
interface into one, or working integrations with the outside world.** The three
external dependencies a home-services marketplace cannot operate without —
taking money, sending an OTP, and being reachable over TLS — are respectively a
fake, a no-op, and absent.

The severity ordering that follows is deliberate. P0 items are not "important";
they are conditions under which the product cannot perform its core transaction
at all.

---

## 2. P0 — THE PRODUCT CANNOT TRANSACT OR AUTHENTICATE

### 2.1 The payment gateway now has a real implementation — it needs live merchant credentials and vendor onboarding to activate (was `#375`)

**Resolved as of this writing.** `backend/shared/Infrastructure/Services/PayUPaymentGateway.cs`
implements `IPaymentGateway` against real PayU Hosted Checkout: order
creation (the signed redirect form), refund (`cancel_refund_transaction` via
PayU's postservice API), and webhook signature verification (PayU's
documented SHA-512 hash formulas, verified against PayU's actual test
environment while this class was written — not just against documentation).
`PaymentGatewayRegistration.AddPaymentGateway` picks it over the sandbox the
same way every other real-vendor integration in this project is chosen —
see 2.2 below for the identical pattern:

```csharp
services.AddPaymentGateway(configuration);
```

**What is still needed before this is live:**
- A real PayU merchant account (`PayU:MerchantKey` / `PayU:MerchantSalt` in
  Production configuration) — commercial onboarding and KYC, lead time
  measured in weeks, same as before.
- `PayU:CheckoutReturnBaseUrl` pointed at the real customer-web origin.
- The customer-web checkout page itself: `IPaymentGateway.CreateOrderAsync`
  now returns a `CheckoutRedirectUrl` + `CheckoutFormFields` a browser must
  actually be POSTed to, plus `/checkout/payu/success` and
  `/checkout/payu/failure` return pages — none of that frontend work exists
  yet, only the backend contract it will consume.
- `SubscriptionBillingJob`'s and `RecurringOccurrenceAutoChargeJob`'s
  off-session/saved-card charging still only works against the sandbox:
  PayU Hosted Checkout requires a live customer browser session and cannot
  power a server-initiated recurring charge. That needs PayU's separate
  tokenized/saved-card product, a different integration from the one built
  here.

The interface boundary stayed clean through this change — no caller of
`IPaymentGateway` needed to change to add the real implementation, exactly as
this section previously predicted.

### 2.2 SMS and email are no-ops — so production login does not work (`#376`)

`backend/shared/Infrastructure/DependencyInjection.cs:842-845`:

```csharp
// Sandbox in every environment for now (SRS 30.2): no real SMS/email
// vendor is configured yet. Swap this registration, not the callers,
// when a production provider lands.
services.AddScoped<INotificationProvider, SandboxNotificationProvider>();
```

`SandboxNotificationProvider.SendSmsAsync`
(`shared/Infrastructure/Services/SandboxNotificationProvider.cs:25-33`) logs
`"Sandbox SMS simulated for {MaskedMobile}"` and returns `Result.Success()`.
Nothing is sent.

**This is the single most consequential finding in this document, and its
consequence is easy to miss.** OTP is the primary authentication path for both
customers and providers — `OtpService` and `ProviderOtpService` both take
`INotificationProvider` as a constructor dependency
(`OtpService.cs:23,26`; `ProviderOtpService.cs:25,28`). With the sandbox
registered, an OTP is generated, persisted, and never delivered, while the API
returns success. **On a production deployment as the code stands today, no
customer and no provider can log in, and the failure is silent** — no error, no
log line indicating a problem, just an OTP that never arrives.

Password login exists for customers (and, since `#372`, providers) and would
still work, but registration, password reset and every notification the product
sends are on the same dead channel.

**What is actually needed:** a real vendor implementing `INotificationProvider`
(the interface boundary is again clean), and — for Indian SMS — DLT template
registration with the telecom regulator for every transactional template the
platform sends. DLT approval is a multi-day process per template and is a
common cause of launch slippage. Templates are already inventoried by the
notification-template module, so the list is derivable rather than guesswork.

### 2.3 Admin MFA always succeeds (`#377`)

`backend/shared/Infrastructure/Services/NoOpAdminMfaChallengeProvider.cs:15-17`:

```csharp
public Task<Result> VerifyAsync(AdminUser adminUser, CancellationToken cancellationToken = default) =>
    Task.FromResult(Result.Success());
```

Registered unconditionally at `DependencyInjection.cs:582`.

This was a deliberate seam (task 95f) so that `AdminLoginService` could call an
MFA provider without every environment configuring one, and as a development
default it is correct. In production it means the admin panel — 289 endpoints
including refunds, payouts, pricing and customer data — is protected by a
password alone. SRS 12.x asks for MFA on this surface.

Note that this becomes tractable only after `#376`: an OTP-based MFA challenge
needs a working delivery channel.

### 2.4 There is no production environment, and no decision about where one goes (`#378`)

`docs/DEVOPS.md:196-203` — OPEN DECISIONS, *"to be finalized before production
setup"*:

1. Cloud provider / hosting platform
2. Container registry
3. Orchestrator (managed containers vs Kubernetes)
4. Secret store implementation
5. Monitoring/alerting stack
6. CDN / media storage provider

All six are still open. The CD workflows are written and look correct, but they
are inert until someone answers these: `cd-production.yml:26-36` documents its
own preconditions — a GitHub Environment named `production` with required
reviewers, five secrets, and a host with `/opt/nestly` provisioned and carrying
a `.env` of runtime secrets. None of that exists.

This is a decision task before it is an engineering task, and it blocks
`#379`–`#382`.

### 2.5 The three frontends cannot be deployed at all (`#379`)

No `Dockerfile` exists under `frontend/` — for `customer-web`, `admin-web` or
`provider-web`. The three backend APIs each have one
(`backend/{consumer,admin,provider}-api/*/Dockerfile`); the apps that users
actually touch have none.

Consequently `deploy/docker-compose.deploy.yml:45-80` defines exactly three
services — `consumer-api`, `admin-api`, `provider-api`. `cd-production.yml` and
`cd-staging.yml` build and push those same three images and nothing else.
`ci.yml`'s `docker` job builds two.

`docs/DEVOPS.md:19-25` lists Customer Web and Admin Web as deployable units, so
this is a gap against the platform's own specification rather than a deliberate
API-only posture. (That table also predates `provider-web` and `provider-api`
and should be corrected to five application units while this is fixed.)

None of the three `next.config.mjs` files sets `output: 'standalone'`, which is
the conventional starting point for containerizing a Next.js app without
shipping the full `node_modules` tree — worth setting as part of this work.

**As it stands, a successful production deploy would put three APIs on a server
with no user interface in front of them.**

### 2.6 No TLS, no reverse proxy, no front door (`#380`)

A search for `nginx|traefik|caddy|letsencrypt|certbot` across every `.yml`,
`.yaml`, `.conf` and `.md` in the repository returns **nothing**.

`deploy/docker-compose.deploy.yml` publishes each API directly on a host port
over plain HTTP — `5257:8080`, `5177:8080`, `5377:8080`.

Two things follow. First, there is no TLS anywhere in the deployment topology,
which is disqualifying on its own for a platform handling payments and personal
data. Second, `/metrics` is deliberately unauthenticated on each API —
`consumer-api/Program.cs:153-156` explains it is *"meant for an internal
scraper behind the network boundary rather than a public consumer."* That
reasoning is sound, but **the shipped compose file provides no such boundary**:
the ports are published to the host, so absent an external firewall the metrics
endpoint is reachable from the internet. The comment describes an
infrastructure assumption that the infrastructure does not yet make true.

Fixing `#380` is what makes the `/metrics` comment honest; until then the two
findings are one.

---

## 3. P1 — OPERATIONALLY UNSOUND

### 3.1 File storage silently falls back to a container-local disk (`#381`)

`shared/Infrastructure/Services/FileStorageRegistration.cs:51-67`: Supabase
Storage is used when credentials are configured; otherwise the resolver logs
*"File uploads will use local disk (App_Data/uploads)"* and returns
`LocalDiskFileStorageService`.

The fallback is fine for development and wrong for production in two distinct
ways: files are lost when a container is replaced, and with more than one
replica an upload written by one instance is invisible to the others. This path
carries booking completion-proof photos and CMS media — the former is evidence
in a customer dispute.

The fallback is a warning-level log, not a startup failure. Production should
fail fast instead: if the environment is Production and no object-storage
credentials are configured, refuse to start rather than quietly degrade to a
disk that will lose data.

### 3.2 Metrics are exported; nothing scrapes them and nothing pages (`#382`)

Tasks 137a–c delivered real instrumentation: `IMetricsService`, OpenTelemetry
with a Prometheus exporter (`DependencyInjection.cs:312-316`), a
`/metrics` scrape endpoint on each API, and `FailureRateAlertMonitor` with
alert codes for payment, booking and notification failure rates.

The code comment at `DependencyInjection.cs:305-311` states the limit plainly:
*"no OTel collector exists anywhere in this repo yet."* Nothing scrapes the
endpoint, nothing evaluates an alert rule, and nothing pages a human.
`RUNBOOK-DEPLOYMENT.md` §142d says as much — no paging destination has been
decided.

The instrumentation is the hard half and it is done. What remains is a
monitoring stack (open decision 5 from `#378`), scrape configuration, alert
rules over the existing alert codes, and a destination that reaches a person at
03:00.

### 3.3 Backups run; restore has never been rehearsed against production-shaped data (`#383`)

`.github/workflows/backup-postgres.yml` exists and
`RUNBOOK-BACKUP-RESTORE.md` documents the restore procedure. What is missing is
a rehearsal: a real restore from a real backup artifact into a scratch
database, timed, with the result recorded — which is the only thing that
converts a backup policy into a recovery guarantee. This also needs the backup
destination and retention to be settled, which depends on `#378`.

### 3.4 Secrets live in a `.env` file on the deploy host (`#384`)

Every service in `deploy/docker-compose.deploy.yml` reads `env_file: .env`, and
`cd-production.yml:36` describes provisioning that file by hand on the host.
Open decision 4 — secret store implementation — is still unanswered.

For staging this is proportionate. For a production system holding payment
credentials and personal data it is not: there is no rotation story, no audit
of access, and the secrets sit in plaintext on a VM. `appsettings.Production.json`
correctly contains only Serilog configuration and no secrets, so the
application side is already doing the right thing — the gap is entirely in
where the values come from.

---

## 4. P2 — QUALITY GATES THAT DO NOT GATE

### 4.1 Playwright E2E exists but CI never runs it (`#385`)

`frontend/customer-web/e2e` holds 5 specs and `frontend/admin-web/e2e` holds 4;
both apps expose `npm run test:e2e`. No workflow in `.github/workflows/`
invokes any of them. The only browser-based job in CI is
`lighthouse-mobile`, which measures performance budgets, not behaviour.

`frontend/provider-web` has **zero** E2E specs, despite owning the entire job
lifecycle (Accept → En Route → Arrived → In Progress → Completed) — the part of
the product that the 2026-08-18 sweep explicitly listed as unwalked past
"Assigned."

The infrastructure to fix this already exists and is proven: `ci.yml`'s
`lighthouse-mobile` job (lines 96-250) already stands up Postgres, Redis, both
APIs, applies migrations, seeds accounts and seeds a catalog fixture via
`e2e/setup/seed-catalog.ts`. An E2E job is that same setup with a different
final command.

### 4.2 The test suite is one large project wearing a module's name (`#386`)

Of 2073 tests, `Catalog.Tests` holds 1698. It is the platform's whole
regression suite — booking, payments, wallet, coupons, cancellation,
notifications and more — under a name that describes one module. A reader
asking "is booking tested?" looks for `Booking.Tests`, finds nothing, and
concludes wrongly. This is
[LAUNCH-READINESS-AUDIT.md](LAUNCH-READINESS-AUDIT.md) §6.3, still open.

`CustomerManagement.Tests` at 12 tests covers profile, communication
preferences and notes — thin enough to be worth re-verifying rather than
trusted (§6.4 of the same audit, also open).

### 4.3 `Performance.Tests` is 8 tests, not a load test (`#387`)

Tasks 135a–c ("catalog browse", "checkout", "concurrent slot booking under
promotion-level traffic", SRS 29.1-29.x) are closed, but the only artifact is
an 8-test project. Concurrent slot booking under contention is precisely the
scenario where this platform's correctness is most at risk and least
observable — an overbooked slot is a customer-facing failure that no unit test
shape will surface. This needs a real load harness against a running stack, and
a recorded baseline to regress against.

### 4.4 No automated security scanning anywhere in CI (`#388`)

Tasks 133a–g closed as manual OWASP review passes over injection, XSS, CSRF,
IDOR, broken access control, payment callback abuse and OTP brute force. Those
were real reviews and their findings are real. But nothing runs continuously:
no dependency vulnerability check (`dotnet list package --vulnerable`, `npm
audit`, or Dependabot), no SAST, no container image scan, and no external
penetration test.

A manual review is a snapshot of one commit. The repository has moved 100+
commits since and takes dependency updates from two ecosystems.

---

## 5. P2 — LAUNCH DATA AND UNEXERCISED SURFACE

### 5.1 A fresh production database is unbookable (`#389`)

[QA-REPORT-2026-08-18.md](QA-REPORT-2026-08-18.md) Phase 1 found **zero rows in
`service_pincode_mapping` and `slot_window_rule` for any seeded city** — no
customer could book any service, in any environment using that seed set. It was
fixed for the development database by adding a zone, a locality, slot windows
and pincode mappings by hand through the admin UI.

That fix was data, not code. It does not travel to a new deployment. A
production database restored from migrations plus the existing seed scripts
lands in the same unbookable state, and the platform gives no signal that it
has — every API returns correct, empty answers.

What is needed is either a production bootstrap dataset (real cities, pincodes,
zones, slot windows, catalog, pricing, tax) or, at minimum, a startup readiness
check that reports a database in which nothing can be booked. The
`AdminPermissionReconciler` added by `#332` is the right precedent: a
startup-time reconciliation rather than a frozen migration.

### 5.2 Modules with structural presence and no runtime evidence (`#390`)

Carried forward from the 2026-08-18 sweep's own known-gaps list and #374's,
both of which named these as untested rather than passing: chat live-thread
messaging over SignalR, support-ticket detail flow, review hide/flag
moderation, referral detail and fraud review, the provider job lifecycle past
"Assigned", AMC entitlement redemption end to end, and real OTP
delivery/verification (which is `#376`).

Each was blocked by thin seed data or missing infrastructure rather than by a
suspected defect. They should be walked once `#376` and `#389` remove those two
blockers, at which point the earlier sweeps' known-gaps lists close.

### 5.3 Commercial and compliance prerequisites (`#391`)

Not code, but on the critical path to a legal launch and with lead times that
do not compress: GST invoicing posture ([GST.md](GST.md)), liability and
insurance ([INSURANCE.md](INSURANCE.md)), published privacy policy and terms,
DLT registration for SMS (see `#376`), and payment-aggregator onboarding (see
`#375`).

Listed here so that the engineering plan does not implicitly assume they happen
for free in parallel.

---

## 6. NOT DEFECTS — CHECKED AND CLEARED

Recorded so that a later pass does not re-raise them:

- **The provider dev-login bypass is correctly gated.** `provider-api/Program.cs:82`
  maps the route only under `IsDevelopment()`, and line 149 asserts the negative
  case. It cannot exist in a production process.
- **`appsettings.Production.json` contains no secrets** — Serilog configuration
  only, for both APIs. Configuration comes from the environment, which is the
  right shape.
- **Firebase push and Google Maps routing degrade deliberately, not accidentally.**
  Both resolve to a real provider when credentials are configured and a sandbox
  otherwise (`PushNotificationRegistration.cs:36-92`,
  `RouteEstimateRegistration.cs:57-76`), logging which branch was taken. Unlike
  §2.1/§2.2 the real implementation exists — these need configuration, not code.
- **Rate limiting is wired on all three APIs** — `AddRateLimiter` plus
  `UseRateLimiter()` in each host: consumer-api `Program.cs:50,133`, admin-api
  `:54,118`, provider-api `:53,177`.
- **Only four TODO comments exist in non-test backend code**, all four
  cross-references between the support-ticket and booking-management modules,
  none of them unfinished work.
- **The MediatR `ICommand`/`IQuery` pipeline has no implementations** — noted in
  ORIENTATION.md. It is unused scaffolding, not a production risk. Left alone.

---

## 7. CRITICAL PATH

Ordered by what blocks what, not by severity alone.

**Stage 1 — unblock the transaction.** `#375` (payment gateway) and `#376`
(SMS/email) in parallel, with vendor onboarding and DLT registration started on
day one because they gate the code, not the other way round. Nothing downstream
is meaningful until these land: without `#376` nobody can log in, and without
`#375` nobody can pay.

**Stage 2 — decide and build the environment.** `#378` (the six open decisions)
is a decision task and should be resolved while Stage 1 is in flight, because
`#379` (frontend containers), `#380` (TLS/reverse proxy), `#381` (object
storage), `#382` (monitoring), `#383` (backup target) and `#384` (secret store)
all wait on its answers. `#379` and `#380` together are the difference between
a deploy that serves a product and one that serves three JSON APIs.

**Stage 3 — make a fresh deployment usable.** `#389` (bootstrap data), then
`#377` (admin MFA, which needs `#376`'s delivery channel).

**Stage 4 — close the loop on quality.** `#385` (E2E in CI), `#388` (scanning),
`#387` (load test), `#390` (walk the unexercised modules, now unblocked by
`#376` and `#389`), `#386` (split the test project).

`#391` runs alongside all four stages and should be owned by someone who is not
writing code.

Stages 1 and 2 are the honest answer to *"what is between here and a soft
launch."* Stage 3 is what stands between a soft launch and a working one.
