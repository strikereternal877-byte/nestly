# ARCHITECTURE.md

Enterprise System Architecture Blueprint

## PURPOSE

This document defines the architectural blueprint of the Nestly platform.

It describes how the system is organized, how major components interact, how requests are processed, and the architectural principles that must be followed during development.

This document is the single source of truth for all architecture-related decisions.

## ARCHITECTURAL OBJECTIVES

The architecture is designed to achieve:

- Scalability
- Maintainability
- Reliability
- Security
- Extensibility
- Testability
- Observability
- Performance
- High Availability

## ARCHITECTURE STYLE

Nestly follows a **Modular Monolith** architecture built on:

- Clean Architecture
- Domain-Driven Design (DDD) principles
- Layered Architecture
- REST-based communication
- Event-driven processing where appropriate

Business modules are independent and designed to support future migration to Microservices with minimal changes.

## HIGH-LEVEL SYSTEM ARCHITECTURE

```
                       Users
                         │
                         ▼
              Next.js Web Application
                         │
                         ▼
              ASP.NET Core REST APIs
                         │
                         ▼
              ┌─────────────────────┐
              │  Application Layer  │
              └─────────────────────┘
                         │
                         ▼
              ┌─────────────────────┐
              │    Domain Layer     │
              └─────────────────────┘
                         │
                         ▼
              ┌─────────────────────┐
              │ Infrastructure Layer│
              └─────────────────────┘
                │        │        │
                ▼        ▼        ▼
           PostgreSQL  Redis  External Services
                          │
                          ▼
                       Hangfire
```

## REQUEST PROCESSING FLOW

Every request follows the same architectural pipeline.

```
Client
  ↓
Presentation Layer
  ↓
Application Layer
  ↓
Domain Layer
  ↓
Infrastructure Layer
  ↓
Database / External Services
  ↓
Response
```

#### Processing Rules

- Presentation handles HTTP communication.
- Application coordinates business use cases.
- Domain executes business rules.
- Infrastructure manages technical concerns.
- Persistence stores and retrieves data.
- Responses return through the same pipeline.

Business logic must remain inside the Domain layer.

## MODULE ORGANIZATION

The application is divided into independent business modules.

Examples include:

- Identity
- Customer
- Provider
- Category
- Service
- Booking
- Payment
- Notification
- Review
- Administration
- Reporting

Each module owns:

- Business logic
- Application services
- Domain model
- Persistence
- Internal implementation

Modules expose only the interfaces required by other modules.

## MODULE INTERACTION DIAGRAM

Identity │ ┌──────────────┼──────────────┐ ▼ ▼ Customer Provider │ │ └──────────────┬───────────────┘ ▼ Booking │ ┌────────────┼────────────┐ ▼ ▼ Payment Notification │ ▼ Reporting

#### Interaction Principles

- Modules communicate through well-defined interfaces.
- No direct database access between modules.
- Avoid circular dependencies.
- Minimize coupling.
- Preserve module independence.

## LAYER RESPONSIBILITIES

### Presentation Layer

Responsible for:

- HTTP communication
- Request routing
- Input validation
- Response generation

### Application Layer

Responsible for:

- Use case orchestration
- Workflow coordination
- Transaction boundaries
- Calling domain services

### Domain Layer

Responsible for:

- Business rules
- Domain entities
- Value objects
- Business invariants
- Domain services

This layer must remain independent of frameworks and infrastructure.

### Infrastructure Layer

Responsible for:

- Persistence
- External integrations
- File storage
- Email
- Background processing
- Caching
- Technical implementations

## DEPENDENCY RULES

The architecture follows strict dependency direction.

- Dependencies always point inward.
- Outer layers depend on inner layers.
- Inner layers never depend on outer layers.
- Business logic must not depend on implementation details.
- Prefer abstractions over concrete implementations.
- Circular dependencies are prohibited.

## CROSS-CUTTING CONCERNS

The following concerns are centralized and shared across the application:

- Logging
- Validation
- Exception Handling
- Configuration
- Monitoring
- Caching
- Auditing

Business modules must not duplicate these capabilities.

## UNIFIED LOGIN (task 206, resolved 2026-08-02)

Before this, `customer-web`, `admin-web` and `provider-web` each had an
independent `/login` at their own origin, with no way to reach the other two
apps from one place. Task 206 asked for "a single login entry point shared
by all three apps, redirecting to the correct app/dashboard based on account
type", and named two candidate approaches to choose between.

