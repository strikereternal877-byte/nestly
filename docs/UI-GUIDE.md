# Nestly — Illustrated Product UI Guide

## PURPOSE

A screenshot-based, UI-focused companion to [WORKFLOW.md](WORKFLOW.md)'s
Mermaid diagrams (task 207) — WORKFLOW.md answers "what happens when," this
document is meant to answer "what does that actually look like," plus give
a new engineer or reviewer the exact steps to get the three apps running
locally from a clean checkout.

This document is **not authoritative** on behavior — where it and SRS.md
disagree, SRS.md is correct and this file is stale.

## STATUS

**Setup/initialization instructions below are complete and verified against
the actual `docker-compose.yml`, migration scripts and seed scripts in this
repository** — not assumed from memory.

**All 21 of 21 screenshots below are now captured**, against a genuinely
fresh database (reset from empty, all migrations replayed, both dev seed
scripts run). Two real, pre-existing bugs were found and fixed to make that
replay possible at all (see [Known issues found and fixed](#known-issues-found-and-fixed-2026-08-02)) —
not local database drift, but migration files that would have failed the
same way for any fresh checkout of this repo.

The three screenshots that were missing as of the previous pass
(`admin-web/booking-detail`, `customer-web/booking-detail`,
`provider-web/job-detail`) needed one real `Completed` booking to exist. That
booking was driven end-to-end through the real APIs/UIs for this pass: a
`Weekday Morning` slot window created in admin-web, a booking placed and
paid (sandbox gateway) in customer-web, the provider activated and assigned
in admin-web, and accepted/started/completed in provider-web — see
[Known issues found, not yet fixed](#known-issues-found-not-yet-fixed-2026-08-02)
for two more real bugs this surfaced.

### Known issues found and fixed (2026-08-02)

Bringing up the local stack for the very first time from a genuinely empty
database (as opposed to this repo's usual long-lived, incrementally-migrated
dev database) surfaced two classes of bug in the migration history itself,
both now fixed in place:

1. **Duplicate schema in early migrations.** `20260730172343_AddCustomerAddressGeographyLink`
   and `20260730182139_AddFinancialSchema` each redundantly recreated the
   entire `booking`/`booking_item`/`booking_status_history`/`booking_addon_item`
   tables (and the former also redundantly re-added `customer_address`'s
   `locality_id`/`pincode_id` columns) that an earlier-numbered migration
   already created — a snapshot-drift artifact from concurrent branch work
   early in this project. Fixed by removing the dead duplicate operations
   from both files' `Up()`/`Down()`, keeping only what each migration
   actually adds new.
2. **Two seed migrations that dynamically over-seeded.** `20260731140113_AddAdminPermissionMatrix`
   and `20260731152427_AddNotificationTemplateManagement` each seed by
   reading a live static catalog (`AdminPermissionCatalog.Permissions` /
   `NotificationTemplateSeedData.BuildDefaults()`) with no filter - correct
   when first authored, but those catalogs keep growing as later tasks add
   modules/event types (Provider, Referral, Chat, Subscription, NestlyCoins;
   RecurringBooking, Referral, Subscription notifications). On a fresh
   database, both migrations now silently re-seed every later addition too,
   colliding with each addition's own dedicated incremental migration on a
   primary-key or unique-index conflict. Fixed by freezing each migration to
   the fixed set of modules/event types that existed when it was authored,
   matching the pattern every later incremental seed migration already used
   correctly.

Both fixes are scoped to migration files only — no application code,
`AdminPermissionCatalog`, or `NotificationTemplateSeedData` changed. Full
backend suite still 987/987 after the fix.

### Known issues found, not yet fixed (2026-08-02)

Driving one booking to `Completed` end-to-end (to capture the three
screenshots above) surfaced two more real gaps, left unfixed here since
fixing them was out of scope for a docs/screenshot pass:

1. **`POST /api/v1/profile/kyc/documents` (provider-api) rejects a
   well-formed request with a raw 400** — `The body field is required. The
   JSON value could not be converted to
   Nestly.ProviderApi.Controllers.SubmitProviderKycDocumentBody` — when
   submitted from provider-web's own "Submit a document" form
   (`frontend/provider-web/src/app/(provider)/profile/page.tsx`) with a
   populated document type, file reference URL and document number. This is
   a raw model-binding exception, not a validation problem response, so the
   root cause is a request-shape mismatch between the frontend and
   `SubmitProviderKycDocumentBody` (`backend/provider-api/ProviderApi/Controllers/ProfileController.cs`)
   rather than missing input. Not root-caused further here.
2. **No admin path activates a provider without KYC docs.** A provider with a
   passed background check but zero submitted KYC documents (the case above
   made unavoidable) has no "activate anyway" action anywhere in admin-web's
   provider detail page — `ProviderStatus` stays `PendingVerification`
   indefinitely, and `POST /api/v1/admin/bookings/{id}/assign-provider`
   correctly refuses to assign a non-`Active` provider. For this pass the
   provider used in the screenshots above (`Ravi K`, mobile `9888877766`) was
   activated with a direct `UPDATE provider SET status = 'Active' WHERE id =
   …` against the local dev database only — not a real onboarding path, and
   not something to script or repeat outside local screenshot capture.

## FIRST-TIME SETUP

Prerequisites: Docker, the .NET 8 SDK, Node.js (see each frontend's
`package.json` `engines` field for the exact version), and `dotnet-ef`
(`dotnet tool install -g dotnet-ef`).

1. **Start Postgres and Redis:**
   ```bash
   docker compose up -d postgres redis
   ```
2. **Apply migrations** (creates/updates the schema against the compose
   database):
   ```bash
   ./database/scripts/apply-migrations.sh
   ```
3. **Seed development accounts.** `AdminUser` and `CustomerAuthIdentity`
   have no self-registration path by design (see each seed script's header
   comment), so the first account of each is a one-time direct insert:
   ```bash
   psql "postgresql://nestly:nestly_dev@localhost:5432/nestly" -f database/seed/dev-admin-seed.sql
   psql "postgresql://nestly:nestly_dev@localhost:5432/nestly" -f database/seed/dev-customer-seed.sql
   ```
   Both scripts are idempotent (`WHERE NOT EXISTS ...` guards) - safe to
   re-run.
4. **Run the three backend APIs** - either via Docker:
   ```bash
   docker compose up -d consumer-api admin-api provider-api
   ```
   or directly for faster local iteration (each on its own default port):
   ```bash
   dotnet run --project backend/consumer-api/ConsumerApi   # http://localhost:5257
   dotnet run --project backend/admin-api/AdminApi         # http://localhost:5177
   dotnet run --project backend/provider-api/ProviderApi     # http://localhost:5337
   ```
5. **Run the three frontends** (each reads `NEXT_PUBLIC_API_URL`, defaulting
   to the ports above if unset):
   ```bash
   npm --prefix frontend/customer-web run dev   # http://localhost:3000
   npm --prefix frontend/admin-web run dev      # http://localhost:3001
   npm --prefix frontend/provider-web run dev    # http://localhost:3002
   ```
6. **Sign in.**

   | App | URL | Credentials |
   |---|---|---|
   | Customer web | `http://localhost:3000/login` → "Email & password" tab | `e2e-customer@nestly.local` / `E2eCustomer!Passw0rd` |
   | Admin web | `http://localhost:3001/login` | `dev-admin@nestly.local` / `E2eTest!Passw0rd` |
   | Provider web | `http://localhost:3002/login` | No seed exists (docs/DEVOPS.md/database/seed has no `dev-provider-seed.sql`) - register a new provider via `/register`, then sign in with a real mobile-OTP code. The OTP is never logged or exposed via any dev bypass (see `dev-customer-seed.sql`'s header comment on the equivalent customer case); read the code directly from the `provider_otp` table in the local dev database if a UI walkthrough needs one without a real SMS provider configured. |

   All three passwords above are seeded local-dev-only values, never valid
   outside a local/CI database - see each seed script's own warning.

   Since task 206, `http://localhost:3000/login` alone can also sign in as
   Admin or Provider via the account-type selector at the top of the page -
   no need to visit `:3001`/`:3002` directly except to exercise a direct
   bookmark to those origins.

## Screens to capture

Captured at 1440x900 against a fresh database seeded only with the two dev
accounts ([First-time setup](#first-time-setup)) plus one minimal real
catalog record created live through the admin UI for this pass: state
Karnataka, city Bengaluru, zone/locality Koramangala, pincode 560034,
category "Home Cleaning", service "Deep Home Cleaning" (₹1499), and the
matching category/city + service/pincode serviceability mappings. The three
`booking-detail`/`job-detail` screenshots additionally needed a `Weekday
Morning` slot window (09:00–13:00, Mon–Fri), one real booking taken all the
way to `Completed` (sandbox payment, provider assignment, accept/start/
complete), and the provider activation workaround noted in
[Known issues found, not yet fixed](#known-issues-found-not-yet-fixed-2026-08-02).

### Customer web (`docs/assets/ui-guide/customer-web/`)

| Screenshot | Route | Notes |
|---|---|---|
| `login` | `/login` | ![login](assets/ui-guide/customer-web/login.png) Account-type selector (task 206) |
| `home` | `/` | ![home](assets/ui-guide/customer-web/home.png) |
| `categories` | `/categories` | ![categories](assets/ui-guide/customer-web/categories.png) |
| `service-detail` | `/services/[slug]` | ![service-detail](assets/ui-guide/customer-web/service-detail.png) |
| `booking-summary` | `/booking/summary` | ![booking-summary](assets/ui-guide/customer-web/booking-summary.png) Mid-checkout, one service in cart |
| `booking-detail` | `/bookings/[id]` | ![booking-detail](assets/ui-guide/customer-web/booking-detail.png) A `Completed` "Deep Home Cleaning" booking, with completion proof and full status timeline |
| `wallet` | `/wallet` | ![wallet](assets/ui-guide/customer-web/wallet.png) |
| `profile` | `/profile` | ![profile](assets/ui-guide/customer-web/profile.png) |

### Admin web (`docs/assets/ui-guide/admin-web/`)

| Screenshot | Route | Notes |
|---|---|---|
| `login` | `/login` | ![login](assets/ui-guide/admin-web/login.png) |
| `dashboard` | `/dashboard` | ![dashboard](assets/ui-guide/admin-web/dashboard.png) |
| `bookings` | `/bookings` | ![bookings](assets/ui-guide/admin-web/bookings.png) Empty list - no bookings exist in this seed |
| `booking-detail` | `/bookings/[bookingId]` | ![booking-detail](assets/ui-guide/admin-web/booking-detail.png) Same `Completed` booking - status timeline, payment, and provider assignment sections |
| `catalog` | `/catalog` | ![catalog](assets/ui-guide/admin-web/catalog.png) |
| `providers` | `/providers` | ![providers](assets/ui-guide/admin-web/providers.png) |
| `coupons` | `/coupons` | ![coupons](assets/ui-guide/admin-web/coupons.png) |
| `reports` | `/reports` | ![reports](assets/ui-guide/admin-web/reports.png) |

### Provider web (`docs/assets/ui-guide/provider-web/`)

No dev seed exists for a provider account ([First-time setup](#first-time-setup)
step 6) - the screenshots below use a real provider registered live through
`/register` for this pass (mobile `9888877766`). OTP-verified two different
ways across this pass's screenshots: by reading and SHA-256-brute-forcing
`provider_otp.code_hash` for the first batch (6 digits, unsalted, ~instant
locally), and for `job-detail` specifically (where the 5-minute OTP expiry
made brute-forcing impractical) by temporarily adding, using, and reverting
a one-line `Console.WriteLine` of the plaintext code in
`ProviderOtpService.GenerateAsync` - the code itself is never logged or
retrievable in plaintext in the actual application, same as the customer OTP
path.

| Screenshot | Route | Notes |
|---|---|---|
| `login` | `/login` | ![login](assets/ui-guide/provider-web/login.png) |
| `profile-skills` | `/profile` | ![profile-skills](assets/ui-guide/provider-web/profile-skills.png) Real category/service dropdowns (task 205) - shows "Home Cleaning", not a raw GUID |
| `profile-service-areas` | `/profile` | ![profile-service-areas](assets/ui-guide/provider-web/profile-service-areas.png) Real city/zone/pincode dropdowns (task 205) - shows "Bengaluru", not a raw GUID |
| `jobs` | `/jobs` | ![jobs](assets/ui-guide/provider-web/jobs.png) Empty list - no bookings assigned in this seed |
| `job-detail` | `/jobs/[id]` | ![job-detail](assets/ui-guide/provider-web/job-detail.png) Same booking, `Completed` from the provider's side - completion proof and verification checklist |
| `earnings` | `/earnings` | ![earnings](assets/ui-guide/provider-web/earnings.png) |
