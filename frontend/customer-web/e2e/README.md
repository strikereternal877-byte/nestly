# E2E suite (tasks 140a-140d)

Real browser tests (Playwright/Chromium) driving customer-web against a real
consumer-api, admin-api, Postgres and Redis - no mocking. Covers the SRS 33
UAT flows: discovery -> category -> service detail (140a), slot selection ->
booking -> payment (140b), cancellation and reschedule (140c), refund and
review submission (140d).

## Prerequisites (start once, outside Playwright)

```bash
docker compose up -d postgres redis
bash database/scripts/apply-migrations.sh
docker exec -i nestly-postgres-1 psql -U nestly -d nestly < database/seed/dev-admin-seed.sql
docker exec -i nestly-postgres-1 psql -U nestly -d nestly < database/seed/dev-customer-seed.sql

ASPNETCORE_ENVIRONMENT=Development dotnet run --project backend/consumer-api/ConsumerApi --urls http://localhost:5257 &
ASPNETCORE_ENVIRONMENT=Development dotnet run --project backend/admin-api/AdminApi --urls http://localhost:5177 &

cd frontend/customer-web
cp .env.example .env.local   # NEXT_PUBLIC_API_URL=http://localhost:5257
npm run dev &                # http://localhost:3000
```

`ASPNETCORE_ENVIRONMENT=Development` matters: `appsettings.Development.json`
on both APIs carries the relaxed `RateLimiting` overrides this suite needs
(the production defaults - 5-20 requests per 15 minutes on login/payment -
exist to stop credential-stuffing and payment-callback abuse, and a real
browser suite creating several bookings per run legitimately exceeds them;
see task 134's `RateLimitOptions` doc comment) and the CORS origin
(`http://localhost:3000`) that lets a real browser call these APIs at all
(task 140a's E2E run surfaced there was no CORS policy anywhere until this
suite added one - see `AddNestlyCors` in
`backend/shared/Infrastructure/DependencyInjection.cs`).

## Run

```bash
npx playwright test                       # all specs, headless
npx playwright test 140a-discovery.spec.ts # one file
npx playwright test --headed --debug      # interactive
```

`playwright.config.ts`'s `globalSetup` (`e2e/setup/global-setup.ts`) seeds a
full geography -> category -> service -> serviceability -> slot-window chain
through real admin-api calls before any spec runs (`e2e/setup/seed-catalog.ts`)
and writes the resulting ids to `e2e/setup/fixture.json` (gitignored,
regenerated every run) for the specs to read via `loadFixture()`.

## Why direct-DB seeds exist at all

Two things have no API path and are bootstrapped via
`database/seed/dev-admin-seed.sql` / `dev-customer-seed.sql` instead (see
each file's header comment for the full reasoning):

- The first admin account: `AdminUser` is "provisioned by a Super Admin
  rather than self-registered" by design - there is no admin
  self-registration endpoint for a first admin to use.
- The test customer's login: OTP codes are generated for real and never
  exposed anywhere retrievable (`SandboxNotificationProvider` only logs a
  masked recipient) - login is exercised for real
  (`POST /auth/login/password`), only the OTP step is skipped for the one
  bootstrap account.

Everything else (categories, services, pricing, serviceability, slot
windows, the test customer's address) is created through the real admin-api
/ consumer-api, not inserted directly - this suite is testing those APIs
too, not just the UI on top of them.

## Known test-only shortcut

`e2e/setup/force-booking-completed.ts` forces a booking straight to
`BookingStatus.Completed` via direct SQL so the review spec (140d) doesn't
also have to stand up provider-api + provider KYC + assignment + job
completion (the only real path to `Completed`). See that file's doc comment.
