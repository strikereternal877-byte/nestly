# ORIENTATION.md

**Start here.** This document explains what Nestly is, how the pieces fit
together, what actually exists today versus what is still planned, and the
non-obvious rules that will bite you if you do not know them.

The rest of the documentation suite describes how things *should* be built
(see [README.md](README.md) for the index and topic ownership). This document
is the only one that describes **the current state of the repository** — so
treat the others as the specification and this one as the map.

Last verified: 2026-08-01. Phase numbering changed since the previous
verification: Provider moved from Phase 8 (deferred, after everything) to
Phase 7 (before Hardening & Launch, now Phase 8) — see PROVIDER.md's STATUS
section. Two new phases were added: 9 (Referral & Growth) and 10 (Product
Enhancements — subscriptions, recurring bookings, in-app chat, service
completion verification). See TASKS-SUMMARY.md for authoritative current
counts; the table below is a snapshot and will drift.

---

## 1. WHAT NESTLY IS

An enterprise home-services marketplace. Customers discover, book, pay for and
review at-home services; internal teams run the full commerce lifecycle through
a separate admin panel.

It is a **modular monolith** — one deployable per API surface, with strict
internal module boundaries so business modules can later be extracted into
services without a rewrite. It is not microservices, and should not be built
as if it were.

Business context and module inventory: [PROJECT.md](PROJECT.md).
Full requirements: [SRS.md](SRS.md).

---

## 2. WHERE THE PROJECT ACTUALLY STANDS

This is the section most likely to be out of date, and the most important to
keep honest.

**Phase 0 (Foundation) is complete — 25/25. Phase 1 (Identity & Customer) is
complete — 46/46**, merged to `main`. Overall backlog: **73 of 221 tasks
done** (the backlog grew from 196 to 221 rows as later phases were decomposed
into subtasks — that is expected, not lost work). The active phase is
**Phase 2 — Catalog & Serviceability (2/26)**.

### What genuinely exists and is verified

