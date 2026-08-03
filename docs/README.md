# Nestly Documentation Index

Master index for the project documentation suite.

## PURPOSE

This document is the entry point for the project's documentation.

It defines:

- What each document is responsible for.
- Which document owns each topic.
- Where to find specific information.
- How to avoid duplicated documentation.

Every topic has exactly **one authoritative document**.

## DOCUMENTS

| Document | Responsibility |
|---|---|
| [ORIENTATION.md](ORIENTATION.md) | **Start here.** What exists today vs. what is planned, how the layers fit together, and the non-obvious rules. The only document describing current repository state |
| [../.claude/CLAUDE.md](../.claude/CLAUDE.md) | AI behavior, workflow, reasoning and response rules |
| [PROJECT.md](PROJECT.md) | Business domain, project vision, goals, users and modules |
| [SRS.md](SRS.md) | Full Software Requirements Specification (v2) — functional, workflow, validation, RBAC, screen, API, and operational requirements |
| [WORKFLOW.md](WORKFLOW.md) | Visual (Mermaid) workflow diagrams for project understanding — not authoritative, defers to SRS.md on conflict |
| [UI-GUIDE.md](UI-GUIDE.md) | Screenshot-illustrated walkthrough of each app's main screens, plus first-time local setup/seed/credentials instructions — companion to WORKFLOW.md, not authoritative |
| [ARCHITECTURE.md](ARCHITECTURE.md) | System architecture, layers, module boundaries and dependencies |
| [CODING-STANDARDS.md](CODING-STANDARDS.md) | Naming, code style, readability and general coding conventions |
| [DOTNET.md](DOTNET.md) | .NET 8, ASP.NET Core and framework-specific development standards |
| [DATABASE.md](DATABASE.md) | PostgreSQL, EF Core, schema design, indexing and data access standards |
| [API.md](API.md) | REST API conventions, DTOs, versioning and endpoint design |
| [FRONTEND.md](FRONTEND.md) | Next.js, React, TypeScript and frontend architecture |
| [SECURITY.md](SECURITY.md) | Authentication, authorization, secrets and security practices |
| [TESTING.md](TESTING.md) | Unit, integration, API and end-to-end testing strategy |
| [DEVOPS.md](DEVOPS.md) | Docker, CI/CD, deployment, monitoring and operations |
| [RUNBOOK-BACKUP-RESTORE.md](RUNBOOK-BACKUP-RESTORE.md) | Tested PostgreSQL backup/restore procedure (companion to DEVOPS.md's backup requirement) |
| [PROVIDER.md](PROVIDER.md) | Provider / Vendor module specification (Phase 7 — scheduled before launch) |
| [REFERRAL.md](REFERRAL.md) | Referral (Refer & Earn) module specification (Phase 9) |
| [PRODUCT-ENHANCEMENTS.md](PRODUCT-ENHANCEMENTS.md) | Subscription, Recurring Bookings, In-App Chat, Completion Verification specification (Phase 10) |
| [NESTLY-COINS.md](NESTLY-COINS.md) | Nestly Coins (reorder loyalty currency for customers and providers) specification (Phase 11) |
| [tasks.csv](tasks.csv) | Development backlog — phased tasks, priorities and dependencies |
| [archive/](archive/) | Original Word-format versions of these documents (historical) |

## TOPIC OWNERSHIP

| Topic | Owner Document |
|---|---|
| AI behavior | CLAUDE.md |
| Business vision | PROJECT.md |
| Business terminology | PROJECT.md |
| Functional requirements | SRS.md |
| Booking lifecycle and workflows | SRS.md |
| RBAC requirements | SRS.md |
| System architecture | ARCHITECTURE.md |
| Module boundaries | ARCHITECTURE.md |
| Dependency rules | ARCHITECTURE.md |
| Layer responsibilities | ARCHITECTURE.md |
| Naming conventions | CODING-STANDARDS.md |
| Code organization | CODING-STANDARDS.md |
| Code readability | CODING-STANDARDS.md |
| .NET conventions | DOTNET.md |
| ASP.NET Core | DOTNET.md |
| Dependency Injection | DOTNET.md |
| Middleware | DOTNET.md |
| Configuration | DOTNET.md |
| EF Core usage | DATABASE.md |
| PostgreSQL | DATABASE.md |
| Schema design | DATABASE.md |
| Migrations | DATABASE.md |
| Transactions | DATABASE.md |
| Indexes | DATABASE.md |
| Query optimization | DATABASE.md |
| REST standards | API.md |
| API versioning | API.md |
| DTOs | API.md |
| Status codes | API.md |
| Request/Response contracts | API.md |
| React | FRONTEND.md |
| Next.js | FRONTEND.md |
| TypeScript | FRONTEND.md |
| Components | FRONTEND.md |
| Authentication | SECURITY.md |
| Authorization | SECURITY.md |
| Secrets management | SECURITY.md |
| Data protection | SECURITY.md |
| Unit Testing | TESTING.md |
| Integration Testing | TESTING.md |
| API Testing | TESTING.md |
| Docker | DEVOPS.md |
| CI/CD | DEVOPS.md |
| Deployment | DEVOPS.md |
| Monitoring | DEVOPS.md |
| Provider module design | PROVIDER.md |
| Development backlog | tasks.csv |

## OWNERSHIP RULES

Every topic belongs to one document.

Do not duplicate guidance across multiple documents.

If a topic needs additional context, reference the owning document instead of repeating the content.

## WRITING PRINCIPLES

Every document should be:

- Focused
- Concise
- Actionable
- Easy to maintain
- Easy for AI to understand
- Free from unnecessary repetition

One document = One responsibility.

## CHANGE POLICY

Before adding new documentation:

1. Identify the topic.
2. Find the owning document.
3. Update only that document.
4. Remove duplicate guidance if it exists elsewhere.
5. Keep the documentation suite consistent.

## SUCCESS CRITERIA

A high-quality documentation suite should have:

- Clear ownership
- No duplicated topics
- No conflicting guidance
- Consistent terminology
- Simple navigation
- Easy maintenance
- AI-friendly structure
