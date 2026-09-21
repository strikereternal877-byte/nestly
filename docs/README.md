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
| [MARKET.md](MARKET.md) | Market context, competitive landscape, revenue-model thesis, launch strategy and the commercial gap register for the Jaipur launch market |
| [LAUNCH-READINESS-AUDIT.md](LAUNCH-READINESS-AUDIT.md) | Evidence-based audit (2026-08-17) of what is actually implemented versus what ORIENTATION.md, the specs and `tasks.csv` claim |
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
| [LAUNCH-COST.md](LAUNCH-COST.md) | Production launch rollout plan and infra cost sizing/minimization for the Jaipur launch (companion to DEVOPS.md's open decisions; pending infra/ops sign-off) |
| [RUNBOOK-BACKUP-RESTORE.md](RUNBOOK-BACKUP-RESTORE.md) | Tested PostgreSQL backup/restore procedure (companion to DEVOPS.md's backup requirement) |
| [RUNBOOK-DEPLOYMENT.md](RUNBOOK-DEPLOYMENT.md) | Deployment procedure (companion to DEVOPS.md) |
| [QA-REPORT-2026-08-07.md](QA-REPORT-2026-08-07.md) | Feature inventory and static findings S1–S11 (still open). Its release verdict is **superseded by QA-REPORT-2026-08-18.md** — kept for the inventory and static findings only |
| [QA-REPORT-2026-08-18.md](QA-REPORT-2026-08-18.md) | Execution of QA phases 3–4 (task 318): full browser walkthrough and cross-service consistency check, with a **CONDITIONAL GO** verdict and a consolidated bug list |
| [audits/mobile-ux-customer-web-2026-08-18.md](audits/mobile-ux-customer-web-2026-08-18.md) · [-provider-web-](audits/mobile-ux-provider-web-2026-08-18.md) · [-admin-web-](audits/mobile-ux-admin-web-2026-08-18.md) | Per-app mobile/tablet UX findings (Phase 22, tasks #337–339) that scoped the rest of the mobile-first batch, plus each app's #351 safe-area audit appended |
| [ENHANCEMENT-BACKLOG-2026-08-08.md](ENHANCEMENT-BACKLOG-2026-08-08.md) | Verified spec-vs-code gaps with file:line evidence. **Closed** — its items became rows #303–#320; kept for its reasoning |
| [PRODUCTION-READINESS.md](PRODUCTION-READINESS.md) | What stands between a green build and a production deployment, with file:line evidence (2026-08-20). Rows #375–#391. Per ORIENTATION.md, this is where the next task comes from |
| [PHASE-26-HANDOFF.md](PHASE-26-HANDOFF.md) | Execution plan for Phase 26 — branches in flight, merge order, per-agent file-ownership boundaries, and the verification gate before any row is closed |
| [UAT-REPORT.md](UAT-REPORT.md) | User acceptance testing results |
| [BOOKING-FLOW-AUDIT.md](BOOKING-FLOW-AUDIT.md) | Point-in-time audit of the booking funnel (source of the Phase 13 defect rows) |
| [ENTERPRISE-GAP-WALKTHROUGH-2026-09-22.md](ENTERPRISE-GAP-WALKTHROUGH-2026-09-22.md) | Point-in-time manual pass over the full signup→booking→completion→payout lifecycle as customer, provider and admin — not a pass/fail check, a list of what enterprise readiness still needs beyond the happy path |
| [CATALOG-ARCHITECTURE-REVIEW.md](CATALOG-ARCHITECTURE-REVIEW.md) | Point-in-time review of the catalog hierarchy (service groups, variants, add-on groups) |
| [migrations-audit.md](migrations-audit.md) · [migrations-plan.md](migrations-plan.md) | Point-in-time migration audit and remediation plan |
| [PHASE-12-HANDOFF.md](PHASE-12-HANDOFF.md) · [PHASE-16-CLOUD-BRIEF.md](PHASE-16-CLOUD-BRIEF.md) | Historical phase handoff notes — superseded, kept for provenance |
| [PROVIDER.md](PROVIDER.md) | Provider / Vendor module specification (Phase 7) |
| [REFERRAL.md](REFERRAL.md) | Referral (Refer & Earn) module specification (Phase 9) |
| [PROVIDER-REFERRAL.md](PROVIDER-REFERRAL.md) | Provider Referral (provider-refers-provider growth program) module specification (Phase 27) |
| [PRODUCT-ENHANCEMENTS.md](PRODUCT-ENHANCEMENTS.md) | Subscription, Recurring Bookings, In-App Chat, Completion Verification specification (Phase 10) |
| [NESTLY-COINS.md](NESTLY-COINS.md) | Nestly Coins (reorder loyalty currency for customers and providers) specification (Phase 11) |
| [AMC.md](AMC.md) | Annual Maintenance Contract module specification: prepaid entitlement drawdown, redemption, renewal pipeline (Phase 20) |
| [PRICING.md](PRICING.md) | Pricing strategy recommendation: all-in transparent pricing, no surge, quote-guarantee mechanics (pending business sign-off) |
| [SUPPLY.md](SUPPLY.md) | Technician supply acquisition plan recommendation: sourcing, vetting pipeline, commercial offer, retention (pending ops sign-off) |
| [INSURANCE.md](INSURANCE.md) | Liability/insurance posture recommendation: property damage and technician injury coverage shape (pending legal sign-off) |
| [GST.md](GST.md) | GST and contracting posture recommendation: agent vs. principal model and its invoicing consequences (pending CA/tax counsel sign-off) |
| [assets/nestly-unit-economics.xlsx](assets/nestly-unit-economics.xlsx) | Costed per-revenue-type unit-economics model (Assumptions, Revenue Streams, Summary) referenced by MARKET.md §3 — formulas hand-verified, not yet confirmed by opening in Excel/LibreOffice (see its Read Me tab) |
| [TRACKING.md](TRACKING.md) | End-to-end order tracking (Phase 16) — state machine, location ingest, ETA pipeline, tracking hub, and the Google Maps configuration surface |
| [tasks.csv](tasks.csv) | Development backlog — phased tasks, priorities and dependencies |
| [archive/](archive/) | Original Word-format versions of these documents (historical) |

## TOPIC OWNERSHIP

| Topic | Owner Document |
|---|---|
| AI behavior | CLAUDE.md |
| Business vision | PROJECT.md |
| Business terminology | PROJECT.md |
| Market and competitor analysis | MARKET.md |
| Implementation status ("what is built") | ORIENTATION.md |
| Status-claim verification and audit history | LAUNCH-READINESS-AUDIT.md |
| Release readiness / go-no-go | QA-REPORT-2026-08-18.md |
| Spec-vs-code gap backlog | ENHANCEMENT-BACKLOG-2026-08-08.md (closed) |
| Production readiness / what blocks deployment | PRODUCTION-READINESS.md |
| Launch market strategy | MARKET.md |
| Revenue models and margin thesis | MARKET.md |
| Pricing posture and go-to-market | MARKET.md (strategic rationale) · PRICING.md (mechanics and policy) |
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
| Responsive / mobile-first design | FRONTEND.md |
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
| Live order tracking | TRACKING.md |
| Location ingest throttling/retention | TRACKING.md |
| ETA / routing pipeline | TRACKING.md |
| Google Maps API key management | TRACKING.md |
| AMC / entitlement contracts | AMC.md |
| Pricing mechanics and quote-guarantee policy | PRICING.md |
| Technician supply acquisition and vetting | SUPPLY.md |
| Liability and insurance coverage posture | INSURANCE.md |
| GST and contracting model (agent vs. principal) | GST.md |
| Development backlog | tasks.csv |

## OWNERSHIP RULES

Every topic belongs to one document.

Do not duplicate guidance across multiple documents.

If a topic needs additional context, reference the owning document instead of repeating the content.

### Implementation status has exactly one owner

**[ORIENTATION.md](ORIENTATION.md) owns "what is built".** No other document
may assert implementation status.

Module specifications describe *what the module should be*, not whether it
exists. A spec's `STATUS` section may state which phase delivered it and link
to ORIENTATION.md — it must not carry a standing "not implemented" claim,
because nothing keeps such a claim in sync with the code.

This rule exists because it was broken. On 2026-08-17, four specifications
(`PRODUCT-ENHANCEMENTS.md`, `REFERRAL.md`, `NESTLY-COINS.md`, `PROVIDER.md`)
still read *"Not implemented"* for modules that had shipped phases earlier —
which in turn caused a competitive analysis to be written against a product
position that had not been true for weeks. See
[LAUNCH-READINESS-AUDIT.md](LAUNCH-READINESS-AUDIT.md).

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
