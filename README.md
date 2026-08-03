# Nestly

Enterprise-grade home services marketplace platform — customers discover, book, pay for, and review at-home services; internal teams manage the full service commerce lifecycle through a dedicated admin panel.

Built as a **modular monolith** on Clean Architecture and DDD principles, designed so business modules can be extracted into microservices later without a rewrite.

> **New to this repository?** Read [docs/ORIENTATION.md](docs/ORIENTATION.md) first — it covers what actually exists today versus what is still planned, and the non-obvious conventions that will otherwise cost you time.

## Technology Stack

| Layer | Technology |
|---|---|
| Backend | .NET 8, ASP.NET Core Web API, Entity Framework Core |
| Database | PostgreSQL |
| Cache / Jobs | Redis, Hangfire |
| Frontend | Next.js, React, TypeScript |
| Infrastructure | Docker, REST, JWT authentication |

## Repository Structure

```
backend/
  consumer-api/       Customer-facing REST API (ConsumerApi)
  admin-api/          Admin/backoffice REST API (AdminApi)
  shared/             Shared class libraries
    Domain/           Business entities and rules (innermost layer)
    Application/      Use cases and orchestration
    Infrastructure/   Persistence, external services
    BuildingBlocks/   Cross-cutting primitives (results, errors, validation)
frontend/
  customer-web/       Customer web app (Next.js) — scaffold pending
  admin-web/          Admin panel web app (Next.js) — scaffold pending
database/
  migrations/         EF Core migration artifacts
  scripts/            Operational SQL scripts
  seed/               Idempotent seed data
docs/                 Project documentation suite (see docs/README.md)
.claude/              AI collaboration rules (CLAUDE.md)
```

## Getting Started

Prerequisites: [.NET SDK 8.0.4xx](https://dotnet.microsoft.com/download/dotnet/8.0) (pinned in `global.json`), Node.js LTS, Docker.

```bash
# Build the full backend solution
dotnet build Nestly.sln

# Run the consumer API
dotnet run --project backend/consumer-api/ConsumerApi

# Run the admin API
dotnet run --project backend/admin-api/AdminApi
```

Frontend apps and docker-compose for local PostgreSQL/Redis are pending scaffold — see the backlog.

## Documentation

The full documentation suite lives in [docs/](docs/README.md):

- [PROJECT.md](docs/PROJECT.md) — vision, goals, modules
- [SRS.md](docs/SRS.md) — complete requirements specification (v2)
- [ARCHITECTURE.md](docs/ARCHITECTURE.md) — system architecture blueprint
- [API.md](docs/API.md) · [DATABASE.md](docs/DATABASE.md) · [DOTNET.md](docs/DOTNET.md) · [FRONTEND.md](docs/FRONTEND.md)
- [SECURITY.md](docs/SECURITY.md) · [TESTING.md](docs/TESTING.md) · [CODING-STANDARDS.md](docs/CODING-STANDARDS.md) · [DEVOPS.md](docs/DEVOPS.md)
- [PROVIDER.md](docs/PROVIDER.md) — deferred Provider/Vendor module design
- [tasks.csv](docs/tasks.csv) — phased development backlog

## Development Status

Greenfield — architecture and requirements are fully specified; feature implementation follows the phased backlog in [docs/tasks.csv](docs/tasks.csv), starting with foundation hardening (Phase 0) and the Identity & Customer module (Phase 1).

## Branching

- `main` — production
- `develop` — integration branch (default for PRs)