**Decision: shared login route calling the right backend per an account-type
selector, not a subdomain gateway issuing role-scoped tokens.**

Reasoning, verified against the actual repository state rather than assumed:

1. **No shared parent domain exists yet.** DEVOPS.md's OPEN DECISIONS still
   lists cloud provider, hosting platform and registry as unresolved — there
   is no production domain for a subdomain-gateway approach
   (`login.nestly.com` issuing a cookie scoped to `.nestly.com`, shared by
   `app.`/`admin.`/`provider.nestly.com`) to be validated against. Even in
   local development the three apps run on unrelated `localhost` ports, not
   subdomains of one parent.
2. **Account type cannot be derived from an identifier alone.**
   `CustomerAuthIdentity`, `AdminUser` and `ProviderAuthIdentity` are three
   independent tables, each with its own uniqueness scope — nothing stops
   the same email/mobile existing in more than one. A gateway that tried to
   auto-detect "which app does this identifier belong to" would need to
   probe all three backends (latency, and an email-enumeration leak across
   systems) and could still be ambiguous. An explicit account-type selector
   sidesteps this entirely.
3. **Every API already authenticates via a Bearer token in the
   `Authorization` header, never a cookie** (see SECURITY.md), and CORS
   credentials are deliberately off. A subdomain-gateway/shared-cookie
   approach would mean reopening that decision (enabling credentialed CORS,
   picking a shared cookie domain) for a feature that doesn't need it.

**Implementation**: `customer-web`'s `/login` gained an account-type
selector (Customer / Admin / Provider). Selecting Admin or Provider still
authenticates directly against `admin-api`/`provider-api` (each keeps issuing
its own independently-audienced token exactly as before — no change to
`JwtOptions`/`AdminJwtOptions`/`ProviderJwtOptions`), then hands the browser
off via a full-page redirect to that app's own origin with the session
carried in the URL fragment (`lib/unified-login-api.ts`), never a query
string — a fragment is never sent to a server, so the token doesn't touch
any access log on the hop, and the receiving `/auth/callback` page
(`admin-web`, `provider-web`) strips it from history the instant it's read.
This is the standard technique for a same-token cross-origin handoff when
there is no shared cookie domain to rely on instead. The only production
config change this required was adding `customer-web`'s origin to
`admin-api`'s and `provider-api`'s `Cors:AllowedOrigins` (`appsettings.*.json`)
— CORS remains credential-less throughout.

`admin-web`'s and `provider-web`'s own `/login` pages are deliberately left
in place, not removed — a bookmarked or direct visit to either app's own
origin must keep working, and there is no reverse proxy/DNS layer yet to
redirect one to the other. Fully retiring them in favor of the shared entry
point is a follow-up once real hosting/subdomain decisions in DEVOPS.md are
made and a proper redirect can be set up at the infrastructure layer instead
of in application code.

## DOMAIN DESIGN PRINCIPLES

The domain model should:

- Encapsulate business rules.
- Protect business invariants.
- Express business concepts clearly.
- Remain independent of technical implementation.
- Favor rich domain behavior over anemic models where appropriate.

## SCALABILITY STRATEGY

The architecture supports:

- Horizontal scaling
- Stateless application services
- Independent module evolution
- Efficient resource utilization
- Asynchronous processing for long-running operations

## RELIABILITY PRINCIPLES

The system should be designed for resilience through:

- Fault isolation
- Retry mechanisms
- Graceful degradation
- Health monitoring
- Failure recovery

## ARCHITECTURAL CONSTRAINTS

All development must adhere to the following constraints:

- Preserve module boundaries.
- Maintain layer separation.
- Do not bypass architectural layers.
- Do not introduce tight coupling.
- Do not duplicate business logic.
- Keep architecture simple and maintainable.

## ARCHITECTURE REVIEW CHECKLIST

Before accepting any architectural change, verify:

- Module boundaries are preserved.
- Dependency direction is correct.
- No circular dependencies exist.
- The design is scalable.
- The design is maintainable.
- The solution is testable.
- The architecture remains consistent with established principles.

## OUT OF SCOPE

This document does not define:

- Business requirements
- Functional specifications
- Technology implementation details
- Coding standards
- Database schema
- API contracts
- Security implementation
- Testing strategy
- Deployment process

Refer to the corresponding project documents for these topics.