| Area | State |
|---|---|
| Solution & layering | 7 projects (adds `Identity.Tests`), dependencies flow inward, builds clean |
| BuildingBlocks | `Result`/`Error` primitives, `Entity`/`AggregateRoot`/`ValueObject`, correlation-id and global-exception middleware |
| Persistence | EF Core + PostgreSQL, snake_case naming, configuration-by-assembly-scan, 8 migrations applied against a real database |
| Domain entities | Customer, CustomerAuthIdentity, CustomerSession, CustomerOtp, CustomerAddress, LoginAttempt, CustomerCommunicationPreference, Category, Service, ServiceAddOn, ServiceFaq, ServiceMedia, SupportTicketComment, AuditLog |
| Identity & auth | Mobile+OTP and email+password registration/login, JWT access+refresh with rotation, login throttling/lockout, forgot/reset password (verified against the account's mobile, not the unverified email) |
| Profile & addresses | View/edit profile, re-verified mobile/email change, communication preferences, full address-book CRUD with a partial-unique-index-enforced single default |
| Consumer-web screens | Login (OTP + password), registration, forgot/reset password, profile, address book — real product screens, not scaffolds |
| Tests | `Identity.Tests`: 69 tests (unit + SQLite-backed integration) covering OTP lifecycle, login lockout, uniqueness constraints, password reset, profile service |
| Caching | `ICacheService` over Redis, with in-process fallback — wired, no consumer yet (first real cache use case lands with Phase 2 catalog) |
| Background jobs | Hangfire on PostgreSQL, admin-only dashboard — wired, no consumer yet |
| Audit trail | `audit_log` table + `IAuditLogWriter` |
| Health checks | `/health/live`, `/health/ready` (Postgres + Redis) |
| Observability | Serilog structured logging, correlation ids |
| DevOps | Dockerfiles for both APIs, docker-compose (Postgres + Redis + both APIs), GitHub Actions CI |
| Admin frontend | `admin-web` scaffolded only — no product screens yet (Phase 6) |

### What does **not** exist yet

Be blunt about this, because the layering makes it easy to assume otherwise:

- **No catalog, serviceability, booking, payments, slots, coupons, post-booking
  or admin panel.** Phases 2–7. Some early drafts sit in `_salvage/` (see §7).
- **MediatR (`ICommand`/`IQuery`/handlers) and `AggregateRoot`/domain events
  are wired but have zero real callers** — every Phase 1 controller calls its
  service directly rather than going through `ISender`, and every entity so
  far derives from plain `Entity<Guid>`, not `AggregateRoot`. Flagged by a
  ponytail-audit pass on 2026-07-28 and left in place rather than removed,
  since both are documented architecture here — but do not assume a command/
  query or a domain event actually fires anywhere yet; grep before relying on
  either.

---

## 3. ARCHITECTURE IN ONE PASS

Clean Architecture. **Dependencies point inward, always.**

```
        ConsumerApi        AdminApi          <- hosts, composition roots
             \               /
              \             /
               Infrastructure                <- EF Core, Redis, Hangfire, HTTP
                     |
                Application                  <- use cases, abstractions, MediatR
                     |
                  Domain                     <- entities, business rules
                     |
              BuildingBlocks                 <- primitives shared by all layers
```

- **Domain** — entities and invariants. No framework dependencies.
- **Application** — orchestration, plus the *abstractions* Infrastructure
  implements (`ICacheService`, `IAuditLogWriter`, `IAuditContextProvider`).
  Framework-free.
- **Infrastructure** — the outermost layer and the only one that knows about
  EF Core, Redis, Hangfire and ASP.NET Core. It has a
  `FrameworkReference` to `Microsoft.AspNetCore.App` deliberately.
- **APIs** — thin. `Program.cs` composes layers and configures the pipeline.

The rule that keeps this honest: **when Infrastructure needs to hand something
to business code, define the interface in Application and implement it in
Infrastructure.** Never let Application reference a framework type.

Detail: [ARCHITECTURE.md](ARCHITECTURE.md).

---

## 4. REPOSITORY MAP

```
backend/
  consumer-api/ConsumerApi/     Customer-facing API. Enqueues jobs, runs none.
  admin-api/AdminApi/           Admin API. Runs the Hangfire server + dashboard.
  shared/
    Domain/                     Entities and business rules
    Application/                Use cases + abstractions Infrastructure implements
    Infrastructure/             Persistence, caching, jobs, auditing
    BuildingBlocks/             Result/Error, entity primitives, middleware
database/
  migrations/                   EF Core migrations AND the model snapshot (see §5)
  scripts/  seed/               Operational SQL, idempotent seed data
frontend/
  customer-web/  admin-web/     Next.js scaffolds
docs/                           Documentation suite — README.md is the index
_salvage/                       Quarantined drafts, NOT compiled (see §7)
tasks.csv                       The backlog: 196 phased tasks
```

---

## 5. NON-OBVIOUS RULES

These are the things that have already cost real debugging time. Read them
before touching the relevant area.

### Migrations live outside the project that compiles them

`database/migrations/` sits outside `Infrastructure/`, so the SDK's implicit
glob does not pick it up. `Infrastructure.csproj` includes it explicitly:

```xml
<Compile Include="..\..\..\database\migrations\**\*.cs" LinkBase="Migrations" />
```

**Why this matters:** without that line, `dotnet build` still succeeds — the
migration files simply are not part of any project — while
`dotnet ef migrations list` reports *no migrations*. A green build is not
evidence that migrations exist.

Also: `dotnet ef migrations add` writes the migration to `-o` but writes
`NestlyDbContextModelSnapshot.cs` to the project's **default** `Migrations/`
folder. Both then compile and the build breaks on duplicate types. After adding
a migration, move the snapshot into `database/migrations/` and delete the
generated folder.

### Entity configuration is discovered, never registered

`NestlyDbContext.OnModelCreating` calls `ApplyConfigurationsFromAssembly`. Add
an `IEntityTypeConfiguration<T>` under
`Infrastructure/Persistence/Configurations/` and it is picked up. **Never add
manual `modelBuilder.Entity<T>()` registrations** — they duplicate the scan.

### The database is snake_case

`UseSnakeCaseNamingConvention()` maps `OccurredOnUtc` → `occurred_on_utc`. Any
raw SQL — index filters, check constraints, `HasFilter(...)` — must be written
in **snake_case**, because it bypasses the convention. This class of bug
survives `dotnet build` and only appears when the migration hits Postgres.

### Two different things are called "auditing"

- **Column stamping** — `IAuditable` + `AuditableEntityInterceptor` fill
  `CreatedOnUtc`/`ModifiedOnUtc`. Records *when a row changed*.
- **The audit trail** — the `audit_log` table records *who did what to which
  entity, from where*.

`IAuditLogWriter` **enlists in the caller's unit of work and does not save** —
your `SaveChangesAsync` commits the audit row in the same transaction as the
change it describes. A rolled-back operation must not leave a phantom entry.

### The cache is advisory

`ICacheService` never throws for transport failures — an unreachable Redis
degrades to the source of truth and logs a warning. Build keys through
`CacheKeys`; inlining key strings is how the writer and the invalidator drift
apart. `GetOrCreateAsync` is cache-aside, **not a lock**: concurrent misses may
each run the factory, so never give it a factory with side effects.

### Background jobs are split across processes

The admin API runs the Hangfire server and the dashboard; the consumer API only
enqueues; tests do neither (`BackgroundJobs:ServerEnabled`). Retries re-run the
whole method, so **every job must be idempotent** and must honour its
`CancellationToken`.

### Security rules that are absolute

Never log passwords, OTPs, tokens or PII. Always hash before storing (OTP codes
are SHA-256 hashed, never plaintext). Never hardcode credentials. See
[SECURITY.md](SECURITY.md).

---

## 6. RUNNING AND VERIFYING

```bash
docker compose up -d postgres redis
```

```bash
dotnet build Nestly.sln
```

```bash
dotnet ef database update --project backend/shared/Infrastructure --startup-project backend/consumer-api/ConsumerApi
```

Health checks once an API is running: `/health/live` (process up) and
`/health/ready` (Postgres and Redis reachable).

**A passing build is not proof of work.** This repository has been burned by
that assumption repeatedly (§7). For anything touching persistence, caching or
jobs, verify against real infrastructure: apply migrations to a throwaway
Postgres and inspect the result, exercise the code through the real DI
container, and confirm the dependency actually served the traffic.

---

## 7. HISTORY YOU NEED IN ORDER TO TRUST THE REPO

Large parts of this backlog were attempted by automated local-model workers.
That history left artefacts you will encounter:

- **~60 tasks were once marked done that were never implemented.** An early
  worker used `npm run build` as its verification command — in a .NET repo with
  no root `package.json`. It could never pass, so verification silently
  degraded to "the model says it is done." Those tasks were audited and reset;
  their `tasks.csv` notes still record the audit findings.
- **Fabricated code has appeared more than once** — SQL Server APIs in this
  PostgreSQL project, invented method names, namespaces derived from folder
  paths instead of `RootNamespace`. It was discarded, not patched.
- **`_salvage/`** holds drafts rescued from those runs (booking, slots, auth
  and notification sketches). It is **deliberately outside every `.csproj`** and
  does not compile. Treat it as reference material, not as working code, and
  re-verify anything you promote out of it.
- **`tasks-corrupted.csv`** is a retained artefact of a CSV corruption incident.
  The live backlog is `tasks.csv`.

The practical rule: **`tasks.csv` status is a claim; the code is the evidence.**
A task note saying "verified by claude-code" records what was actually checked
and how — prefer those. When in doubt, grep for the thing before assuming it
exists.

---

## 8. THE ROADMAP

`tasks.csv` carries a `phase` column; work proceeds phase by phase.

| Phase | Scope | Done |
|---|---|---|
| 0 | Foundation — solution, persistence, caching, jobs, audit, DevOps | 25/25 |
| 1 | Identity & Customer — registration, JWT, profile, addresses, tests | 46/46 |
| 2 | Catalog & Serviceability | 67/72 |
| 3 | Booking Core | 47/56 |
| 4 | Payments & Financial | 40/46 |
| 5 | Post-Booking — reviews, support, notifications | 40/47 |
| 6 | Admin Panel | 104/119 |
| 7 | Provider — service-provider identity, onboarding, assignment, earnings (PROVIDER.md) | 21/25 |
| 8 | Hardening & Launch | 22/38 |
| 9 | Referral & Growth — refer-and-earn, milestones, expiring wallet credit (REFERRAL.md) | 0/16 |
| 10 | Product Enhancements — subscriptions, recurring bookings, in-app chat, completion verification (PRODUCT-ENHANCEMENTS.md) | 0/22 |

Per-phase task counts grew as several tasks were decomposed into subtasks by
an automated worker (e.g. `#35` → `#35a`..`#35d` → `#35ba`..`#35bx4`); the
done/total ratio for a phase is only meaningful relative to its *current*
total, not the number originally planned. **Provider was moved from Phase 8
to Phase 7 on 2026-07-31** — it now runs before Hardening & Launch, not after
everything else — see PROVIDER.md's STATUS section for why.

**Phase 8 (Hardening & Launch) and Phase 7 (Provider) are both active**, with
Phases 0–6 fully or substantially complete. There is an authenticated
principal throughout the system, RBAC is in place for the admin panel
(Phase 6), and the payment/wallet/coupon infrastructure Phases 9 and 10
depend on (referral rewards, subscription billing) already exists and is
largely done.

---

## 9. WHERE TO GO NEXT

| You want to know about | Read |
|---|---|
| Which document owns a topic | [README.md](README.md) |
| Business domain and modules | [PROJECT.md](PROJECT.md) |
| Full requirements | [SRS.md](SRS.md) |
| Layer boundaries | [ARCHITECTURE.md](ARCHITECTURE.md) |
| .NET / ASP.NET conventions, caching and jobs usage | [DOTNET.md](DOTNET.md) |
| Schema, EF Core, indexing, auditing | [DATABASE.md](DATABASE.md) |
| REST conventions and versioning | [API.md](API.md) |
| Auth, secrets, security rules | [SECURITY.md](SECURITY.md) |
| Test strategy | [TESTING.md](TESTING.md) |
| Docker, CI/CD, operations | [DEVOPS.md](DEVOPS.md) |
| How AI agents must behave here | [../.claude/CLAUDE.md](../.claude/CLAUDE.md) |
