# API.md

REST API Standards & Design Guidelines

## PURPOSE

This document defines the standards, conventions, and best practices for designing REST APIs within the Nestly platform.

It ensures that all APIs are consistent, predictable, scalable, versioned, and easy to consume.

This document is the single source of truth for API design.

## API DESIGN PRINCIPLES

Every API should be:

- RESTful
- Stateless
- Resource-Oriented
- Consistent
- Predictable
- Secure
- Versioned
- Backward Compatible

## API URL STRUCTURE

All endpoints should follow a consistent URI structure.

/api/v1/{resource} /api/v1/{resource}/{id} /api/v1/{resource}/{id}/{sub-resource}

Examples:

GET /api/v1/customers GET /api/v1/customers/{id} POST /api/v1/customers PUT /api/v1/customers/{id} PATCH /api/v1/customers/{id} DELETE /api/v1/customers/{id}

## RESOURCE NAMING

Rules:

- Use nouns, not verbs.
- Use plural resource names.
- Keep URLs lowercase.
- Use hyphens for multiple words.
- Avoid implementation-specific names.

Good Examples

/customers /bookings /service-providers /payments

Avoid

/GetCustomer /CreateBooking /DeleteUser

## HTTP METHODS

Use HTTP methods according to their intended purpose.

| Method | Usage |
|---|---|
| GET | Read data |
| POST | Create resources |
| PUT | Replace resources |
| PATCH | Partial update |
| DELETE | Remove resources |

## API VERSIONING

All public APIs must be versioned.

Preferred format

/api/v1/ /api/v2/

Rules:

- Never introduce breaking changes without a new version.
- Older versions remain supported until officially deprecated.

## REQUEST DESIGN

Requests should:

- Use DTOs
- Be strongly typed
- Be validated
- Be self-descriptive
- Avoid unnecessary parameters

Do not expose domain entities directly.

## DTO DESIGN

Use separate DTOs for different operations.

Examples:

- Create DTO
- Update DTO
- Response DTO
- Search DTO
- Summary DTO

Rules:

- Never expose internal entities.
- Keep DTOs focused.
- Avoid unnecessary properties.
- Keep request and response models independent.

## RESPONSE DESIGN

Responses should be:

- Consistent
- Predictable
- Self-descriptive

Typical response should include:

- Data
- Metadata (if applicable)
- Pagination (for collections)
- Error details (for failures)

## HTTP STATUS CODES

Use standard HTTP status codes.

| Code | Meaning |
|---|---|
| 200 | OK |
| 201 | Created |
| 204 | No Content |
| 400 | Bad Request |
| 401 | Unauthorized |
| 403 | Forbidden |
| 404 | Not Found |
| 409 | Conflict |
| 422 | Unprocessable Entity |
| 500 | Internal Server Error |

Do not return HTTP 200 for failed operations.

## PAGINATION

Collection APIs should support pagination.

Standard parameters:

- Page Number
- Page Size

Responses should include:

- Total Records
- Total Pages
- Current Page
- Page Size

Avoid returning excessively large datasets.

## FILTERING

Support filtering where appropriate.

Examples:

?status=Active ?category=Cleaning ?city=Delhi

Filtering should remain optional and consistent.

## SORTING

Support sorting using query parameters.

Example

?sort=name ?sort=-createdDate

Use ascending order by default.

## SEARCH

Search endpoints should:

- Support keyword searches
- Support filtering
- Support pagination
- Return predictable results

Search operations should remain fast and optimized.

## IDENTITY

Every resource must have a stable unique identifier.

Identifiers must remain immutable after creation.

## IDEMPOTENCY

Follow HTTP idempotency rules.

- GET should be idempotent.
- PUT should be idempotent.
- DELETE should be idempotent.
- POST should create new resources unless explicitly designed otherwise.

## VALIDATION

Validate every incoming request.

Validation should include:

- Required fields
- Data format
- Business constraints
- Input length
- Allowed values

Reject invalid requests before processing.

## ERROR RESPONSE

Return consistent error responses.

Every error should clearly communicate:

- Error Code
- Message
- Validation Errors (if applicable)
- Correlation Identifier (if available)

Never expose internal implementation details.

## API DOCUMENTATION

Every endpoint must include:

- Purpose
- HTTP Method
- URL
- Request Model
- Response Model
- Status Codes
- Validation Rules
- Example Requests
- Example Responses

Documentation must remain synchronized with implementation.

## BACKWARD COMPATIBILITY

Maintain backward compatibility whenever possible.

Breaking changes require:

- New API version
- Migration strategy
- Deprecation notice

## API REVIEW CHECKLIST

Before publishing an API, verify:

- Resource naming is correct.
- Correct HTTP method is used.
- DTOs are defined appropriately.
- Validation rules exist.
- Status codes are correct.
- Pagination is supported where needed.
- Error responses are consistent.
- API is documented.
- Backward compatibility is maintained.

## OUT OF SCOPE

This document does not define:

- ASP.NET Core implementation
- Controller implementation
- Dependency Injection
- Authentication
- Authorization
- Business Logic
- Database Access
- ORM Configuration
- Security Policies
- Deployment

Refer to the corresponding project documents for these topics.

---

# PART 2 — AUTH AND PROFILE SERVICE REFERENCE

Everything above this line is the general standard that all Nestly APIs
follow. This part is the concrete endpoint reference for the services
implemented in Phase 1: **Auth**, **Profile**, and **Address**
(SRS 24.1, 24.2). It is generated from — and must stay in step with — the
controllers in `backend/consumer-api/ConsumerApi/Controllers/`.

## CONVENTIONS USED IN THIS REFERENCE

- **Base URL**: all paths below are relative to the Consumer API host and
  carry the version segment, e.g. `POST /api/v1/auth/login/password`.
- **Content type**: `application/json` on request and response.
- **Casing**: request and response properties are camelCase. The C# records
  they map to are PascalCase; `System.Text.Json` applies the camelCase policy.
- **Auth column**: `None` means the endpoint is reachable unauthenticated.
  `Bearer` means it requires `Authorization: Bearer <accessToken>`; the
  customer is identified by the token's `sub` claim, never by a route or body
  parameter.
- **Errors**: every failure is RFC 7807 Problem Details — see
  [Error Response](#error-response) above and the shared shape at the end of
  this part.

## AUTH ENDPOINT INDEX

| # | Method | URL | Purpose | Auth | Rate limit |
|---|--------|-----|---------|------|------------|
| 1 | POST | `/api/v1/auth/registration/otp` | Send a registration OTP | None | `otp` |
| 2 | POST | `/api/v1/auth/registration` | Complete registration | None | — |
| 3 | POST | `/api/v1/auth/login/otp` | Send a login OTP | None | `otp` |
| 4 | POST | `/api/v1/auth/login/otp/verify` | Log in with mobile + OTP | None | `login` |
| 5 | POST | `/api/v1/auth/login/password` | Log in with email + password | None | `login` |
| 6 | POST | `/api/v1/auth/refresh` | Rotate the token pair | None | — |
| 7 | POST | `/api/v1/auth/logout` | Invalidate a session | None | — |
| 8 | POST | `/api/v1/auth/password/forgot` | Request a reset code | None | `otp` |
| 9 | POST | `/api/v1/auth/password/reset` | Set a new password | None | `login` |

Rate-limit policies are partitioned by client IP (`Program.cs`): `otp` permits
5 requests/hour, `login` permits 10 requests/15 minutes. Both return `429`
when exceeded. These are separate from the per-identifier account lockout,
which is what stops a slow distributed brute force.

## PROFILE AND ADDRESS ENDPOINT INDEX

| #  | Method | URL | Purpose | Auth |
|----|--------|-----|---------|------|
| 10 | GET | `/api/v1/profile` | View profile | Bearer |
| 11 | PUT | `/api/v1/profile` | Edit name and optional profile data | Bearer |
| 12 | POST | `/api/v1/profile/mobile/otp` | Send a code to a new mobile number | Bearer |
| 13 | POST | `/api/v1/profile/mobile` | Apply the mobile change | Bearer |
| 14 | POST | `/api/v1/profile/email/otp` | Send a code to a new email address | Bearer |
| 15 | POST | `/api/v1/profile/email` | Apply the email change | Bearer |
| 16 | GET | `/api/v1/profile/preferences` | Read communication preferences | Bearer |
| 17 | PUT | `/api/v1/profile/preferences` | Replace communication preferences | Bearer |
| 18 | GET | `/api/v1/addresses` | List the caller's addresses | Bearer |
| 19 | POST | `/api/v1/addresses` | Add an address | Bearer |
| 20 | PUT | `/api/v1/addresses/{id}` | Edit an address | Bearer |
| 21 | DELETE | `/api/v1/addresses/{id}` | Delete an address | Bearer |
| 22 | POST | `/api/v1/addresses/{id}/default` | Set the default address | Bearer |

---

## 1. POST /api/v1/auth/registration/otp

**Purpose** — Step 1 of registration (SRS 11.2.1): send a one-time code to a
mobile number to prove the caller controls it.

**Request**

```json
{ "mobile": "+919876543210" }
```

| Field | Type | Required | Rules |
|-------|------|----------|-------|
| `mobile` | string | yes | `^\+?[1-9]\d{7,14}$` |

**Responses** — `200` empty body on success.

| Status | When |
|--------|------|
| 200 | Code sent |
| 400 | Mobile fails validation, or a code was requested within the last 30s (`Otp.TooManyRequests`) |
| 409 | Mobile already registered (`Registration.MobileAlreadyRegistered`) |
| 429 | `otp` rate limit exceeded |

## 2. POST /api/v1/auth/registration

**Purpose** — Step 2 of registration: create the customer once the OTP
verifies. Email + password is optional; mobile + OTP always works.

**Request**

```json
{
  "mobile": "+919876543210",
  "otpCode": "123456",
  "name": "Asha Menon",
  "email": "asha@example.com",
  "password": "a-strong-password",
  "consentAccepted": true
}
```

| Field | Type | Required | Rules |
|-------|------|----------|-------|
| `mobile` | string | yes | `^\+?[1-9]\d{7,14}$` |
| `otpCode` | string | yes | exactly 6 digits |
| `name` | string | yes | 1–200 chars |
| `email` | string \| null | no | valid email; required if `password` is set |
| `password` | string \| null | no | min 8 chars; rejected when password auth is disabled |
| `consentAccepted` | boolean | yes | must be `true` |

**Response** — `201`

```json
{
  "id": "3f2504e0-4f89-11d3-9a0c-0305e82c3301",
  "mobile": "+919876543210",
  "email": "asha@example.com",
  "name": "Asha Menon",
  "status": "Active"
}
```

The account starts `Active`: the OTP already proved mobile ownership, so
there is nothing left to verify.

| Status | When |
|--------|------|
| 201 | Customer created |
| 400 | Validation failure, wrong/expired OTP, consent not accepted, password without email |
| 409 | Mobile or email already registered |

## 3. POST /api/v1/auth/login/otp

**Purpose** — Send a login code to an already-registered mobile number
(SRS 11.2.2).

**Request** — `{ "mobile": "+919876543210" }`

| Status | When |
|--------|------|
| 200 | Code sent |
| 400 | Validation failure or resend cooldown |
| 404 | No account for that number (`Login.NotFound`) |
| 429 | `otp` rate limit exceeded |

## 4. POST /api/v1/auth/login/otp/verify

**Purpose** — Exchange a mobile + OTP pair for a session.

**Request**

```json
{ "mobile": "+919876543210", "otpCode": "123456" }
```

**Response** — `200`, the shared `LoginResponse`:

```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "accessTokenExpiresAtUtc": "2026-07-28T13:45:00Z",
  "refreshToken": "b1c2d3e4f5..."
}
```

The refresh token is returned in plaintext exactly once; only its SHA-256
hash is stored server-side.

| Status | When |
|--------|------|
| 200 | Signed in |
| 400 | Validation failure or wrong/expired OTP |
| 401 | — |
| 403 | Account locked (`Login.AccountLocked`) or not active (`Login.AccountNotActive`) |
| 404 | No account for that number |
| 429 | `login` rate limit exceeded |

## 5. POST /api/v1/auth/login/password

**Purpose** — Log in with email + password, when password auth is enabled.

**Request**

```json
{ "email": "asha@example.com", "password": "a-strong-password" }
```

**Response** — `200`, `LoginResponse` (as above).

| Status | When |
|--------|------|
| 200 | Signed in |
| 400 | Validation failure |
| 401 | `Login.InvalidCredentials` — returned identically for an unknown email and a wrong password, so the endpoint cannot be used to enumerate accounts |
| 403 | Account locked or not active |
| 429 | `login` rate limit exceeded |

## 6. POST /api/v1/auth/refresh

**Purpose** — Exchange a still-valid refresh token for a new pair. The old
token is revoked on use (rotation, SRS 28.3), so a captured token cannot be
replayed after the legitimate client has used it.

**Request** — `{ "refreshToken": "b1c2d3e4f5..." }`

**Response** — `200`, `LoginResponse`.

| Status | When |
|--------|------|
| 200 | New pair issued |
| 400 | Missing token |
| 401 | Token unknown, expired, or already revoked |

## 7. POST /api/v1/auth/logout

**Purpose** — Invalidate one session's refresh token (SRS 11.2.2).

**Request** — `{ "refreshToken": "b1c2d3e4f5..." }`

| Status | When |
|--------|------|
| 204 | Session invalidated. Also returned for an already-invalid token — the caller's desired end state (no active session) holds either way |
| 400 | Missing token |

## 8. POST /api/v1/auth/password/forgot

**Purpose** — Step 1 of the reset flow (SRS 11.2.2). The code is sent to the
**mobile number on the account**, not to the email: the mobile was proven by
OTP at registration, whereas the email is only ever an unverified identifier.

**Request** — `{ "email": "asha@example.com" }`

| Status | When |
|--------|------|
| 200 | Always, whether or not the email is registered — an honest 404 would turn this endpoint into an email-address oracle |
| 400 | Email fails validation, resend cooldown, or password auth is disabled |
| 429 | `otp` rate limit exceeded |

## 9. POST /api/v1/auth/password/reset

**Purpose** — Step 2: set the new password once the code verifies. Every
active session for the customer is revoked, so tokens issued before the reset
stop working immediately.

**Request**

```json
{
  "email": "asha@example.com",
  "otpCode": "123456",
  "newPassword": "a-new-strong-password"
}
```

| Field | Type | Required | Rules |
|-------|------|----------|-------|
| `email` | string | yes | valid email |
| `otpCode` | string | yes | exactly 6 digits |
| `newPassword` | string | yes | min 8 chars — the same floor registration enforces |

| Status | When |
|--------|------|
| 204 | Password changed and sessions revoked |
| 400 | Validation failure; wrong/expired code; unknown email or inactive account (all reported as the single `PasswordReset.Invalid`, so a caller cannot tell them apart) |
| 429 | `login` rate limit exceeded |

---

## 10. GET /api/v1/profile

**Purpose** — View profile (SRS 11.2.3). Scoped to the caller's own `sub`
claim.

**Response** — `200`, the `CustomerProfileResponse` schema (below).

| Status | When |
|--------|------|
| 200 | Profile returned |
| 401 | Missing or invalid bearer token |
| 404 | Customer record not found |

## 11. PUT /api/v1/profile

**Purpose** — Edit name and optional profile data (SRS 11.2.3).

`mobile` and `email` are **deliberately absent** from this request: both are
identity-bearing (mobile is the OTP login identifier, email the password
one), so they move only through the re-verified endpoints 12–15. A PUT that
could change them would let anyone holding a valid token take over a login
identifier.

**Request** — the `UpdateProfileRequest` schema (below).

| Status | When |
|--------|------|
| 200 | Updated; returns the full `CustomerProfileResponse` |
| 400 | Validation failure |
| 401 | Missing or invalid bearer token |
| 404 | Customer record not found |

## 12. POST /api/v1/profile/mobile/otp

**Purpose** — Step 1 of a mobile change: send a code to the number being
claimed.

**Request** — `{ "newMobile": "+919000000001" }`

| Status | When |
|--------|------|
| 200 | Code sent to the new number |
| 400 | Validation failure; same as the current number (`Profile.MobileUnchanged`); resend cooldown |
| 401 | Missing or invalid bearer token |
| 409 | Number already registered to another account |
| 429 | `otp` rate limit exceeded |

## 13. POST /api/v1/profile/mobile

**Purpose** — Step 2: apply the change once the code verifies. Updates the
customer record *and* repoints the mobile auth identity, so OTP login follows
the new number.

**Request** — `{ "newMobile": "+919000000001", "otpCode": "123456" }`

| Status | When |
|--------|------|
| 200 | Changed; returns the updated `CustomerProfileResponse` |
| 400 | Validation failure or wrong/expired code |
| 401 | Missing or invalid bearer token |
| 409 | Number claimed by another account since the code was issued |

## 14. POST /api/v1/profile/email/otp

**Purpose** — Step 1 of an email change. Unlike every other OTP in the
system, this one is delivered over **email**, to the address being claimed —
sending it to the address already on file would prove nothing about the new
one.

**Request** — `{ "newEmail": "new.address@example.com" }`

| Status | When |
|--------|------|
| 200 | Code sent to the new address |
| 400 | Validation failure; same as the current address (`Profile.EmailUnchanged`); resend cooldown |
| 401 | Missing or invalid bearer token |
| 409 | Address already registered (only when `Identity:RequireUniqueEmail` is true) |
| 429 | `otp` rate limit exceeded |

## 15. POST /api/v1/profile/email

**Purpose** — Step 2: apply the change. Also repoints the email+password auth
identity when one exists, so password login follows the new address.

**Request** — `{ "newEmail": "new.address@example.com", "otpCode": "123456" }`

| Status | When |
|--------|------|
| 200 | Changed; returns the updated `CustomerProfileResponse` |
| 400 | Validation failure or wrong/expired code |
| 401 | Missing or invalid bearer token |
| 409 | Address claimed by another account since the code was issued |

## 16. GET /api/v1/profile/preferences

**Purpose** — Read communication preferences (SRS 11.2.3, channels per
SRS 30.2).

A customer who has never saved a preference gets the defaults without a row
being created — a GET stays side-effect free.

| Status | When |
|--------|------|
| 200 | Preferences returned |
| 401 | Missing or invalid bearer token |
| 404 | Customer record not found |

## 17. PUT /api/v1/profile/preferences

**Purpose** — Replace communication preferences. Every flag is required, so a
save always states the full desired state rather than leaving a channel
ambiguously unset.

**Scope note** — these flags govern transactional (booking/account) and
promotional traffic. OTP and other security messages are **not** covered and
cannot be switched off; disabling them would lock a customer out of their own
account.

| Status | When |
|--------|------|
| 200 | Saved; returns the stored preferences |
| 401 | Missing or invalid bearer token |
| 404 | Customer record not found |

---

## 18–22. ADDRESS ENDPOINTS

Full field rules live with the address module; the identity-relevant
guarantees are:

- Every action resolves the customer from the JWT `sub` claim. An address id
  belonging to another customer returns `404`, not `403` — the API does not
  confirm that someone else's id exists (SRS 28.3 IDOR).
- The first address a customer creates is always made default, so they are
  never left with none.
- Exactly one default per customer is enforced by a partial unique index, not
  only by service code.

| Endpoint | Success | Other |
|----------|---------|-------|
| `GET /api/v1/addresses` | 200 (array, possibly empty) | 401 |
| `POST /api/v1/addresses` | 201 | 400, 401 |
| `PUT /api/v1/addresses/{id}` | 200 | 400, 401, 404 |
| `DELETE /api/v1/addresses/{id}` | 204 | 401, 404 |
| `POST /api/v1/addresses/{id}/default` | 204 | 401, 404 |

---

# PAYLOAD SCHEMAS

The schemas below are the normative definition of the Profile payloads. They
are expressed as **JSON Schema (draft 2020-12)**, which is the schema
language OpenAPI 3.1 itself uses and the one `System.Text.Json` payloads are
actually validated against in practice.

> **On XSD** — an earlier plan item asked for `.xsd` files for these payloads.
> XSD describes XML documents; these endpoints neither accept nor produce
> XML, and ASP.NET Core is not configured with an XML formatter. Shipping
> XSDs would mean maintaining a second schema that nothing reads and that
> would silently drift from the contract. JSON Schema is the correct
> equivalent artefact and is what follows. If an XML-only consumer ever
> appears, the right move is to add an XML formatter and generate the XSD
> from these types then — not to hand-write one now.

## CustomerProfileResponse

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://nestly.app/schemas/customer-profile-response.json",
  "title": "CustomerProfileResponse",
  "type": "object",
  "additionalProperties": false,
  "required": ["id", "mobile", "email", "name", "dateOfBirth", "city",
               "state", "pincode", "country", "status", "createdAt", "updatedAt"],
  "properties": {
    "id": { "type": "string", "format": "uuid" },
    "mobile": { "type": "string", "pattern": "^\\+?[1-9]\\d{7,14}$" },
    "email": { "type": ["string", "null"], "format": "email", "maxLength": 200 },
    "name": { "type": "string", "minLength": 1, "maxLength": 200 },
    "dateOfBirth": { "type": ["string", "null"], "format": "date-time" },
    "city": { "type": ["string", "null"], "maxLength": 200 },
    "state": { "type": ["string", "null"], "maxLength": 200 },
    "pincode": { "type": ["string", "null"], "pattern": "^\\d{6}$" },
    "country": { "type": ["string", "null"], "maxLength": 200 },
    "status": { "type": "string", "enum": ["Active", "Blocked", "Unverified", "SoftDeleted"] },
    "createdAt": { "type": "string", "format": "date-time" },
    "updatedAt": { "type": "string", "format": "date-time" }
  }
}
```

No property carries credential material: password hashes, refresh tokens, and
session rows never leave the persistence layer.

## UpdateProfileRequest

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://nestly.app/schemas/update-profile-request.json",
  "title": "UpdateProfileRequest",
  "type": "object",
  "additionalProperties": false,
  "required": ["name"],
  "properties": {
    "name": { "type": "string", "minLength": 1, "maxLength": 200 },
    "dateOfBirth": {
      "type": ["string", "null"],
      "format": "date-time",
      "description": "Must be in the past."
    },
    "city": { "type": ["string", "null"], "maxLength": 200 },
    "state": { "type": ["string", "null"], "maxLength": 200 },
    "pincode": { "type": ["string", "null"], "pattern": "^\\d{6}$" },
    "country": { "type": ["string", "null"], "maxLength": 200 }
  }
}
```

`mobile` and `email` are absent by design — see endpoint 11.

## CommunicationPreferencesRequest / Response

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://nestly.app/schemas/communication-preferences.json",
  "title": "CommunicationPreferences",
  "type": "object",
  "additionalProperties": false,
  "required": ["transactionalSms", "transactionalEmail", "transactionalWhatsApp",
               "promotionalSms", "promotionalEmail", "promotionalWhatsApp", "push"],
  "properties": {
    "transactionalSms": { "type": "boolean", "default": true },
    "transactionalEmail": { "type": "boolean", "default": true },
    "transactionalWhatsApp": { "type": "boolean", "default": false },
    "promotionalSms": { "type": "boolean", "default": false },
    "promotionalEmail": { "type": "boolean", "default": false },
    "promotionalWhatsApp": { "type": "boolean", "default": false },
    "push": {
      "type": "boolean",
      "default": false,
      "description": "SRS 30.2 lists Push as a future channel; the flag is stored now so enabling it needs no schema change."
    }
  }
}
```

The response carries the same properties plus `updatedAt`
(`string`, `date-time`).

## Error (all endpoints)

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://nestly.app/schemas/problem-details.json",
  "title": "ProblemDetails",
  "type": "object",
  "properties": {
    "type": { "type": "string", "format": "uri" },
    "title": { "type": "string" },
    "status": { "type": "integer" },
    "detail": { "type": "string" },
    "correlationId": { "type": "string" },
    "errors": {
      "description": "Present on validation failures; keyed by field name.",
      "type": "object",
      "additionalProperties": { "type": "array", "items": { "type": "string" } }
    }
  }
}
```

Error codes follow the `Module.Reason` convention (`Login.AccountLocked`,
`Profile.MobileUnchanged`, `Otp.Expired`). They are stable and safe to branch
on; `detail` is human-facing and may be reworded.

# OPENAPI / SWAGGER

Swagger UI is served in Development only, at `/swagger`, from the generated
OpenAPI document at `/swagger/v1/swagger.json` (`Program.cs`). Because
`AddSwaggerGen` reflects over the controllers, the operation list, request and
response types, and the status codes declared via `[ProducesResponseType]`
are generated from the code itself and cannot drift from it.

What reflection cannot infer, and what this document therefore carries, is
intent: which of two identical-looking 400s is deliberate, why `forgot`
always returns 200, and why `mobile` is missing from the profile PUT. Keep the
two in step — the generated document for shape, this reference for reasoning.

# PART 3 — FULL ENDPOINT REFERENCE (GENERATED)

<!-- BEGIN GENERATED ENDPOINT REFERENCE -->

Generated by `scripts/generate-openapi.sh` (which drives
`scripts/generate_endpoint_reference.py`) against the real OpenAPI documents
Swashbuckle produces for each API (`AddSwaggerGen`/`UseSwagger`), cross-referenced
with each controller action's `/// <summary>` doc comment and
`[Authorize]`/`[AllowAnonymous]` attributes — this solution has no
`IncludeXmlComments` wired, so the raw OpenAPI JSON's own summaries are
empty and the real one-line descriptions have to come from source.

**Generated against commit `564ca806` on 2026-10-03**: 93 controllers,
557 operations across the three APIs. Routes, request/response shapes
and status codes are reflection-derived from the code and cannot drift from
it *as of that commit*; controller doc comments can still be edited without
re-running this script, and new controllers won't appear until it's re-run.
Treat this table as a snapshot, not a live feed — regenerate it
(`scripts/generate-openapi.sh`) whenever the controller surface changes
materially, the same way this repo's other generated/audited docs
(docs/TRACKING.md, docs/ORIENTATION.md) flag their own staleness rather than
pretending to be permanently current.

"Success Response" shows only the 2xx/204 case. Every non-2xx response not
shown here follows the `ProblemDetails` shape documented earlier in this
file (see ERROR RESPONSE / PAYLOAD SCHEMAS) unless the table says otherwise —
that convention is enforced by `GlobalExceptionHandlingMiddleware` for
unhandled failures and by each controller's explicit
`[ProducesResponseType]` list for the rest, so it is not repeated per row.

## CONSUMER-API (customer-facing)

### Amc

Customer-facing AMC flow (docs/AMC.md): browse the plan catalog, purchase a contract for a named asset, view "my AMC contracts", cancel, and redeem entitlement into an ordinary booking. Every action scoped to the caller's own customer id, same pattern as `SubscriptionController`.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| POST | `/api/v{version}/amc/contracts` | Purchases a plan for a named asset (docs/AMC.md - records the contract; does not charge a real payment, see OPEN DECISIONS #4). | Customer JWT | AmcContractPurchaseRequest | 201 → MyAmcContractResponse |
| GET | `/api/v{version}/amc/plans` | Every AMC plan currently open to new purchases, optionally filtered to one service category. | Customer JWT | — | 200 → AmcPlanBrowseResponse[] |
| GET | `/api/v{version}/me/amc-contracts` | The caller's AMC contracts, active and past, newest first. | Customer JWT | — | 200 → MyAmcContractResponse[] |
| GET | `/api/v{version}/me/amc-contracts/{id}` | One contract's detail plus its full visit history. | Customer JWT | — | 200 → MyAmcContractResponse |
| POST | `/api/v{version}/me/amc-contracts/{id}/cancel` | Customer-initiated cancellation - immediate, terminal. | Customer JWT | — | 204 No Content |
| POST | `/api/v{version}/me/amc-contracts/{id}/redeem` | Redeems entitlement against a contract: creates a zero-priced booking through the same orchestration a normal "Book now" tap uses (docs/AMC.md). Entitlement itself is drawn down only once the resulting booking reaches Completed, not here. | Customer JWT | BookingSummaryRequest | 201 → BookingDetailResponse |

### Auth

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| POST | `/api/v{version}/auth/login/otp` | Send a login OTP to an already-registered mobile number (SRS 11.2.2). | Public | RequestLoginOtpRequest | 204 No Content |
| POST | `/api/v{version}/auth/login/otp/verify` | Login via mobile OTP (SRS 11.2.2). | Public | LoginWithOtpRequest | 200 → LoginResponse |
| POST | `/api/v{version}/auth/login/password` | Login via email + password, when password auth is enabled (SRS 11.2.2). | Public | LoginWithPasswordRequest | 200 → LoginResponse |
| POST | `/api/v{version}/auth/logout` | Invalidate a session's refresh token (SRS 11.2.2: logout invalidates the active session). | Public | LogoutRequest | 204 No Content |
| POST | `/api/v{version}/auth/password/forgot` | Step 1 of the reset flow (SRS 11.2.2). Always 200 — see `RequestResetAsync` for why an unknown address is not reported as such. | Public | ForgotPasswordRequest | 200 OK |
| POST | `/api/v{version}/auth/password/reset` | Step 2: set the new password once the OTP verifies (SRS 11.2.2). | Public | ResetPasswordRequest | 204 No Content |
| POST | `/api/v{version}/auth/refresh` | Exchange a still-valid refresh token for a new access+refresh pair (rotation, SRS 28.3). | Public | RefreshTokenRequest | 200 → LoginResponse |
| POST | `/api/v{version}/auth/registration` | Step 2: complete registration once the OTP has been verified (SRS 11.2.1). | Public | RegisterCustomerRequest | 201 → CustomerSummaryResponse |
| POST | `/api/v{version}/auth/registration/email` | Email-first registration step 2: complete registration once the email OTP has been verified. | Public | RegisterCustomerWithEmailRequest | 201 → CustomerSummaryResponse |
| POST | `/api/v{version}/auth/registration/email-otp` | Email-first registration step 1: send an OTP to an email address instead of a mobile number. | Public | RequestRegistrationEmailOtpRequest | 204 No Content |
| POST | `/api/v{version}/auth/registration/otp` | Step 1: send a registration OTP to a mobile number (SRS 11.2.1). | Public | RequestRegistrationOtpRequest | 204 No Content |

### Banners

Public storefront banners (SRS 11.1.2/11.1.3 "home banner shall be admin-configurable"). No auth - anyone loading the home page reads these, same as `CategoriesController`. Only live, publish-windowed banners are returned; admin CRUD lives in admin-api's BannersController.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/banners/home` | The banners currently live for the home page, ordered for display. Empty array when none are live. | Public | — | 200 → HomeBannerResponse[] |

### BookingCompletionProof

Completion proof (photos + checklist) for a booking (SRS 11.13, task 198). Read-only, same shape RefundsController exposes for refund status.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/bookings/{bookingId}/completion-proof` | _(no doc comment)_ | Customer JWT | — | 200 → BookingCompletionProofResponse |

### BookingSupportTickets

Booking-scoped view of a customer's support tickets (SRS 11.18.2 "booking reference", task 86d). Same underlying tickets as `SupportTicketsController`, filtered by booking.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/bookings/{bookingId}/support-tickets` | _(no doc comment)_ | Customer JWT | — | 200 → SupportTicketSummaryResponse[] |

### Bookings

Booking (SRS 13). Every action is scoped to the caller's own customer id — never a route/body parameter.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/bookings` | Lists the caller's bookings, optionally filtered to a status bucket, newest first (SRS 11.13, task 60b). Paged - `page`/`pageSize` default to 1/20, same as the admin booking search. | Customer JWT | — | 200 → BookingListResponse |
| POST | `/api/v{version}/bookings` | Creates a booking (SRS 13, tasks 58-59). Re-validates every precondition the summary already checked - a summary is not a reservation. | Customer JWT | BookingSummaryRequest | 201 → BookingDetailResponse |
| POST | `/api/v{version}/bookings/summary` | Previews what booking would produce - price, slot, and policy summary - without persisting anything (SRS 11.7, task 57). | Customer JWT | BookingSummaryRequest | 200 → BookingSummaryResponse |
| GET | `/api/v{version}/bookings/{id}` | Booking detail with its full status timeline (SRS 11.13, 24.6, task 60c). | Customer JWT | — | 200 → BookingDetailResponse |
| GET | `/api/v{version}/bookings/{id}/tracking` | The live tracking snapshot for a booking in progress (task 275) - the one-shot read the tracking screen loads before the SignalR hub starts pushing updates into it. Narrower than `Detail` on purpose: status, who is coming (with a masked phone, never the raw number), where they are, when they are expected, and where they are heading. No 403 is documented because none is possible - someone else's booking is a 404, so this endpoint cannot be used to confirm a booking id exists. | Customer JWT | — | 200 → BookingTrackingResponse |

### Cancellations

Customer-initiated booking cancellation (SRS 11.14, 24.6, tasks 80a-c, 81). Every action is scoped to the caller's own customer id, same as `BookingsController`.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| POST | `/api/v{version}/bookings/{bookingId}/cancellation` | Confirms the cancellation - transitions the booking, raises a refund if one is owed, tells the assigned professional the booking is off, and returns the outcome (SRS 24.6). | Customer JWT | CancelBookingRequest | 200 → CancellationOutcomeResponse |
| GET | `/api/v{version}/bookings/{bookingId}/cancellation/policy` | Cancellation eligibility + fee/refund policy preview, shown before the customer confirms (SRS 11.14.3). A late-reschedule fee the customer already paid is counted against the cancellation fee (`RescheduleFeeCredited`), not charged on top. | Customer JWT | — | 200 → CancellationPolicyResponse |

### CatalogSearch

Catalog search (task 42c, SRS 11.5-11.6, 24.3). No auth - anyone can search.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/catalog/search` | Search categories and services by name. | Public | — | 200 → CatalogSearchResponse |

### Categories

Public category catalog (task 41, SRS 11.1/11.5). No auth - anyone can browse.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/categories` | List active categories serviceable in a city (SRS 11.1), optionally narrowed to a specific pincode when the customer has picked an area rather than just a city (SRS 11.1.3). | Public | — | 200 → CategorySummaryResponse[] |
| GET | `/api/v{version}/categories/{slug}` | Category detail with its active services and their add-ons (SRS 11.5). | Public | — | 200 → CategoryDetailResponse |

### Chat

Customer-facing chat over a booking or support-ticket thread (task 191). Every action is scoped to the caller's own customer id - never a route/body parameter - same convention as BookingsController/SupportTicketsController. REST is the actual send/read path (works with or without a live socket); `ChatHub` (mapped at `/hubs/chat`, see Program.cs) only pushes live updates to a thread once a client has GETten/POSTed through here at least once - see ChatHub's own doc comment for the full real-time design.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| POST | `/api/v{version}/chat/threads` | Returns the thread for a booking/support-ticket context, creating it on first use (task 191). | Customer JWT | GetOrCreateChatThreadRequest | 200 → ChatThreadResponse |
| GET | `/api/v{version}/chat/threads/{threadId}/messages` | Paginated history, oldest first (task 191, 192). | Customer JWT | — | 200 → ChatMessagePageResult |
| POST | `/api/v{version}/chat/threads/{threadId}/messages` | Sends a message - the REST send path task 190 calls out explicitly, not just a fallback for a broken socket. | Customer JWT | SendChatMessageRequest | 201 → ChatMessageResponse |
| POST | `/api/v{version}/chat/threads/{threadId}/read` | Marks every message not sent by this customer as read (task 192 read receipts). | Customer JWT | — | 204 No Content |

### CmsPages

Public static pages (SRS 12.16.1/12.16.2) - Terms &amp; Conditions, Privacy Policy, Refund/Cancellation Policy, Contact Us and any other admin-authored page. No auth - anyone can read a published page, same as `BannersController`/`LandingController`. Only a live (published, within its publish window) page is ever returned; admin CRUD lives in admin-api's `CmsPagesController`.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/cms/pages/{slug}` | The live page at this slug. 404 covers "no such page", "still a draft" and "outside its publish window" identically - a customer app has no business telling those apart. | Public | — | 200 → CmsPageContentResponse |

### Coupons

Coupon apply/remove at checkout (SRS 11.10.3, task 73). Both endpoints delegate to `IBookingSummaryService` - the same server-side validation and price recomputation the booking summary/preview already performs - rather than duplicating coupon business logic here. "Apply" and "remove" are the same recompute operation with a code present or absent respectively; there is no server-side checkout session to mutate (see the doc comment on `CouponCode`), so each call is a fresh, complete recomputation.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| POST | `/api/v{version}/coupons/apply` | Validates and applies a coupon code to the given cart context, returning the recomputed summary with the discount shown (SRS 11.10.3 "success message" / "meaningful error message"). | Customer JWT | BookingSummaryRequest | 200 → BookingSummaryResponse |
| POST | `/api/v{version}/coupons/remove` | Removes any applied coupon and returns the recomputed summary at full price (SRS 11.10.3 "coupon removal shall recompute final payable amount"). | Customer JWT | BookingSummaryRequest | 200 → BookingSummaryResponse |

### CustomerAddress

Address book (SRS 11.3). Every action is scoped to the caller's own customer id — never a route/body parameter.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/addresses` | _(no doc comment)_ | Customer JWT | — | 200 → CustomerAddressResponse[] |
| POST | `/api/v{version}/addresses` | _(no doc comment)_ | Customer JWT | UpsertAddressRequest | 201 → CustomerAddressResponse |
| DELETE | `/api/v{version}/addresses/{id}` | _(no doc comment)_ | Customer JWT | — | 204 No Content |
| PUT | `/api/v{version}/addresses/{id}` | _(no doc comment)_ | Customer JWT | UpsertAddressRequest | 200 → CustomerAddressResponse |
| POST | `/api/v{version}/addresses/{id}/default` | _(no doc comment)_ | Customer JWT | — | 204 No Content |

### CustomerProfile

Customer profile (SRS 11.2.3). Like the address book, every action is scoped to the caller's own customer id taken from the JWT — there is no route or body parameter that could name a different customer.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| DELETE | `/api/v{version}/profile` | Permanently deletes the caller's own account (SRS 11.2.3, right to erasure). Booking/payment history is retained under this customer id for financial/legal reasons, but personal fields are anonymized and every active session is revoked immediately - login is impossible from this point on. | Customer JWT | — | 204 No Content |
| GET | `/api/v{version}/profile` | View profile (SRS 11.2.3). | Customer JWT | — | 200 → CustomerProfileResponse |
| PUT | `/api/v{version}/profile` | Edit name and optional profile data (SRS 11.2.3). Mobile/email change through the endpoints below. | Customer JWT | UpdateProfileRequest | 200 → CustomerProfileResponse |
| POST | `/api/v{version}/profile/email` | Step 2: apply the email change once the code verifies (SRS 11.2.3). | Customer JWT | ConfirmEmailChangeRequest | 200 → CustomerProfileResponse |
| POST | `/api/v{version}/profile/email/otp` | Step 1 of an email change: send a code to the new address (SRS 11.2.3). | Customer JWT | RequestEmailChangeOtpRequest | 200 OK |
| POST | `/api/v{version}/profile/mobile` | Step 2: apply the mobile change once the code verifies (SRS 11.2.3). | Customer JWT | ConfirmMobileChangeRequest | 200 → CustomerProfileResponse |
| POST | `/api/v{version}/profile/mobile/otp` | Step 1 of a mobile change: send a code to the new number (SRS 11.2.3). | Customer JWT | RequestMobileChangeOtpRequest | 200 OK |
| GET | `/api/v{version}/profile/preferences` | Read communication preferences (SRS 11.2.3). | Customer JWT | — | 200 → CommunicationPreferencesResponse |
| PUT | `/api/v{version}/profile/preferences` | Replace communication preferences (SRS 11.2.3). | Customer JWT | CommunicationPreferencesRequest | 200 → CommunicationPreferencesResponse |

### DeviceTokens

Push device token registration (SRS 19.1, task 156).

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/device-tokens` | The caller's active registered devices. | Customer JWT | — | 200 → DeviceTokenResponse[] |
| POST | `/api/v{version}/device-tokens` | Registers (or re-registers) the caller's device for push notifications. | Customer JWT | RegisterDeviceTokenRequest | 200 → DeviceTokenResponse |
| DELETE | `/api/v{version}/device-tokens/{id}` | Deactivates a device (e.g. on logout). | Customer JWT | — | 204 No Content |

### FeatureFlags

Public customer-facing feature flags (SRS 12.19 "Feature flags"). No auth - this gates navigation/UI before or without a session, same reasoning as `GeographyController`. Projects the admin-only `FeatureFlagSettings` group down to `CustomerFeatureFlagsResponse` plus the pre-existing coupons flag (`CouponsEnabled`) - never the full admin settings shape, which would leak provider-side flags to an unauthenticated caller.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/feature-flags` | _(no doc comment)_ | Public | — | 200 → CustomerFeatureFlagsResponse |

### Geography

Public geography lookups for location selection (SRS 11.1, 11.4.1). No auth - browsing needs no session.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/geography/cities` | Active cities for a location picker (SRS 11.1 - "change city from homepage"). | Public | — | 200 → CityResponse[] |
| GET | `/api/v{version}/geography/cities/{cityId}/localities` | Localities within a city matching an optional name/pincode search term (SRS 11.4.1), so a customer can resolve a localityId without knowing it - required by the slot and serviceability APIs. | Public | — | 200 → LocalityResponse[] |
| GET | `/api/v{version}/geography/pincodes/{code}` | Resolves a pincode's city/state, so an address form can autofill them once the customer enters a pincode (task 369). | Public | — | 200 → PincodeLookupResponse |

### Landing

The admin-curated home page sections ("New &amp; Trending", "Most Booked Services" and the per-category strips). No auth - the home page is public, same as `CategoriesController`. The hero's category rail is NOT served here: it is location-dependent and already covered by `GET /categories?cityId=`.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/landing/home` | Every curated section in one call; unconfigured sections come back empty rather than absent. | Public | — | 200 → HomeLandingResponse |

### NestlyCoins

Public Nestly Coins program info (docs/NESTLY-COINS.md API SURFACE, task 203). No auth - anyone can see the current earn rate/rules, same as `CategoriesController`; a customer's own coins history is already visible via the existing `WalletController`'s ledger (GUIDELINES #4 - "no new endpoint, this is why reusing Wallet matters").

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/nestly-coins/program` | Current earn rate/rules, for in-app messaging ("earn coins on your next order"). 404 if the program isn't currently active. | Public | — | 200 → NestlyCoinsProgramPublicResponse |

### Payments

Payments (SRS 11.11, 30.1). Order creation/retry is scoped to the caller's own customer id; the webhook is not (see its own doc comment).

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/payments/bookings/{bookingId}` | Payment transaction + attempt history for a booking (SRS 11.11.3, 14.3, task 71). | Customer JWT | — | 200 → PaymentTransactionResponse |
| POST | `/api/v{version}/payments/bookings/{bookingId}/verify` | Actively re-checks a still-pending attempt against the gateway directly, rather than only waiting on its webhook - for a hosted- checkout gateway, a checkout the customer abandoned or cancelled before submitting payment details may never trigger a webhook at all, which otherwise leaves the booking stuck "confirming" until the unrelated 20-minute PaymentPending expiry sweep. Called by customer-web's payment return page once its own short client-side wait for a webhook elapses. A safe no-op (200 with the transaction unchanged) if the attempt is already resolved or the gateway itself still reports it as pending. | Customer JWT | — | 200 → PaymentTransactionResponse |
| POST | `/api/v{version}/payments/orders` | Creates a gateway order for a booking's payment, or (task 70) retries after a prior failure - the same endpoint serves both, since a retry is just "create an order for a booking whose last attempt failed". Idempotent for a booking already awaiting a callback (task 68d). | Customer JWT | CreatePaymentOrderRequest | 201 → PaymentOrderResponse |
| POST | `/api/v{version}/payments/orders/simulate` | Sandbox-only convenience (task 68b): simulates a gateway completing payment for an order the caller owns, deterministically per `SandboxPaymentGateway`'s amount convention, by constructing and signing the same callback `Webhook` handles for real. There is no equivalent endpoint for a real gateway integration - only the gateway itself can decide a payment's outcome. | Customer JWT | SimulatePaymentRequest | 204 No Content |
| POST | `/api/v{version}/payments/webhook` | The gateway's payment callback (SRS 30.1, 11.11.3, tasks 69a-c). Deliberately not [Authorize] - the caller is the payment gateway, not a logged-in customer, and is authenticated by its signature instead (SRS 28.3 "payment callback abuse"). Always idempotent (task 69b): a redelivered callback for an already-resolved attempt is a no-op 200, never re-applied. | Public | PaymentWebhookRequest | 200 OK |
| POST | `/api/v{version}/payments/webhook/payu` | PayU's own callback shape (SRS 30.1, 11.11.3, tasks 69a-c) - separate from `Webhook` because PayU posts `application/x-www-form-urlencoded` fields with PayU-specific names, not this project's generic JSON `PaymentWebhookRequest`. Normalizes into that same request and runs it through the identical verify/idempotent-apply path `Webhook` does - PayU's signature is checked by `IPaymentGateway.BuildCanonicalPayload`/ `VerifyWebhookSignature`, exactly like the sandbox's. Not [Authorize], same reasoning as `Webhook`. | Public | object | 200 OK |

### Pricing

Server-side authoritative price calculation (task 48, SRS 11.9.2). No auth - price is checked before login too.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| POST | `/api/v{version}/pricing/calculate` | Calculates the full price breakdown for a service+city+quantity+add-ons combination. | Public | PriceCalculationRequest | 200 → PriceBreakdownResponse |

### RecurringBookingPlans

Recurring booking plans (PRODUCT-ENHANCEMENTS.md section 2, task 186). Every action is scoped to the caller's own customer id — never a route/body parameter, same convention as `BookingsController`.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/recurring-booking-plans` | Lists the caller's recurring plans, most recently created first. | Customer JWT | — | 200 → RecurringBookingPlanResponse[] |
| POST | `/api/v{version}/recurring-booking-plans` | Creates a recurring plan. Validated end-to-end through the same booking orchestration a one-off booking preview uses (task 58) before anything is persisted. | Customer JWT | CreateRecurringBookingPlanRequest | 201 → RecurringBookingPlanResponse |
| GET | `/api/v{version}/recurring-booking-plans/{id}` | Plan detail. | Customer JWT | — | 200 → RecurringBookingPlanResponse |
| POST | `/api/v{version}/recurring-booking-plans/{id}/auto-charge` | Toggles off-session auto-charge consent on the plan (recurring-booking payment-timing fix). Callable regardless of pause state. | Customer JWT | SetAutoChargeRequest | 200 → RecurringBookingPlanResponse |
| POST | `/api/v{version}/recurring-booking-plans/{id}/bounds` | Edits how many more occurrences this plan will generate - end date and/or occurrence count only (occurrence-count integrity fix). Cannot reduce below what has already been booked. | Customer JWT | SetOccurrenceBoundsRequest | 200 → RecurringBookingPlanResponse |
| POST | `/api/v{version}/recurring-booking-plans/{id}/cancel` | Cancels a plan permanently - a cancelled plan can never be resumed. | Customer JWT | — | 200 → RecurringBookingPlanResponse |
| GET | `/api/v{version}/recurring-booking-plans/{id}/occurrences/history` | What the scheduler has actually recorded so far - booked and skipped occurrences alike. | Customer JWT | — | 200 → OccurrenceHistoryResponse[] |
| GET | `/api/v{version}/recurring-booking-plans/{id}/occurrences/upcoming` | Upcoming (projected, not yet real) occurrence dates for the manage screen. | Customer JWT | — | 200 → UpcomingOccurrenceResponse[] |
| POST | `/api/v{version}/recurring-booking-plans/{id}/pause` | Pauses an active plan - the scheduler will not attempt or skip-and-notify any occurrence while paused. | Customer JWT | — | 200 → RecurringBookingPlanResponse |
| POST | `/api/v{version}/recurring-booking-plans/{id}/resume` | Resumes a paused plan from exactly where it left off. | Customer JWT | — | 200 → RecurringBookingPlanResponse |
| POST | `/api/v{version}/recurring-booking-plans/{id}/skip-visits` | "I'm away until a date": no visit is generated before it; the plan carries on from there. Optionally cancels the few visits already booked before that date. | Customer JWT | SkipVisitsRequest | 200 → RecurringBookingPlanResponse |
| POST | `/api/v{version}/recurring-booking-plans/{id}/slot` | Changes the time-of-day window for every visit generated from now on. Visits already booked keep their slot. | Customer JWT | ChangePlanSlotRequest | 200 → RecurringBookingPlanResponse |

### Referral

Refer &amp; Earn screen (REFERRAL.md, task 168): the caller's own referral code/share link/lifetime stats, and their own referral history. Every action is scoped to the caller's own customer id, same pattern as `WalletController`.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/referral` | Code (lazily generated on first call), share link, and lifetime stats (REFERRAL.md "GET /me/referral"). | Customer JWT | — | 200 → ReferralSummaryResponse |
| GET | `/api/v{version}/referral/history` | This customer's own referrals as referrer, newest first (REFERRAL.md "GET /me/referral/history"). | Customer JWT | — | 200 → ReferralHistoryItemResponse[] |

### Refunds

Refund status per booking (SRS 11.17.2, task 78c). Read-only: a customer can see refund status but cannot self-initiate a refund - that happens through the cancellation flow (Phase 5) or an admin action (Phase 6), both of which will call IRefundService directly once they exist.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/bookings/{bookingId}/refunds` | _(no doc comment)_ | Customer JWT | — | 200 → RefundTransactionResponse[] |

### Reschedules

Customer-initiated booking reschedule (SRS 11.15, 24.6, tasks 82a-d, 83). Every action is scoped to the caller's own customer id, same as `BookingsController` and `CancellationsController`.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| POST | `/api/v{version}/bookings/{bookingId}/reschedule` | Confirms the reschedule and updates the booking's slot immediately (SRS 11.15.3, 24.6). When late-fee collection is switched on (it ships off; otherwise the fee is only recorded), a late reschedule's fee is debited from the customer's wallet in the same step and booked as platform revenue; when the wallet cannot cover it the response is 422 `Reschedule.LateFeeWalletShort` (naming the amount to add) and nothing moves. A professional already on the booking is kept when the new time works for them, otherwise replaced, and is told either way. | Customer JWT | RescheduleBookingRequest | 200 → RescheduleOutcomeResponse |
| GET | `/api/v{version}/bookings/{bookingId}/reschedule/eligibility` | Whether this booking can be rescheduled right now - status, window, and count-limit checks (SRS 11.15.1) - plus the rules in numbers: when rescheduling stops being free, the last moment it is allowed, and, when a reschedule now would be late, the late fee and - when late-fee collection is switched on (it ships off) - whether the customer's wallet covers it (the fee is then debited from the wallet). | Customer JWT | — | 200 → RescheduleEligibilityResponse |
| GET | `/api/v{version}/bookings/{bookingId}/reschedule/slots` | Eligible future slots for this booking's service at a locality/date, for the picker (SRS 11.15.3, 24.6). | Customer JWT | — | 200 → SlotAvailabilityResponse |

### Reviews

Customer review submission for a completed booking (SRS 11.16, 17, 24.8, tasks 85a-c).

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/bookings/{bookingId}/review` | The review already submitted for this booking, if any. | Customer JWT | — | 200 → ReviewResponse |
| POST | `/api/v{version}/bookings/{bookingId}/review` | Submits the booking's one primary review (SRS 24.8). | Customer JWT | SubmitReviewRequest | 201 → ReviewResponse |
| GET | `/api/v{version}/bookings/{bookingId}/review/eligibility` | Whether this booking is eligible for a review right now (SRS 11.16.1, 11.16.3, 24.8 "get eligible review booking"). | Customer JWT | — | 200 → ReviewEligibilityResponse |

### Serviceability

Public serviceability checks (SRS 11.4, 24.4). No auth - browsing must be able to tell a customer a category/service isn't offered at their location before they sign in or add anything (SRS 11.4.3).

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/serviceability/categories/{categoryId}` | Whether a category is serviceable in a city (SRS 12.9.2). | Public | — | 200 → ServiceabilityResponse |
| GET | `/api/v{version}/serviceability/services/{serviceId}` | Whether a service is serviceable at a locality (SRS 12.9.2) - the dimension a customer's selected address naturally carries, resolved to its parent pincode the same way the slot APIs do. | Public | — | 200 → ServiceabilityResponse |

### Services

Public service catalog (tasks 42a/42b, SRS 11.5-11.6). No auth - anyone can browse.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/services` | Services within a category (SRS 11.5.3). | Public | — | 200 → ServiceListItemResponse[] |
| GET | `/api/v{version}/services/{slug}` | Service detail: inclusions/exclusions/add-ons/policies/FAQs (SRS 11.6.1). | Public | — | 200 → ServiceDetailResponse |
| GET | `/api/v{version}/services/{slug}/reviews-summary` | Rating summary and recent reviews for the service detail page (SRS 11.6.1 "Reviews and rating summary"). | Public | — | 200 → ServiceReviewSummaryResponse |

### Slots

Slot availability (task 46, SRS 24.4). No auth - anyone can check availability before login.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/slots` | Available slots for a service, at an address (locality), on a date. | Public | — | 200 → SlotAvailabilityResponse |
| GET | `/api/v{version}/slots/range` | Availability for every date in a range - one call for a whole date strip. | Public | — | 200 → SlotRangeResponse |
| GET | `/api/v{version}/slots/revalidate` | Re-checks a previously offered slot right before booking confirmation. | Public | — | 200 → SlotRevalidationResponse |

### Subscription

Customer-facing subscription flow (PRODUCT-ENHANCEMENTS.md #1, task 181): browse plans, subscribe, cancel, view active subscription. Every action scoped to the caller's own customer id, same pattern as `ReferralController`/`WalletController`.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/subscription/me` | The caller's current live subscription and remaining benefits, or 204 if they have none. | Customer JWT | — | 200 → MySubscriptionResponse |
| GET | `/api/v{version}/subscription/plans` | Every plan currently open to new subscribers. | Customer JWT | — | 200 → SubscriptionPlanBrowseResponse[] |
| POST | `/api/v{version}/subscription/subscribe` | _(no doc comment)_ | Customer JWT | SubscribeRequest | 201 → MySubscriptionResponse |
| POST | `/api/v{version}/subscription/{subscriptionId}/cancel` | _(no doc comment)_ | Customer JWT | — | 204 No Content |

### SupportTickets

Customer support tickets (SRS 11.18, 16, 24.8, tasks 86a-d) - both booking-linked and generic (task 86d): `BookingId` carries the link when one applies. See `BookingSupportTicketsController` for the booking-scoped listing view of the same tickets.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/support-tickets` | Lists all of the caller's tickets, newest first (SRS 24.8, task 86b). | Customer JWT | — | 200 → SupportTicketSummaryResponse[] |
| POST | `/api/v{version}/support-tickets` | Raises a new ticket (SRS 11.18.1-2, 24.8, task 86a). | Customer JWT | CreateSupportTicketRequest | 201 → SupportTicketDetailResponse |
| GET | `/api/v{version}/support-tickets/{id}` | Ticket detail with its full comment thread (SRS 11.18.3, 24.8, task 86c). | Customer JWT | — | 200 → SupportTicketDetailResponse |
| POST | `/api/v{version}/support-tickets/{id}/comments` | Appends a customer follow-up to the ticket's thread (SRS 11.18.3, 12.14.2). | Customer JWT | AddSupportTicketCommentRequest | 200 → SupportTicketDetailResponse |

### Wallet

Wallet balance and ledger (SRS 11.17.1, 14.5, task 74c), and adding money to it. Every action is scoped to the caller's own customer id.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/wallet/balance` | _(no doc comment)_ | Customer JWT | — | 200 → WalletBalanceResponse |
| GET | `/api/v{version}/wallet/ledger` | _(no doc comment)_ | Customer JWT | — | 200 → WalletLedgerEntryResponse[] |
| GET | `/api/v{version}/wallet/top-up/config` | Whether adding money is switched on, and its limits - what the "Add money" screen reads before offering anything. | Customer JWT | — | 200 → WalletTopUpConfigResponse |
| POST | `/api/v{version}/wallet/top-ups` | Starts adding money: creates the gateway order and returns where to send the customer. 422 when top-ups are off or a limit is hit. | Customer JWT | CreateWalletTopUpRequest | 201 → WalletTopUpOrderResponse |
| GET | `/api/v{version}/wallet/top-ups/{id}` | _(no doc comment)_ | Customer JWT | — | 200 → WalletTopUpResponse |
| POST | `/api/v{version}/wallet/top-ups/{id}/simulate` | Sandbox-only: completes a top-up the way the gateway's callback would. 422 when a real gateway is configured. | Customer JWT | — | 200 → WalletTopUpResponse |
| POST | `/api/v{version}/wallet/top-ups/{id}/verify` | Asks the gateway directly how a still-pending top-up ended - for a checkout the customer abandoned or whose webhook never arrived. Called by the return page once its own short wait elapses. A safe no-op once resolved. | Customer JWT | — | 200 → WalletTopUpResponse |

## ADMIN-API (internal ops console)

### AdminAuth

Admin panel authentication (SRS 12.1, tasks 95a-95g).

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| POST | `/api/v{version}/admin/auth/login` | Admin login (SRS 12.1.1): email + password, JWT issuance, lockout and login audit. Throttled per-IP by the "login" rate-limit policy (task 95c); per-account throttling/lockout (95d) happens inside `IAdminLoginService` itself. | Public | AdminLoginRequest | 200 → AdminLoginResponse |
| POST | `/api/v{version}/admin/auth/logout` | Invalidate a session's refresh token (SRS 12.1.2: logout invalidates the active session). | Public | LogoutRequest | 204 No Content |
| POST | `/api/v{version}/admin/auth/refresh` | Exchange a still-valid refresh token for a new access+refresh pair (rotation, SRS 28.3). | Public | RefreshTokenRequest | 200 → AdminLoginResponse |
| POST | `/api/v{version}/admin/auth/unlock/{adminUserId}` | Administrative unlock of a locked account (task 95d's unlock path). A lockout also clears itself automatically once its window elapses; this only clears it sooner. Gated behind "settings.write" (task 96b/96c) - unlocking someone else's account is admin-user administration (SRS 12.2.1), the same module as assigning roles or deactivating an account. Only Super Admin holds this permission in the seeded matrix (task 96a). | Admin JWT + permission `settings.write` | — | 204 No Content |

### AdminNestlyCoins

Admin get/update for the per-audience Nestly Coins program config, plus the coins-issued/clawed-back report (docs/NESTLY-COINS.md API SURFACE, task 202). Read-only actions require "nestly-coins.read"; mutating actions require "nestly-coins.write" - same per-action split as `ReferralProgramConfigController`.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/nestly-coins/config/{audience}` | The program config for one audience (task 200/202) - 404 if that audience has never been configured. | Admin JWT + permission `nestly-coins.read` | — | 200 → NestlyCoinsProgramConfigResponse |
| PUT | `/api/v{version}/admin/nestly-coins/config/{audience}` | Creates or updates the program config for one audience - this is the only way an audience's coins program is ever activated. | Admin JWT + permission `nestly-coins.write` | NestlyCoinsProgramConfigUpsertRequest | 200 → NestlyCoinsProgramConfigResponse |
| GET | `/api/v{version}/admin/nestly-coins/reports/issued` | Coins issued vs. clawed back for one audience over a date range (mirrors Referral's funnel/cost report). | Admin JWT + permission `nestly-coins.read` | — | 200 → NestlyCoinsReportResponse |

### AdminRoles

Role CRUD and permission-matrix editing (SRS 12.2.2, 12.2.3, task 313): AdminPermissionCatalog's nine seeded roles and their grants used to be compile-time constants - changing who could do what required a code change and redeploy. This controller makes `AdminRole` and its permission grants genuinely writable at runtime. Gated behind "settings.read"/"settings.write" - the same two policies `AdminUsersController` already uses for admin-user administration (nothing else in the seeded permission matrix grants Settings besides Super Admin). Every permission-granting write is subject to a self-escalation guard (see `AdminRoleManagementService`'s doc comment) - a 403 from any action below most likely means that guard rejected the request, not a missing policy grant (the [Authorize] attribute would already have produced the 403 for that).

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/admin-roles` | Every role with its currently granted permission codes. | Admin JWT + permission `settings.read` | — | 200 → AdminRoleDetailResponse[] |
| POST | `/api/v{version}/admin/admin-roles` | Creates a new role with an initial permission-matrix row (SRS 12.2.2 "roles are configurable"). | Admin JWT + permission `settings.write` | CreateAdminRoleRequest | 201 → AdminRoleDetailResponse |
| GET | `/api/v{version}/admin/admin-roles/permissions` | Every grantable permission code (module x action), for the permission-matrix editor's grid. | Admin JWT + permission `settings.read` | — | 200 → AdminPermissionCatalogEntryResponse[] |
| GET | `/api/v{version}/admin/admin-roles/{roleId}` | Role detail, including its current permission-matrix row. | Admin JWT + permission `settings.read` | — | 200 → AdminRoleDetailResponse |
| PUT | `/api/v{version}/admin/admin-roles/{roleId}` | Renames a role / edits its description - permissions are edited separately below. | Admin JWT + permission `settings.write` | UpdateAdminRoleRequest | 200 → AdminRoleDetailResponse |
| PUT | `/api/v{version}/admin/admin-roles/{roleId}/permissions` | Replaces a role's entire permission-matrix row (SRS 12.2.3) - a full-replace with the complete grid state, not an add/remove delta. Subject to the self-escalation guard: rejected with 403 if it would grant the role any code the caller does not already hold. | Admin JWT + permission `settings.write` | SetAdminRolePermissionsRequest | 200 → AdminRoleDetailResponse |

### AdminUsers

Admin user management (SRS 12.2.1, tasks 97a-97d): CRUD over admin accounts, role assignment, activate/deactivate, and admin-initiated password reset - one Super Admin managing another back-office operator's account. Gated behind "settings.read"/"settings.write" - the same two policies `Unlock` already uses for administering another admin's account (nothing else in the seeded permission matrix grants Settings besides Super Admin, per `AdminPermissionCatalog`).

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/admin-users` | Search/filter admin accounts (task 97a "list"). | Admin JWT + permission `settings.read` | — | 200 → AdminUserSearchResponse |
| POST | `/api/v{version}/admin/admin-users` | Provisions a new admin account (SRS 12.2.1 "Create admin users", task 97a). | Admin JWT + permission `settings.write` | CreateAdminUserRequest | 201 → AdminUserDetailResponse |
| GET | `/api/v{version}/admin/admin-users/roles` | Every seeded/created role, for the role-assignment picker (task 97b). | Admin JWT + permission `settings.read` | — | 200 → AdminRoleSummaryResponse[] |
| GET | `/api/v{version}/admin/admin-users/{adminUserId}` | Admin account detail (task 97a "get"). | Admin JWT + permission `settings.read` | — | 200 → AdminUserDetailResponse |
| PUT | `/api/v{version}/admin/admin-users/{adminUserId}` | Edits an admin account's profile - email and name (SRS 12.2.1 "Edit admin user profile", task 97a). | Admin JWT + permission `settings.write` | UpdateAdminUserRequest | 200 → AdminUserDetailResponse |
| POST | `/api/v{version}/admin/admin-users/{adminUserId}/activate` | Activates a deactivated admin account (SRS 12.2.1 "Activate/deactivate users", task 97c). | Admin JWT + permission `settings.write` | — | 200 → AdminUserDetailResponse |
| POST | `/api/v{version}/admin/admin-users/{adminUserId}/deactivate` | Deactivates an admin account (SRS 12.2.1 "Activate/deactivate users", task 97c) - distinct from clearing a login lockout (`Unlock`, task 95d): this permanently disables login until reactivated, rather than clearing a temporary, self-resolving failed-attempt lockout. | Admin JWT + permission `settings.write` | — | 200 → AdminUserDetailResponse |
| POST | `/api/v{version}/admin/admin-users/{adminUserId}/reset-password` | Admin-initiated password reset (SRS 12.2.1 "Reset password / send reset link", task 97d): generates a temporary password and returns it once for the Super Admin to relay to the account owner out of band. | Admin JWT + permission `settings.write` | — | 200 → ResetAdminPasswordResponse |
| PUT | `/api/v{version}/admin/admin-users/{adminUserId}/role` | Assigns or clears an admin account's role (SRS 12.2.1 "Assign role(s)", task 97b). | Admin JWT + permission `settings.write` | AssignAdminRoleRequest | 200 → AdminUserDetailResponse |

### AmcContracts

Admin visibility into AMC contracts (docs/AMC.md): search/filter by status and customer, contract detail, and the renewal-pipeline report - mirrors the shape `RecurringPlansController` already established for its own search-plus-report pair. RBAC: gated behind the existing "bookings.read", with no new `AdminModules` entry - docs/AMC.md's RBAC ADDITIONS section applies the exact same reasoning `RecurringPlansController`'s doc comment gives for recurring plans: an AMC contract is a way bookings come into existence, and every row this controller reports on is either a `CustomerAmcContract` or a `Booking` carrying its id, both already readable in strictly more detail through `BookingsController` to any admin holding "bookings.read". A new permission gating a strictly weaker view of data already readable is an inconvenience, not a boundary.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/amc-contracts` | Every AMC contract on the platform, newest first, filterable by status and searchable by customer name/mobile. | Admin JWT + permission `bookings.read` | — | 200 → AmcContractAdminSearchResponse |
| GET | `/api/v{version}/admin/amc-contracts/renewal-report` | Contract status counts plus contracts expiring or exhausted within a horizon (defaults to the next 30 days) - the "needs a renewal conversation" list docs/AMC.md's renewal pipeline exists for. | Admin JWT + permission `bookings.read` | — | 200 → AmcRenewalReportResponse |
| GET | `/api/v{version}/admin/amc-contracts/{id}` | _(no doc comment)_ | Admin JWT + permission `bookings.read` | — | 200 → AmcContractAdminListItemResponse |

### AmcPlans

Admin CRUD for the AMC plan catalog (docs/AMC.md): category, price, term, visits included. Mirrors `SubscriptionPlansController` exactly. Read-only actions require "subscription.read"; mutating actions require "subscription.write" - docs/AMC.md's RBAC ADDITIONS section: an AMC contract is a commercial record adjacent to Subscription, not a new vertical, so no new `AdminModules` entry is added.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/amc-plans` | _(no doc comment)_ | Admin JWT + permission `subscription.read` | — | 200 → AmcPlanAdminResponse[] |
| POST | `/api/v{version}/admin/amc-plans` | _(no doc comment)_ | Admin JWT + permission `subscription.write` | AmcPlanCreateRequest | 201 → AmcPlanAdminResponse |
| GET | `/api/v{version}/admin/amc-plans/{id}` | _(no doc comment)_ | Admin JWT + permission `subscription.read` | — | 200 → AmcPlanAdminResponse |
| PUT | `/api/v{version}/admin/amc-plans/{id}` | _(no doc comment)_ | Admin JWT + permission `subscription.write` | AmcPlanUpdateRequest | 200 → AmcPlanAdminResponse |
| POST | `/api/v{version}/admin/amc-plans/{id}/activate` | _(no doc comment)_ | Admin JWT + permission `subscription.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/amc-plans/{id}/deactivate` | _(no doc comment)_ | Admin JWT + permission `subscription.write` | — | 204 No Content |

### AuditLog

Audit log viewer API (task 130, SRS 21): a filterable read over the existing audit trail written by `AdminLoginService` (task 95g's login audit) and `PermissionAuthorizationHandler` (task 96d's permission-check audit) — no second audit table is introduced here. Gated behind "audit.read", the permission code `AdminPermissionCatalog` defines for `Audit`. Per the seeded role matrix (task 96a), only Super Admin holds it today - even Finance Admin, which lists the audit module in its notes, is granted Read only through that same catalog, matching what is enforced here.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/audit-log` | Searches the audit trail (SRS 21.1-21.2), filterable by actor, date range, entity/action, and outcome (task 130). Newest first, paginated. | Admin JWT + permission `audit.read` | — | 200 → PagedAuditLogResponse |

### Banners

Admin banner management (SRS 12.16.1 "Home banners / Category banners / Promotional blocks", tasks 124b/124c/124d/124f): CRUD plus draft/publish workflow, media asset reference, category-scoped placement, ordering, and an optional publish window. Read-only actions require "cms.read"; every mutating action requires "cms.write" (task 96b/96c), matching `CouponsController`'s per-action policy split.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/cms/banners` | Search/filter banners (SRS 12.16.1). | Admin JWT + permission `cms.read` | — | 200 → BannerAdminSearchResponse |
| POST | `/api/v{version}/admin/cms/banners` | Creates a banner. Always starts as Draft - see `Publish`. | Admin JWT + permission `cms.write` | BannerCreateRequest | 201 → BannerResponse |
| GET | `/api/v{version}/admin/cms/banners/categories` | Active categories, for the banner form's "category" picker when placement is CategoryPage. | Admin JWT + permission `cms.read` | — | 200 → CategoryLookupResponse[] |
| GET | `/api/v{version}/admin/cms/banners/media` | The media library, for the banner form's asset picker (task 124e). | Admin JWT + permission `cms.read` | — | 200 → CmsMediaResponse[] |
| GET | `/api/v{version}/admin/cms/banners/{id}` | Banner detail (SRS 12.16.1). | Admin JWT + permission `cms.read` | — | 200 → BannerResponse |
| PUT | `/api/v{version}/admin/cms/banners/{id}` | Edits every mutable banner field (SRS 12.16.1). | Admin JWT + permission `cms.write` | BannerUpdateRequest | 200 → BannerResponse |
| POST | `/api/v{version}/admin/cms/banners/{id}/publish` | Publishes a draft banner, or re-publishes one already live (SRS 12.16.2 "draft/publish status"). | Admin JWT + permission `cms.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/cms/banners/{id}/unpublish` | Pulls a banner back to draft without deleting it. | Admin JWT + permission `cms.write` | — | 204 No Content |

### BookingConflicts

Task 321/322: standing provider double-bookings - the bookings one provider is live on at overlapping times - and the count behind the dashboard badge. <para> Read-only by design. Resolution is a reassignment, and a reassignment already has exactly one entry point: `POST /admin/bookings/{bookingId}/assign-provider`. Adding a "resolve conflict" mutation here would be a second write path to the same state, with its own copy of the validation, the supersede rules and the task 288 conflict check - the precise duplication `IBookingProviderAssignmentService`'s doc comment exists to prevent. The dashboard therefore reads from here and writes through `BookingsController`, which also means every conflict resolution lands in the audit trail as the ordinary assignment it is. </para> <para> RBAC: the existing "bookings.read", no new `AdminModules` entry - the same reasoning `RecurringPlansController` records at length. Every row exposed here is a booking the caller can already open individually through `BookingsController`, and in more detail; a new permission gating a strictly weaker view of already-readable data is not a boundary. </para>

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/booking-conflicts` | Conflict groups, soonest first. Both dates are optional: `fromDate` defaults to today, because a clash in the past can no longer be resolved by moving anyone and would only pad the list an admin is working through. Pass an explicit earlier `fromDate` to audit historical damage. | Admin JWT + permission `bookings.read` | — | 200 → BookingAssignmentConflictSearchResponse |
| GET | `/api/v{version}/admin/booking-conflicts/count` | Number of outstanding conflict groups from `fromDate` (default today) onward. | Admin JWT + permission `bookings.read` | — | 200 → BookingConflictCountResponse |

### Bookings

Admin booking management (SRS 12.11, 12.13.2-3; tasks 115a-117c): filterable search, full detail/timeline, general operational status updates, and the cancel/reschedule/refund actions. Every mutating action is delegated to `IBookingManagementService`, which in turn composes the existing cancellation/reschedule/refund domain services (tasks 80c, 82d, 75d) rather than reimplementing their policy math. Full and partial refunds are both gated behind "bookings.write" rather than two separate tiers - SRS 12.13.2 does not call for a stricter permission on a full refund than a partial one, and `AdminPermissionCatalog`'s own doc comment explicitly treats splitting a module's Write tier further as a deliberate, not-yet-needed extension (YAGNI) until a controller actually requires the distinction - inventing a new "bookings.refund.full" code here would be exactly that speculative split, and the task brief instructs not to invent new permission codes.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/bookings` | Filterable, paginated booking search (SRS 12.11.1, task 115a). | Admin JWT + permission `bookings.read` | — | 200 → AdminBookingSearchResponse |
| GET | `/api/v{version}/admin/bookings/auto-charge/pending` | The admin auto-charge queue: every recurring occurrence still awaiting its off-session charge (Payment Management UX pass gap - previously zero admin visibility into RecurringOccurrenceAutoChargeJob at all). A static route ahead of `GetDetail`'s `{bookingId:guid}` route, same non-clash reasoning as `ListUnassignedAtRisk`. | Admin JWT + permission `bookings.read` | — | 200 → AdminAutoChargeCandidateResponse[] |
| GET | `/api/v{version}/admin/bookings/completion-proofs/pending` | The admin completion-proof review queue: every proof still awaiting a verdict, across every booking, oldest submission first (Order/Booking Management UX pass gap - previously reachable only by opening one InProgress booking at a time). A static route ahead of `GetDetail`'s `{bookingId:guid}` route, same non-clash reasoning as `ListUnassignedAtRisk`. | Admin JWT + permission `bookings.read` | — | 200 → BookingCompletionProofQueueItemResponse[] |
| GET | `/api/v{version}/admin/bookings/fulfilment-board` | Row "Fulfilment control room", docs/OPEN-FIXES-FEATURES.csv: every operationally live booking on `date` (defaults to today), flat - admin-web buckets these into status columns itself, see `GetFulfilmentBoardAsync`. A static route ahead of `GetDetail`'s `{bookingId:guid}` route, same non-clash reasoning as `ListUnassignedAtRisk`. | Admin JWT + permission `bookings.read` | — | 200 → AdminFulfilmentBoardResponse |
| GET | `/api/v{version}/admin/bookings/reschedule-cities` | Active cities for the reschedule panel's locality picker (row 26, docs/OPEN-FIXES-FEATURES.csv) - the same `IGeographyQueryService` the customer booking flow's city selector uses, so this never re-derives its own city list. | Admin JWT + permission `bookings.read` | — | 200 → CityResponse[] |
| GET | `/api/v{version}/admin/bookings/reschedule-localities` | Localities matching a name/pincode search within a city (row 26, docs/OPEN-FIXES-FEATURES.csv) - resolves the `LocalityId` the reschedule action needs, via the same `IGeographyQueryService` the customer booking flow's `LocalitySelector` calls, instead of asking the admin to paste a raw UUID. | Admin JWT + permission `bookings.read` | — | 200 → LocalityResponse[] |
| GET | `/api/v{version}/admin/bookings/unassigned-at-risk` | Row "Unassigned and at-risk queue", docs/OPEN-FIXES-FEATURES.csv: paid bookings that are assignable but have no live provider on them yet, soonest slot first so the most at-risk booking surfaces at the top - see `ListUnassignedAtRiskAsync`. A static route ahead of `GetDetail`'s `{bookingId:guid}` route would ordinarily risk a clash, but the guid constraint means "unassigned-at-risk" never matches it regardless of declaration order. | Admin JWT + permission `bookings.read` | — | 200 → AdminUnassignedAtRiskBookingSearchResponse |
| GET | `/api/v{version}/admin/bookings/{bookingId}` | Full detail: snapshots, status timeline, payment, cancellation/reschedule/refund history (SRS 12.11.2, tasks 115b-115c). | Admin JWT + permission `bookings.read` | — | 200 → AdminBookingDetailResponse |
| POST | `/api/v{version}/admin/bookings/{bookingId}/assign-provider` | Assigns (or reassigns) a provider to a booking (task 147, PROVIDER.md OPEN DECISIONS #1 - manual admin-driven assignment). Gated behind "bookings.write" - the existing permission code, per PROVIDER.md's SCOPE BOUNDARY this is Booking-domain behaviour, not a separate Provider-module permission. Returns 409 with "BookingProviderAssignment.ProviderDoubleBooked" when the provider is already on an overlapping job (task 288) - unlike their advisory capacity limits, that one is a hard stop even for an admin. | Admin JWT + permission `bookings.write` | AssignProviderRequest | 200 → BookingProviderAssignmentResponse |
| GET | `/api/v{version}/admin/bookings/{bookingId}/assignments` | Full provider-assignment history for a booking, newest first (task 147/159) - shows prior rejections/reassignments leading to the current state. | Admin JWT + permission `bookings.read` | — | 200 → BookingProviderAssignmentResponse[] |
| POST | `/api/v{version}/admin/bookings/{bookingId}/auto-charge/cancel` | Stops the automatic sweep from ever attempting this occurrence again and notifies the customer to pay manually - see `CancelAutoChargeRetries`. | Admin JWT + permission `bookings.write` | — | 200 → AdminBookingDetailResponse |
| POST | `/api/v{version}/admin/bookings/{bookingId}/auto-charge/retry` | Forces an immediate off-session charge attempt for one recurring occurrence, bypassing the backoff-timing gate - see `ForceAttemptAsync`. | Admin JWT + permission `bookings.write` | — | 200 → AdminBookingDetailResponse |
| POST | `/api/v{version}/admin/bookings/{bookingId}/cancel` | Admin-initiated cancellation (SRS 12.11.3, task 117a) via the existing cancellation domain service (task 80c). | Admin JWT + permission `bookings.write` | AdminCancelBookingRequest | 200 → AdminBookingDetailResponse |
| GET | `/api/v{version}/admin/bookings/{bookingId}/completion-proof` | Completion proof (photos + checklist) for a booking, if any (task 198, SRS 12.11.2 dispute review). | Admin JWT + permission `bookings.read` | — | 200 → BookingCompletionProofResponse |
| POST | `/api/v{version}/admin/bookings/{bookingId}/completion-proof/approve` | Approves the completion proof and, as the direct consequence, moves the booking to Completed - the only path Completed is now reachable by (see `BookingManagementService.DisallowedGenericTransitionTargets`). | Admin JWT + permission `bookings.write` | — | 200 → AdminBookingDetailResponse |
| POST | `/api/v{version}/admin/bookings/{bookingId}/completion-proof/reject` | Rejects the completion proof with a required reason; the booking stays InProgress for the provider to finish and resubmit. | Admin JWT + permission `bookings.write` | RejectCompletionProofRequest | 200 → AdminBookingDetailResponse |
| GET | `/api/v{version}/admin/bookings/{bookingId}/eligible-providers` | Candidate providers for manually assigning this booking - matched by service area (pincode/city) and skill (service/category), ranked by specificity then current load. Read-only, to inform the admin's own choice before calling `AssignProvider`: no auto-dispatch (PROVIDER.md OPEN DECISIONS #1). | Admin JWT + permission `bookings.read` | — | 200 → EligibleProviderResponse[] |
| POST | `/api/v{version}/admin/bookings/{bookingId}/manual-payment` | Records a manual/offline payment (cash, UPI, bank transfer) against a booking Awaiting Payment (row 25, docs/OPEN-FIXES-FEATURES.csv) - transitions the booking exactly like a successful gateway payment via `RecordManualPaymentAsync`. | Admin JWT + permission `bookings.write` | AdminManualPaymentRequest | 200 → AdminBookingDetailResponse |
| POST | `/api/v{version}/admin/bookings/{bookingId}/refund` | Full or partial refund with audit (SRS 12.11.3, 12.13.2-3, task 117c) via the existing refund domain service (task 75d). | Admin JWT + permission `bookings.write` | AdminRefundRequest | 200 → AdminBookingDetailResponse |
| POST | `/api/v{version}/admin/bookings/{bookingId}/reject-assignment` | Rejects the booking's current outstanding assignment (task 159) - clears the assigned provider and returns the booking to AwaitingFulfilment so it needs manual reassignment (no auto-match, PROVIDER.md OPEN DECISIONS #1). | Admin JWT + permission `bookings.write` | RejectAssignmentRequest | 200 → BookingProviderAssignmentResponse |
| POST | `/api/v{version}/admin/bookings/{bookingId}/reschedule` | Admin-initiated reschedule (SRS 12.11.3, task 117b) via the existing reschedule domain service (task 82d). | Admin JWT + permission `bookings.write` | AdminRescheduleBookingRequest | 200 → AdminBookingDetailResponse |
| GET | `/api/v{version}/admin/bookings/{bookingId}/reschedule-slots` | Available slot windows for this booking's service, at a candidate locality, on a candidate date (row 26, docs/OPEN-FIXES-FEATURES.csv) - resolves the `SlotWindowId` the reschedule action needs, via the same `ISlotAvailabilityService` the customer booking flow's `SlotPicker` calls, instead of asking the admin to paste a raw UUID. | Admin JWT + permission `bookings.read` | — | 200 → SlotAvailabilityResponse |
| POST | `/api/v{version}/admin/bookings/{bookingId}/status` | General operational status transition (SRS 12.11.3, task 115d) - see `AdminBookingStatusUpdateRequest` for the restricted target-status set. | Admin JWT + permission `bookings.write` | AdminBookingStatusUpdateRequest | 200 → AdminBookingDetailResponse |
| GET | `/api/v{version}/admin/bookings/{bookingId}/tracking` | Live tracking snapshot for the admin ops view (task 284) - same shape task 275 built for the customer screen, minus the ownership check. | Admin JWT + permission `bookings.read` | — | 200 → BookingTrackingResponse |

### Categories

Admin category management (SRS 12.5, tasks 103a-103e): CRUD, display ordering, media (icon/banner) and SEO fields, activation and featuring. Gated behind the "catalog" permission module (SRS 12.5-12.7 share one module - see `AdminPermissionCatalog`).

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/catalog/categories` | _(no doc comment)_ | Admin JWT + permission `catalog.read` | — | 200 → CategoryResponse[] |
| POST | `/api/v{version}/admin/catalog/categories` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | CategoryCreateRequest | 200 → CategoryResponse |
| GET | `/api/v{version}/admin/catalog/categories/{id}` | _(no doc comment)_ | Admin JWT + permission `catalog.read` | — | 200 → CategoryResponse |
| PUT | `/api/v{version}/admin/catalog/categories/{id}` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | CategoryUpdateRequest | 200 → CategoryResponse |
| POST | `/api/v{version}/admin/catalog/categories/{id}/activate` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | — | 204 No Content |
| GET | `/api/v{version}/admin/catalog/categories/{id}/children` | _(no doc comment)_ | Admin JWT + permission `catalog.read` | — | 200 → CategoryResponse[] |
| POST | `/api/v{version}/admin/catalog/categories/{id}/deactivate` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/catalog/categories/{id}/feature` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/catalog/categories/{id}/unfeature` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | — | 204 No Content |

### CategoryGroups

Admin management of category groups - optional section headers for a subset of a parent category's subcategories (e.g. "Large appliances" under "AC &amp; Appliance Repair"): CRUD, activation. Flat, top-level route - same shape as `ServiceGroupsController` - because category groups get their own admin-web tab rather than living only under one category's edit page. Gated behind the "catalog" permission module, same as `CategoriesController` (SRS 12.5-12.7 share one module).

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/catalog/category-groups` | _(no doc comment)_ | Admin JWT + permission `catalog.read` | — | 200 → CategoryGroupAdminResponse[] |
| POST | `/api/v{version}/admin/catalog/category-groups` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | CategoryGroupCreateRequest | 200 → CategoryGroupAdminResponse |
| DELETE | `/api/v{version}/admin/catalog/category-groups/{id}` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | — | 204 No Content |
| GET | `/api/v{version}/admin/catalog/category-groups/{id}` | _(no doc comment)_ | Admin JWT + permission `catalog.read` | — | 200 → CategoryGroupAdminResponse |
| PUT | `/api/v{version}/admin/catalog/category-groups/{id}` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | CategoryGroupUpdateRequest | 200 → CategoryGroupAdminResponse |
| POST | `/api/v{version}/admin/catalog/category-groups/{id}/activate` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/catalog/category-groups/{id}/deactivate` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | — | 204 No Content |

### Chat

Admin support-console reply view (PRODUCT-ENHANCEMENTS.md IN-APP CHAT, task 193) - view and reply on any booking/support-ticket thread, not scoped to a single customer the way ConsumerApi's ChatController is. Every action here (including `Reply`) is gated behind "chat.read" alone, not the read/write split every other admin controller in this codebase uses (e.g. SupportTicketsController's support.read vs support.write). See AdminModules.Chat's doc comment for why: this catalog still generates chat.write mechanically, but PRODUCT-ENHANCEMENTS.md's RBAC ADDITIONS section is explicit that Chat has exactly one tier ("View"), and no role is ever granted chat.write - gating Reply behind a permission nothing holds would make the feature unreachable, not safely locked down.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/chat/threads` | Every thread across every customer, most recent first - the inbox an admin opens before knowing which specific thread they want (task 193 follow-up). | Admin JWT + permission `chat.read` | — | 200 → AdminChatThreadListResponse |
| POST | `/api/v{version}/admin/chat/threads` | Returns (or opens) the thread for a booking/support-ticket context - an admin may proactively message a customer, not only reply. | Admin JWT + permission `chat.read` | GetOrCreateChatThreadRequest | 200 → ChatThreadResponse |
| GET | `/api/v{version}/admin/chat/threads/{threadId}/messages` | _(no doc comment)_ | Admin JWT + permission `chat.read` | — | 200 → ChatMessagePageResult |
| POST | `/api/v{version}/admin/chat/threads/{threadId}/messages` | _(no doc comment)_ | Admin JWT + permission `chat.read` | SendChatMessageRequest | 201 → ChatMessageResponse |
| POST | `/api/v{version}/admin/chat/threads/{threadId}/read` | _(no doc comment)_ | Admin JWT + permission `chat.read` | — | 204 No Content |

### CmsFaqs

Admin site-level FAQ management (SRS 12.16.1 "FAQ entries", tasks 124c/124d/124f): CRUD plus draft/publish workflow, sort order, placement, and an optional publish window. Distinct from per-service FAQ management (task 40e's `ServiceFaq`, exposed via `ServicesController`) - see `CmsFaq`'s doc comment. Read-only actions require "cms.read"; every mutating action requires "cms.write" (task 96b/96c), matching `CouponsController`'s per-action policy split.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/cms/faqs` | Search/filter FAQ entries (SRS 12.16.1). | Admin JWT + permission `cms.read` | — | 200 → CmsFaqAdminSearchResponse |
| POST | `/api/v{version}/admin/cms/faqs` | Creates a FAQ entry. Always starts as Draft - see `Publish`. | Admin JWT + permission `cms.write` | CmsFaqCreateRequest | 201 → CmsFaqResponse |
| GET | `/api/v{version}/admin/cms/faqs/{id}` | FAQ detail (SRS 12.16.1). | Admin JWT + permission `cms.read` | — | 200 → CmsFaqResponse |
| PUT | `/api/v{version}/admin/cms/faqs/{id}` | Edits every mutable FAQ field (SRS 12.16.1). | Admin JWT + permission `cms.write` | CmsFaqUpdateRequest | 200 → CmsFaqResponse |
| POST | `/api/v{version}/admin/cms/faqs/{id}/publish` | Publishes a draft FAQ entry, or re-publishes one already live (SRS 12.16.2 "draft/publish status"). | Admin JWT + permission `cms.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/cms/faqs/{id}/unpublish` | Pulls a FAQ entry back to draft without deleting it. | Admin JWT + permission `cms.write` | — | 204 No Content |

### CmsMedia

Admin CMS media library management (SRS 12.16.2 "media upload support", task 124e): CRUD over the asset library `Banner` draws its image from, plus `Upload` (task 314) - a genuine file upload via `IFileStorageService`, the same abstraction provider-web's job-completion photos use. `Create`/`Update` still accept a hand-typed URL directly (an already-hosted external image, or a CDN asset uploaded outside this app) - the two are not mutually exclusive, every `CmsMedia` row is just a URL either way. Read-only actions require "cms.read"; every mutating action requires "cms.write" (task 96b/96c), matching `CouponsController`'s per-action policy split.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/cms/media` | Every media asset, newest first (task 124e). | Admin JWT + permission `cms.read` | — | 200 → CmsMediaResponse[] |
| POST | `/api/v{version}/admin/cms/media` | Registers a new media asset by URL (task 124e). | Admin JWT + permission `cms.write` | CmsMediaCreateRequest | 201 → CmsMediaResponse |
| POST | `/api/v{version}/admin/cms/media/upload` | Task 314: registers a new media asset from an uploaded file instead of a hand-typed URL. Validated here rather than via a FluentValidation record validator since the payload is multipart, not JSON - content-type is checked against an image allowlist and size is capped before anything is read into memory or written to disk, mirroring provider-api's `JobsController.UploadCompletionPhoto` exactly. | Admin JWT + permission `cms.write` | object | 201 → CmsMediaResponse |
| DELETE | `/api/v{version}/admin/cms/media/{id}` | Deletes a media asset. Fails with a conflict if a banner still references it. | Admin JWT + permission `cms.write` | — | 204 No Content |
| GET | `/api/v{version}/admin/cms/media/{id}` | Media asset detail. | Admin JWT + permission `cms.read` | — | 200 → CmsMediaResponse |
| PUT | `/api/v{version}/admin/cms/media/{id}` | Edits a media asset's URL/alt text. | Admin JWT + permission `cms.write` | CmsMediaUpdateRequest | 200 → CmsMediaResponse |

### CmsPages

Admin static page management (SRS 12.16.1 "About / policy pages", "SEO content for key public pages", tasks 124a/124c/124d/124f): CRUD plus draft/publish workflow, optional publish window, and placement. Read-only actions require "cms.read"; every mutating action requires "cms.write" (task 96b/96c) - a role granted Write always also holds Read (see `AdminPermissionCatalog`), so the two are applied per-action rather than a single class-level policy, matching `CouponsController`.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/cms/pages` | Search/filter pages (SRS 12.16.1). | Admin JWT + permission `cms.read` | — | 200 → CmsPageAdminSearchResponse |
| POST | `/api/v{version}/admin/cms/pages` | Creates a page. Always starts as Draft - see `Publish`. | Admin JWT + permission `cms.write` | CmsPageCreateRequest | 201 → CmsPageResponse |
| GET | `/api/v{version}/admin/cms/pages/{id}` | Page detail (SRS 12.16.1). | Admin JWT + permission `cms.read` | — | 200 → CmsPageResponse |
| PUT | `/api/v{version}/admin/cms/pages/{id}` | Edits every mutable page field (SRS 12.16.1). | Admin JWT + permission `cms.write` | CmsPageUpdateRequest | 200 → CmsPageResponse |
| POST | `/api/v{version}/admin/cms/pages/{id}/publish` | Publishes a draft page, or re-publishes one already live (SRS 12.16.2 "draft/publish status"). | Admin JWT + permission `cms.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/cms/pages/{id}/unpublish` | Pulls a page back to draft without deleting it. | Admin JWT + permission `cms.write` | — | 204 No Content |

### Coupons

Admin coupon and campaign management (SRS 12.12, task 118): coupon CRUD with every rule dimension (discount type/value, min order value, applicable category, usage limits, validity window, first/repeat-order segment) plus redemption reporting. Read-only actions require "coupons.read"; every mutating action requires "coupons.write" (task 96b/96c) - a role granted Write always also holds Read (see `AdminPermissionCatalog`), so the two are applied per-action rather than a single class-level policy, matching `CustomersController`.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/coupons` | Search/filter coupons (SRS 12.12.1, task 118). | Admin JWT + permission `coupons.read` | — | 200 → CouponAdminSearchResponse |
| POST | `/api/v{version}/admin/coupons` | Creates a coupon with every rule dimension (SRS 12.12.1, task 118). | Admin JWT + permission `coupons.write` | CouponCreateRequest | 201 → CouponAdminResponse |
| GET | `/api/v{version}/admin/coupons/categories` | Active categories, for the coupon form's "applicable category" picker (see `ListApplicableCategoriesAsync`). | Admin JWT + permission `coupons.read` | — | 200 → CategoryLookupResponse[] |
| GET | `/api/v{version}/admin/coupons/redemptions/report` | Redemption reporting - aggregate stats from CouponRedemption (SRS 12.12.2, task 118). | Admin JWT + permission `coupons.read` | — | 200 → CouponRedemptionReportResponse |
| GET | `/api/v{version}/admin/coupons/{id}` | Coupon detail (SRS 12.12.1, task 118). | Admin JWT + permission `coupons.read` | — | 200 → CouponAdminResponse |
| PUT | `/api/v{version}/admin/coupons/{id}` | Edits every mutable rule dimension of an existing coupon (SRS 12.12.1, task 118). The coupon code itself is immutable - see `Update`. | Admin JWT + permission `coupons.write` | CouponUpdateRequest | 200 → CouponAdminResponse |
| POST | `/api/v{version}/admin/coupons/{id}/activate` | Re-enables a suspended coupon (SRS 12.12.1 "Active status"). | Admin JWT + permission `coupons.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/coupons/{id}/deactivate` | Suspends a coupon without deleting it (SRS 12.12.1 "Active status"). | Admin JWT + permission `coupons.write` | — | 204 No Content |

### Customers

Admin customer management (SRS 12.4, tasks 101a-101d): search/filter, the 360 detail view, block/unblock, and internal notes. Read-only actions require "customers.read"; every mutating action requires "customers.write" (task 96b/96c) - a role granted Write always also holds Read (see `AdminPermissionCatalog`), so the two are applied per-action rather than a single class-level policy.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/customers` | Search/filter customers (SRS 12.4.1, task 101a). | Admin JWT + permission `customers.read` | — | 200 → CustomerSearchResponse |
| GET | `/api/v{version}/admin/customers/analytics` | Customer Analytics dashboard (Admin Web new page): total/active/ blocked/unverified/deleted counts, new-today/7-day/trend-window registration counts, the acquisition-vs-activation booking funnel, a daily registration-trend series and a top-cities breakdown - see `CustomerAnalyticsResponse`'s doc comment for exactly what each field means. Static route declared ahead of `GetDetail`'s `{customerId:guid}` route, same non-clash reasoning as `ListPerformance`'s own precedent. | Admin JWT + permission `customers.read` | — | 200 → CustomerAnalyticsResponse |
| GET | `/api/v{version}/admin/customers/{customerId}` | Customer 360 view - profile, addresses, bookings, wallet, coupons, tickets, notes (SRS 12.4.2, task 101b). | Admin JWT + permission `customers.read` | — | 200 → CustomerDetailResponse |
| POST | `/api/v{version}/admin/customers/{customerId}/block` | Blocks a customer's account (SRS 12.4.3, task 101c). | Admin JWT + permission `customers.write` | BlockCustomerRequest | 200 → CustomerDetailResponse |
| POST | `/api/v{version}/admin/customers/{customerId}/delete` | Deletes a customer's account (right-to-erasure request handled on the customer's behalf by support). Terminal and irreversible - unlike Block/Unblock there is no "undelete" endpoint. | Admin JWT + permission `customers.write` | BlockCustomerRequest | 200 → CustomerDetailResponse |
| POST | `/api/v{version}/admin/customers/{customerId}/notes` | Adds an internal note to a customer's record (SRS 12.4.3, task 101d). | Admin JWT + permission `customers.write` | AddCustomerNoteRequest | 201 → CustomerNoteResponse |
| POST | `/api/v{version}/admin/customers/{customerId}/unblock` | Restores a blocked customer's account (SRS 12.4.3, task 101c). | Admin JWT + permission `customers.write` | — | 200 → CustomerDetailResponse |
| POST | `/api/v{version}/admin/customers/{customerId}/wallet/adjust` | Records a manual wallet credit/debit (SRS 12.4.3 gap: the wallet tab was read-only) - a goodwill credit or a correction, for when none of the wallet's normal sources (refund, coupon, referral, Nestly Coins) apply. A debit fails with a business error rather than letting the balance go negative (see `DebitAsync`). | Admin JWT + permission `customers.write` | AdjustCustomerWalletRequest | 200 → CustomerDetailResponse |

### Dashboard

Admin dashboard KPI widgets (SRS 12.3, task 99): bookings, revenue, cancellations, refunds, and open support tickets, filterable by date range/city/category (SRS 12.3.2). Gated behind "dashboard.read" (task 96b) - this is a read-only view, so it only ever needs the module's Read tier, never Write.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/dashboard/kpis` | Computes the KPI widgets for the given filters (all optional - an unset date range defaults to today). | Admin JWT + permission `dashboard.read` | — | 200 → DashboardKpiResponse |

### Geography

Admin geography master CRUD (SRS 12.9.1, task 111): state, city, zone, locality and pincode. Gated behind the "serviceability" module - the geography master and the serviceability mappings built on top of it (see `ServiceabilityMappingsController`) are one admin capability (SRS 12.9).

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/geography/cities` | _(no doc comment)_ | Admin JWT + permission `serviceability.read` | — | 200 → CityAdminResponse[] |
| POST | `/api/v{version}/admin/geography/cities` | _(no doc comment)_ | Admin JWT + permission `serviceability.write` | CityCreateRequest | 200 → CityAdminResponse |
| PUT | `/api/v{version}/admin/geography/cities/{id}` | _(no doc comment)_ | Admin JWT + permission `serviceability.write` | CityUpdateRequest | 200 → CityAdminResponse |
| POST | `/api/v{version}/admin/geography/cities/{id}/activate` | _(no doc comment)_ | Admin JWT + permission `serviceability.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/geography/cities/{id}/deactivate` | _(no doc comment)_ | Admin JWT + permission `serviceability.write` | — | 204 No Content |
| GET | `/api/v{version}/admin/geography/localities` | _(no doc comment)_ | Admin JWT + permission `serviceability.read` | — | 200 → LocalityAdminResponse[] |
| POST | `/api/v{version}/admin/geography/localities` | _(no doc comment)_ | Admin JWT + permission `serviceability.write` | LocalityCreateRequest | 200 → LocalityAdminResponse |
| PUT | `/api/v{version}/admin/geography/localities/{id}` | _(no doc comment)_ | Admin JWT + permission `serviceability.write` | LocalityUpdateRequest | 200 → LocalityAdminResponse |
| POST | `/api/v{version}/admin/geography/localities/{id}/activate` | _(no doc comment)_ | Admin JWT + permission `serviceability.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/geography/localities/{id}/deactivate` | _(no doc comment)_ | Admin JWT + permission `serviceability.write` | — | 204 No Content |
| GET | `/api/v{version}/admin/geography/pincodes` | _(no doc comment)_ | Admin JWT + permission `serviceability.read` | — | 200 → PincodeAdminResponse[] |
| POST | `/api/v{version}/admin/geography/pincodes` | _(no doc comment)_ | Admin JWT + permission `serviceability.write` | PincodeCreateRequest | 200 → PincodeAdminResponse |
| POST | `/api/v{version}/admin/geography/pincodes/{id}/activate` | _(no doc comment)_ | Admin JWT + permission `serviceability.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/geography/pincodes/{id}/deactivate` | _(no doc comment)_ | Admin JWT + permission `serviceability.write` | — | 204 No Content |
| GET | `/api/v{version}/admin/geography/states` | _(no doc comment)_ | Admin JWT + permission `serviceability.read` | — | 200 → StateResponse[] |
| POST | `/api/v{version}/admin/geography/states` | _(no doc comment)_ | Admin JWT + permission `serviceability.write` | StateCreateRequest | 200 → StateResponse |
| PUT | `/api/v{version}/admin/geography/states/{id}` | _(no doc comment)_ | Admin JWT + permission `serviceability.write` | StateUpdateRequest | 200 → StateResponse |
| POST | `/api/v{version}/admin/geography/states/{id}/activate` | _(no doc comment)_ | Admin JWT + permission `serviceability.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/geography/states/{id}/deactivate` | _(no doc comment)_ | Admin JWT + permission `serviceability.write` | — | 204 No Content |
| GET | `/api/v{version}/admin/geography/zones` | _(no doc comment)_ | Admin JWT + permission `serviceability.read` | — | 200 → ZoneResponse[] |
| POST | `/api/v{version}/admin/geography/zones` | _(no doc comment)_ | Admin JWT + permission `serviceability.write` | ZoneCreateRequest | 200 → ZoneResponse |
| PUT | `/api/v{version}/admin/geography/zones/{id}` | _(no doc comment)_ | Admin JWT + permission `serviceability.write` | ZoneUpdateRequest | 200 → ZoneResponse |
| POST | `/api/v{version}/admin/geography/zones/{id}/activate` | _(no doc comment)_ | Admin JWT + permission `serviceability.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/geography/zones/{id}/deactivate` | _(no doc comment)_ | Admin JWT + permission `serviceability.write` | — | 204 No Content |

### Landing

Admin curation of the customer home page's three configurable sections. Gated behind the existing "cms" module rather than "catalog": this picks which catalog entries to merchandise, it does not create or edit them, and it sits alongside banners/pages as home-page content. Every write replaces a whole section (PUT, not POST/DELETE per row) so a repeated save is idempotent and the submitted order is the display order.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/landing` | The full curation config - all three sections in one call for the admin screen. | Admin JWT + permission `cms.read` | — | 200 → LandingConfigResponse |
| PUT | `/api/v{version}/admin/landing/category-sections/{categoryId}` | Replaces one category strip's service picks (max 5, all belonging to that category). | Admin JWT + permission `cms.write` | UpdateCategorySectionRequest | 204 No Content |
| PUT | `/api/v{version}/admin/landing/most-booked` | Replaces the "Most Booked Services" picks. | Admin JWT + permission `cms.write` | UpdateMostBookedRequest | 204 No Content |
| PUT | `/api/v{version}/admin/landing/new-and-trending` | Replaces the "New &amp; Trending" sub-category picks. | Admin JWT + permission `cms.write` | UpdateNewAndTrendingRequest | 204 No Content |

### NotificationTemplates

Admin notification template management (SRS 12.17, tasks 126a-d): CRUD over channel-specific templates with variable placeholders, preview/test rendering, and change history via the existing audit trail. Read-only actions require "notifications.read"; every mutating action requires "notifications.write" (task 96b/96c) - applied per-action rather than a single class-level policy, matching `CouponsController`.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/notification-templates` | Lists templates, optionally filtered by channel/event type/active status (SRS 12.17.1-2). | Admin JWT + permission `notifications.read` | — | 200 → NotificationTemplateResponse[] |
| POST | `/api/v{version}/admin/notification-templates` | Creates a template for a not-yet-covered (EventType, Channel) combination (SRS 12.17.1-2). | Admin JWT + permission `notifications.write` | NotificationTemplateCreateRequest | 201 → NotificationTemplateResponse |
| POST | `/api/v{version}/admin/notification-templates/preview` | Renders draft (not-yet-saved) subject/body text against sample values, for the template editor's live preview (task 127). | Admin JWT + permission `notifications.read` | NotificationTemplateAdHocPreviewRequest | 200 → NotificationTemplatePreviewResponse |
| GET | `/api/v{version}/admin/notification-templates/{id}` | _(no doc comment)_ | Admin JWT + permission `notifications.read` | — | 200 → NotificationTemplateResponse |
| PUT | `/api/v{version}/admin/notification-templates/{id}` | Edits an existing template's subject/body (SRS 12.17.2). Event type, channel and template key are immutable - see `NotificationTemplate`'s doc comment. | Admin JWT + permission `notifications.write` | NotificationTemplateUpdateRequest | 200 → NotificationTemplateResponse |
| POST | `/api/v{version}/admin/notification-templates/{id}/activate` | _(no doc comment)_ | Admin JWT + permission `notifications.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/notification-templates/{id}/deactivate` | _(no doc comment)_ | Admin JWT + permission `notifications.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/notification-templates/{id}/preview` | Renders a saved template against sample values (SRS 12.17.2 "Preview/test capability", task 126b) - a pure render, nothing is sent or persisted. | Admin JWT + permission `notifications.read` | NotificationTemplatePreviewRequest | 200 → NotificationTemplatePreviewResponse |

### Payments

Admin payment transaction view (SRS 12.13.1, task 311): a filterable transaction list and a per-transaction detail (attempts + refunds), the reconciliation surface admins previously only got incidentally through a booking's own detail page (`BookingsController.GetDetail`'s embedded `AdminBookingPaymentSummary`/`Refunds`). Read-only for the list/detail themselves - see `Payments`'s doc comment for why there is still no refund-initiation write endpoint here (SRS 12.13.2-3 remains `BookingsController`'s "bookings.write"-gated action) - plus the payment reconciliation queue and its one write action (docs/OPEN-FIXES-FEATURES.csv "Payment reconciliation"): stuck-pending, failed and orphaned Awaiting Payment bookings, gateway orders versus booking status, with a void action for a stuck pending order. A transaction id that does not exist 404s rather than 403ing (SRS 28.3 IDOR guard, same rule `docs/API.md`'s address-endpoint section documents) - there is no ownership concept to hide behind here since every admin holding "payments.read" may see every transaction, but the convention of never leaking existence via status code is kept consistent with the rest of the admin API regardless.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/payments` | Transaction list, filterable by booking id, status and creation date range (SRS 12.13.1). | Admin JWT + permission `payments.read` | — | 200 → PagedAdminPaymentTransactionResponse |
| GET | `/api/v{version}/admin/payments/reconciliation` | Payment reconciliation queue (docs/OPEN-FIXES-FEATURES.csv "Payment reconciliation"): stuck-pending, failed and orphaned Awaiting Payment/ Payment Failed bookings, oldest first - see `GetReconciliationAsync`. A static route ahead of `GetDetail`'s `{transactionId:guid}` route, same safe-by-construction ordering as `BookingsController.ListUnassignedAtRisk` - the guid constraint means "reconciliation" never matches it. | Admin JWT + permission `payments.read` | — | 200 → AdminPaymentReconciliationResponse |
| GET | `/api/v{version}/admin/payments/{transactionId}` | Full transaction detail: attempts and refunds (SRS 12.13.1, 14.3). | Admin JWT + permission `payments.read` | — | 200 → AdminPaymentTransactionDetailResponse |
| POST | `/api/v{version}/admin/payments/{transactionId}/void` | Voids a stuck pending payment order (docs/OPEN-FIXES-FEATURES.csv "Payment reconciliation") - marks OUR record only, no gateway call - see `VoidAsync`. The module's one write action, gated separately from the read endpoints above (`Payments`'s doc comment). | Admin JWT + permission `payments.write` | AdminVoidPaymentTransactionRequest | 200 → AdminPaymentTransactionListItemResponse |

### Payouts

Admin provider payout batches (PROVIDER.md Financial Domain, API surface "run payout batch, list payouts"; task 148). OPEN DECISIONS #3: v1 is manual bank transfer - a batch is created from the provider's earning ledger for a period, then an admin walks its status Pending -&gt; Processing -&gt; Paid/Failed by hand; there is no gateway integration. Gated "payout.read"/"payout.write" (PROVIDER.md RBAC ADDITIONS).

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/payouts` | Search/filter payouts by provider and/or status. | Admin JWT + permission `payout.read` | — | 200 → ProviderPayoutSearchResponse |
| POST | `/api/v{version}/admin/payouts/providers/{providerId}` | Runs a payout batch for a provider over a period, summing their earning ledger (task 148). | Admin JWT + permission `payout.write` | CreateProviderPayoutRequest | 201 → ProviderPayoutResponse |
| POST | `/api/v{version}/admin/payouts/webhook/payu` | PayU Payouts' own transfer webhook (PayU Payouts task brief) - separate route from admin actions above, deliberately not [Authorize]: the caller is PayU, not a logged-in admin. See `PayUPayoutWebhookPayload`'s own doc comment for why this has no signature to verify (unlike PayU Hosted Checkout's callback) and what lightweight check stands in for one instead. Always idempotent (mirrors the Hosted Checkout webhook's own convention): a redelivered webhook for an already-resolved payout is a no-op 200, never re-applied. | Public | PayUPayoutWebhookPayload | 200 OK |
| GET | `/api/v{version}/admin/payouts/{payoutId}` | _(no doc comment)_ | Admin JWT + permission `payout.read` | — | 200 → ProviderPayoutResponse |
| POST | `/api/v{version}/admin/payouts/{payoutId}/pay-via-payu` | The automated counterpart to `UpdateStatus`'s manual Pending -&gt; Processing move: triggers a real PayU Payouts transfer (PayU Payouts task brief). Same `WritePolicy` as `UpdateStatus` - both are equally sensitive admin write actions on the same payout. A Business/NotFound error (never a silent no-op) when PayU is not configured, the payout is not Pending, the provider has no admin-verified bank account on file, or PayU declines the transfer outright. | Admin JWT + permission `payout.write` | — | 200 → ProviderPayoutResponse |
| POST | `/api/v{version}/admin/payouts/{payoutId}/status` | Advances a payout's status: Pending -&gt; Processing -&gt; Paid (with a bank transfer reference), or -&gt; Failed (task 148, OPEN DECISIONS #3 - admin-triggered, not gateway-driven). | Admin JWT + permission `payout.write` | UpdateProviderPayoutStatusRequest | 200 → ProviderPayoutResponse |

### Pricing

Admin pricing management (SRS 12.8, tasks 109a-109e): base/add-on/ city-wise/promotional price, per-city tax rate, visit charge and platform fee, effective dating, and a price-change audit trail via `IAuditLogWriter`. Gated behind the "pricing" module, matching the admin RBAC catalog in `AdminPermissionCatalog`.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/pricing/addons` | _(no doc comment)_ | Admin JWT + permission `pricing.read` | — | 200 → AddOnPriceResponse[] |
| PUT | `/api/v{version}/admin/pricing/addons/{addOnId}` | _(no doc comment)_ | Admin JWT + permission `pricing.write` | AddOnPriceUpdateRequest | 200 → AddOnPriceResponse |
| GET | `/api/v{version}/admin/pricing/city-prices` | _(no doc comment)_ | Admin JWT + permission `pricing.read` | — | 200 → CityPriceResponse[] |
| POST | `/api/v{version}/admin/pricing/city-prices` | _(no doc comment)_ | Admin JWT + permission `pricing.write` | CityPriceCreateRequest | 200 → CityPriceResponse |
| PUT | `/api/v{version}/admin/pricing/city-prices/{id}` | _(no doc comment)_ | Admin JWT + permission `pricing.write` | CityPriceUpdateRequest | 200 → CityPriceResponse |
| GET | `/api/v{version}/admin/pricing/policies` | _(no doc comment)_ | Admin JWT + permission `pricing.read` | — | 200 → CityPricingPolicyResponse[] |
| PUT | `/api/v{version}/admin/pricing/policies/{cityId}` | _(no doc comment)_ | Admin JWT + permission `pricing.write` | CityPricingPolicyUpsertRequest | 200 → CityPricingPolicyResponse |
| GET | `/api/v{version}/admin/pricing/promotions` | _(no doc comment)_ | Admin JWT + permission `pricing.read` | — | 200 → PromotionalPriceResponse[] |
| POST | `/api/v{version}/admin/pricing/promotions` | _(no doc comment)_ | Admin JWT + permission `pricing.write` | PromotionalPriceCreateRequest | 200 → PromotionalPriceResponse |
| PUT | `/api/v{version}/admin/pricing/promotions/{id}` | _(no doc comment)_ | Admin JWT + permission `pricing.write` | PromotionalPriceUpdateRequest | 200 → PromotionalPriceResponse |
| POST | `/api/v{version}/admin/pricing/promotions/{id}/activate` | _(no doc comment)_ | Admin JWT + permission `pricing.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/pricing/promotions/{id}/deactivate` | _(no doc comment)_ | Admin JWT + permission `pricing.write` | — | 204 No Content |
| GET | `/api/v{version}/admin/pricing/services` | _(no doc comment)_ | Admin JWT + permission `pricing.read` | — | 200 → ServicePriceResponse[] |
| PUT | `/api/v{version}/admin/pricing/services/{serviceId}` | _(no doc comment)_ | Admin JWT + permission `pricing.write` | ServicePriceUpdateRequest | 200 → ServicePriceResponse |

### ProviderReferralProgramConfig

Admin CRUD for the provider referral program config (reward values, qualifying completed-job count, expiry days, per-provider cap, active flag), mirrors `ReferralProgramConfigController`. Read-only actions require "provider-referral.read"; every mutating action requires "provider-referral.write".

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/provider-referral/config` | The single provider referral program config row. | Admin JWT + permission `provider-referral.read` | — | 200 → ProviderReferralProgramConfigResponse |
| PUT | `/api/v{version}/admin/provider-referral/config` | Edits every mutable field of the provider referral program config. | Admin JWT + permission `provider-referral.write` | ProviderReferralProgramConfigUpdateRequest | 200 → ProviderReferralProgramConfigResponse |

### ProviderReferrals

Admin provider-referral list/detail and fraud review queue, mirrors `ReferralsController` (funnel/cost reports intentionally not included in this v1 - see PROVIDER-REFERRAL.md).

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/provider-referral` | Filter by status, fraud flag, and/or search by provider. | Admin JWT + permission `provider-referral.read` | — | 200 → ProviderReferralAdminSearchResponse |
| GET | `/api/v{version}/admin/provider-referral/fraud-queue` | Only referrals currently flagged for fraud review - the fraud review queue. | Admin JWT + permission `provider-referral.read` | — | 200 → ProviderReferralAdminSearchResponse |
| GET | `/api/v{version}/admin/provider-referral/{id}` | Provider referral detail view. | Admin JWT + permission `provider-referral.read` | — | 200 → ProviderReferralAdminDetailResponse |
| POST | `/api/v{version}/admin/provider-referral/{id}/approve` | Confirms a flagged referral as a real abuse pattern - the flag clears; any reward reversal is a separate, deliberate action. | Admin JWT + permission `provider-referral.write` | ProviderReferralFraudReviewRequest | 204 No Content |
| POST | `/api/v{version}/admin/provider-referral/{id}/flag` | Manually flags a provider referral for fraud review. | Admin JWT + permission `provider-referral.write` | ProviderReferralFraudReviewRequest | 204 No Content |
| POST | `/api/v{version}/admin/provider-referral/{id}/reject` | Rejects a flag as a false positive - the flag clears, no further action. | Admin JWT + permission `provider-referral.write` | ProviderReferralFraudReviewRequest | 204 No Content |

### ProviderSupportTickets

Admin workflow over provider support tickets (Provider Management UX pass) - search/detail across every provider, reply, resolve. Gated behind the same "support.read"/"support.write" modules as the customer `SupportTicketsController`: this is the same admin support function, just for the provider side of the marketplace rather than duplicated permission plumbing for what is conceptually one queue.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/provider-support-tickets` | Filtered/paginated ticket search across every provider. | Admin JWT + permission `support.read` | — | 200 → AdminProviderSupportTicketSearchResponse |
| GET | `/api/v{version}/admin/provider-support-tickets/{id}` | Full ticket detail - comment thread and provider display name. | Admin JWT + permission `support.read` | — | 200 → AdminProviderSupportTicketDetailResponse |
| POST | `/api/v{version}/admin/provider-support-tickets/{id}/reply` | Appends an admin reply to the ticket's thread. A still-Open ticket moves to InProgress. | Admin JWT + permission `support.write` | AddProviderSupportTicketCommentRequest | 200 → AdminProviderSupportTicketDetailResponse |
| POST | `/api/v{version}/admin/provider-support-tickets/{id}/resolve` | Moves the ticket to Resolved. | Admin JWT + permission `support.write` | ResolveProviderSupportTicketRequest | 200 → AdminProviderSupportTicketDetailResponse |

### Providers

Admin provider directory management (PROVIDER.md API surface "Admin-Facing Additions": Provider CRUD, KYC approval, performance; tasks 150a-150c, 160). Read-only actions require "provider.read"; profile/status/KYC/ background-check mutations require "provider.write" - manual bank-transfer earning adjustments are gated "payout.write" instead (see the earnings endpoints below), matching the Provider/Payout RBAC split in PROVIDER.md's RBAC ADDITIONS section.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/providers` | Search/filter providers (task 150a). | Admin JWT + permission `provider.read` | — | 200 → ProviderSearchResponse |
| POST | `/api/v{version}/admin/providers` | Admin-created provider record (task 150a). ProviderType is always Individual - OPEN DECISIONS #2. | Admin JWT + permission `provider.write` | CreateProviderRequest | 201 → ProviderDetailResponse |
| GET | `/api/v{version}/admin/providers/bank-accounts/pending` | The bank-account verification queue: every provider's submitted bank account details still awaiting a verdict, oldest submission first - same shape as `ListPendingKycDocuments`. Static route declared ahead of `ApproveBankAccount`'s `{bankAccountId:guid}` route, same non-clash reasoning as `ListPerformance` below. | Admin JWT + permission `provider.read` | — | 200 → ProviderBankAccountQueueItemResponse[] |
| POST | `/api/v{version}/admin/providers/bank-accounts/{bankAccountId}/approve` | Approves a provider's submitted bank account details. | Admin JWT + permission `provider.write` | — | 200 → ProviderBankAccountResponse |
| POST | `/api/v{version}/admin/providers/bank-accounts/{bankAccountId}/reject` | Rejects a provider's submitted bank account details. | Admin JWT + permission `provider.write` | RejectProviderBankAccountRequest | 200 → ProviderBankAccountResponse |
| GET | `/api/v{version}/admin/providers/kyc-documents/pending` | The KYC verification queue (Provider Management UX pass): every document across every provider still awaiting a verdict, oldest submission first. Before this, finding a pending document required searching for a specific provider and opening their Verification tab - this is the cross-provider worklist an admin actually works from. Static route declared ahead of `ApproveKycDocument`'s `{documentId:guid}` route, same non-clash reasoning as `ListPerformance` above. | Admin JWT + permission `provider.read` | — | 200 → ProviderKycDocumentQueueItemResponse[] |
| POST | `/api/v{version}/admin/providers/kyc-documents/{documentId}/approve` | Approves a submitted KYC document (task 150b, the admin-side counterpart to task 146c's submission flow). | Admin JWT + permission `provider.write` | — | 200 → ProviderKycDocumentResponse |
| POST | `/api/v{version}/admin/providers/kyc-documents/{documentId}/reject` | Rejects a submitted KYC document (task 150b). | Admin JWT + permission `provider.write` | RejectProviderKycDocumentRequest | 200 → ProviderKycDocumentResponse |
| GET | `/api/v{version}/admin/providers/onboarding-overview` | The Provider Onboarding Overview dashboard's cohort-of-the-day funnel counts (Admin Web new page): of every provider who registered on `date` (defaults to today), how many are now at each onboarding/status stage - see `AdminProviderOnboardingOverviewResponse`'s doc comment for exactly what each count means. Static route declared ahead of `GetDetail`'s `{providerId:guid}` route, same non-clash reasoning as `ListPerformance` above. | Admin JWT + permission `provider.read` | — | 200 → AdminProviderOnboardingOverviewResponse |
| GET | `/api/v{version}/admin/providers/performance` | The provider-performance ranking list (docs/OPEN-FIXES-FEATURES.csv "Provider performance"): offers received, acceptance rate, average response time, completion rate and average rating, per provider, over a rolling window (default 30 days) - sortable by any of those columns. A static route ahead of `GetPerformance`'s `{providerId:guid}/performance` route, same non-clash reasoning as `GetFulfilmentBoard` in BookingsController (the guid constraint there never matches the literal "performance" segment here either way, but the static route reads clearer listed first). | Admin JWT + permission `provider.read` | — | 200 → ProviderPerformanceListResponse |
| GET | `/api/v{version}/admin/providers/photo-moderation/pending` | The photo-moderation queue (task 293): every provider whose profile photo is awaiting a verdict. A provider photo is user-supplied content shown to customers, so it goes through the same admin gate this API already applies to KYC documents and review text - it is not published on upload. | Admin JWT + permission `provider.read` | — | 200 → ProviderPhotoResponse[] |
| GET | `/api/v{version}/admin/providers/{providerId}` | Provider detail: profile, KYC documents, background check history (task 150a/150b). | Admin JWT + permission `provider.read` | — | 200 → ProviderDetailResponse |
| PUT | `/api/v{version}/admin/providers/{providerId}` | Updates a provider's profile (task 150a). | Admin JWT + permission `provider.write` | UpdateProviderRequest | 200 → ProviderDetailResponse |
| POST | `/api/v{version}/admin/providers/{providerId}/activate` | Activates a provider once KYC is approved and the background check has passed (task 160's gate). | Admin JWT + permission `provider.write` | — | 200 → ProviderDetailResponse |
| POST | `/api/v{version}/admin/providers/{providerId}/background-check` | Records a background/reference check outcome (task 160) - a distinct step from KYC document validation. | Admin JWT + permission `provider.write` | RecordBackgroundCheckRequest | 200 → ProviderBackgroundCheckResponse |
| GET | `/api/v{version}/admin/providers/{providerId}/capacity` | A provider's dispatch capacity limits (task 245/308). Hard-enforced by the automatic-assignment engine; still only an advisory load signal on manual admin assignment (PROVIDER.md OPEN DECISIONS - AUTOMATIC ASSIGNMENT #2). Unlimited (both null) until an admin sets one below. | Admin JWT + permission `provider.read` | — | 200 → ProviderCapacityResponse |
| PUT | `/api/v{version}/admin/providers/{providerId}/capacity` | Sets (or clears, via null) a provider's `MaxJobsPerDay`/`MaxJobsPerSlot` (task 308). Full-overwrite, same PUT-style convention as `Update`. | Admin JWT + permission `provider.write` | SetProviderCapacityRequest | 200 → ProviderCapacityResponse |
| POST | `/api/v{version}/admin/providers/{providerId}/delete` | Deletes a provider's account (right-to-erasure request handled on the provider's behalf by support). Terminal and irreversible - unlike Suspend/Reactivate there is no "undelete" endpoint. | Admin JWT + permission `provider.write` | DeleteProviderRequest | 200 → ProviderDetailResponse |
| GET | `/api/v{version}/admin/providers/{providerId}/earnings` | A provider's earning ledger and current balance (task 148). | Admin JWT + permission `provider.read` | — | 200 → ProviderEarningsSummaryResponse |
| POST | `/api/v{version}/admin/providers/{providerId}/earnings` | Records a manual credit/debit adjustment to a provider's earning ledger (task 148 - "credit per completed job... debit for penalties"). Gated "payout.write" rather than "provider.write" - this is a financial-ledger mutation, the same RBAC tier as processing a payout, not a provider-profile edit. | Admin JWT + permission `payout.write` | RecordProviderEarningAdjustmentRequest | 201 → ProviderEarningLedgerEntryResponse |
| GET | `/api/v{version}/admin/providers/{providerId}/performance` | Single-provider job-fulfilment performance summary, all-time (PROVIDER.md API surface "get provider performance metrics", task 150c). | Admin JWT + permission `provider.read` | — | 200 → ProviderPerformanceResponse |
| POST | `/api/v{version}/admin/providers/{providerId}/photo/approve` | Approves a provider's profile photo - the only transition that makes it visible to customers (task 293). | Admin JWT + permission `provider.write` | — | 200 → ProviderPhotoResponse |
| POST | `/api/v{version}/admin/providers/{providerId}/photo/reject` | Rejects a provider's profile photo (task 293). The reason is shown back to the provider so a rejection is actionable. | Admin JWT + permission `provider.write` | RejectProviderPhotoRequest | 200 → ProviderPhotoResponse |
| POST | `/api/v{version}/admin/providers/{providerId}/reactivate` | Reactivates a previously suspended provider (task 150a). | Admin JWT + permission `provider.write` | — | 200 → ProviderDetailResponse |
| POST | `/api/v{version}/admin/providers/{providerId}/suspend` | Suspends a provider's account (task 150a, PROVIDER.md RBAC ADDITIONS "Suspend"). | Admin JWT + permission `provider.write` | SuspendProviderRequest | 200 → ProviderDetailResponse |

### RecurringPlans

Admin visibility into recurring booking plans (task 299, PRODUCT-ENHANCEMENTS.md section 2): the full plan list, the status/cadence/upcoming-volume report behind it, and (Order/Booking Management UX pass) plan-level cancellation - see `IRecurringBookingPlanAdminService` for what cancellation does and does not affect. RBAC: read actions are gated behind the EXISTING "bookings.read", with no new `AdminModules` entry and no "RecurringPlans.View" code. The task brief left that open ("no new RBAC module needed if admin's existing Booking view permission already covers occurrence rows"); it does, for three reasons: 1. A recurring plan is a standing instruction to create Bookings, and every row this controller reports on is either a `RecurringBookingPlan` or a `Booking` carrying that plan's id (task 296's `RecurringBookingPlanId`). An admin holding "bookings.read" can already open every one of those bookings individually through `BookingsController` and read strictly more about each of them (customer contact details, payment, refunds) than this controller's counts expose. A new permission gating a strictly weaker view of data the holder can already see is not a boundary, it is an inconvenience - and one that fails open, because the underlying bookings stay readable either way. 2. `BookingsController` already set this precedent in the opposite direction: provider assignment lives under "bookings.write" rather than the Provider module's, because assigning a provider is Booking-domain behaviour. Recurrence is likewise a property of how bookings come into existence, not a separate vertical. 3. `AdminPermissionAction`'s own doc comment calls splitting the matrix further "speculative (YAGNI)" until a controller actually needs the distinction, and `AdminModules` records the same judgement for Referral/Chat/Nestly Coins. A new module here would also cost a seed migration (`SeedNestlyCoinsPermissions` is the precedent) and a role-grant decision for all nine default roles - real schema and policy churn bought for no additional protection. The practical consequence is intended: Operations Admin and Booking Admin, the two roles that own day-to-day fulfilment, see recurring plans on day one without a permission grant, exactly as they see the bookings those plans generate.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/recurring-plans` | Every recurring plan on the platform, newest first, filterable by lifecycle status, cadence, customer, service, pause reason or whether it is paid in advance. | Admin JWT + permission `bookings.read` | — | 200 → AdminRecurringPlanSearchResponse |
| GET | `/api/v{version}/admin/recurring-plans/report` | Active/paused/cancelled/completed plan counts, the active-plan cadence mix, and upcoming occurrence volume over a horizon (defaults to the next four weeks). | Admin JWT + permission `bookings.read` | — | 200 → AdminRecurringPlanReportResponse |
| GET | `/api/v{version}/admin/recurring-plans/{planId}` | One plan with the customer's contact and wallet balance and the visits it has generated (upcoming first, then the latest past ones). | Admin JWT + permission `bookings.read` | — | 200 → AdminRecurringPlanDetailResponse |
| POST | `/api/v{version}/admin/recurring-plans/{planId}/cancel` | Cancels the whole standing instruction - no further occurrences are ever generated. Distinct from cancelling the individual bookings it has already produced, which is unaffected and still goes through `BookingsController`. | Admin JWT + permission `bookings.write` | AdminCancelRecurringPlanRequest | 200 → AdminRecurringPlanSummaryResponse |
| POST | `/api/v{version}/admin/recurring-plans/{planId}/pause` | Pauses an active plan on the customer's behalf: no new visits are booked while it is paused, visits already booked are untouched. The customer is told support paused it and cannot resume it themselves; the reason goes to the audit trail. | Admin JWT + permission `bookings.write` | AdminPauseRecurringPlanRequest | 200 → AdminRecurringPlanSummaryResponse |
| POST | `/api/v{version}/admin/recurring-plans/{planId}/resume` | Resumes a paused plan, whoever or whatever paused it (including one the system paused for unpaid visits). The customer is told; the reason goes to the audit trail. | Admin JWT + permission `bookings.write` | AdminResumeRecurringPlanRequest | 200 → AdminRecurringPlanSummaryResponse |

### ReferralProgramConfig

Admin CRUD for the referral program config (reward types/values per side, min qualifying order amount, expiry days, per-customer cap, active window) plus task 174's milestone tiers (task 167). Read-only actions require "referral.read"; every mutating action requires "referral.write" - same per-action split as `CouponsController`.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/referral/config` | The single referral program config row (task 167). | Admin JWT + permission `referral.read` | — | 200 → ReferralProgramConfigResponse |
| PUT | `/api/v{version}/admin/referral/config` | Edits every mutable field of the referral program config (task 167). | Admin JWT + permission `referral.write` | ReferralProgramConfigUpdateRequest | 200 → ReferralProgramConfigResponse |
| GET | `/api/v{version}/admin/referral/config/milestones` | All milestone tiers, active and inactive, ascending by threshold (task 174). | Admin JWT + permission `referral.read` | — | 200 → ReferralMilestoneResponse[] |
| POST | `/api/v{version}/admin/referral/config/milestones` | Creates a new milestone tier (task 174). | Admin JWT + permission `referral.write` | ReferralMilestoneCreateRequest | 201 → ReferralMilestoneResponse |
| POST | `/api/v{version}/admin/referral/config/milestones/{milestoneId}/activate` | Re-enables a suspended milestone tier. | Admin JWT + permission `referral.write` | — | 200 → ReferralMilestoneResponse |
| POST | `/api/v{version}/admin/referral/config/milestones/{milestoneId}/deactivate` | Suspends a milestone tier without deleting it. | Admin JWT + permission `referral.write` | — | 200 → ReferralMilestoneResponse |

### Referrals

Admin referral list/detail, fraud review queue (task 166's `IReferralFraudReviewService`, wired up here for the first time), and funnel/cost reports (task 170, 171). Read-only actions require "referral.read"; fraud-review actions and require "referral.write" - same per-action split as `CouponsController` (this module collapses REFERRAL.md's four permission tiers to the existing two, see `Referral`'s doc comment).

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/referral` | Filter by status, fraud flag, and/or search by customer (task 170). | Admin JWT + permission `referral.read` | — | 200 → ReferralAdminSearchResponse |
| GET | `/api/v{version}/admin/referral/fraud-queue` | Only referrals currently flagged for fraud review - the fraud review queue (task 166, 170). | Admin JWT + permission `referral.read` | — | 200 → ReferralAdminSearchResponse |
| GET | `/api/v{version}/admin/referral/reports/cost` | Total program cost report: every reward disbursed within the range, split wallet-credit vs coupon (task 171). | Admin JWT + permission `referral.read` | — | 200 → ReferralCostReportResponse |
| GET | `/api/v{version}/admin/referral/reports/funnel` | Funnel report: invited/registered/qualified/rewarded, cohort-based over an optional date range (task 171). | Admin JWT + permission `referral.read` | — | 200 → ReferralFunnelReportResponse |
| GET | `/api/v{version}/admin/referral/{id}` | Referral detail view (task 170). | Admin JWT + permission `referral.read` | — | 200 → ReferralAdminDetailResponse |
| POST | `/api/v{version}/admin/referral/{id}/approve` | Confirms a flagged referral as a real abuse pattern (task 166) - the flag clears; any reward reversal is a separate, deliberate action. | Admin JWT + permission `referral.write` | ReferralFraudReviewRequest | 204 No Content |
| POST | `/api/v{version}/admin/referral/{id}/flag` | Manually flags a referral for fraud review (task 166). | Admin JWT + permission `referral.write` | ReferralFraudReviewRequest | 204 No Content |
| POST | `/api/v{version}/admin/referral/{id}/reject` | Rejects a flag as a false positive (task 166) - the flag clears, no further action. | Admin JWT + permission `referral.write` | ReferralFraudReviewRequest | 204 No Content |

### Reports

Standard admin reports and the async export queue (SRS 12.18, tasks 128a-128d). Read-only report views and their instant CSV export share "reports.read" - same precedent as `ReviewsController`'s CSV export, which requires only Read since exporting data an admin can already view is not itself a mutation. Requesting an asynchronous export (`RequestExport`) requires "reports.write" instead: unlike an instant export, it creates a persisted `ExportJob` row and occupies a background worker slot, which is a write-shaped action even though the report content itself is read-only.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/reports/booking-revenue` | Booking and revenue report (SRS 12.18.1, task 128a). | Admin JWT + permission `reports.read` | — | 200 → BookingRevenueReportResponse |
| GET | `/api/v{version}/admin/reports/booking-revenue/export` | CSV export of the booking and revenue report (SRS 12.18.2, task 128a). | Admin JWT + permission `reports.read` | — | 200 OK |
| GET | `/api/v{version}/admin/reports/coupon-usage` | Coupon usage report (SRS 12.18.1, task 128b). | Admin JWT + permission `reports.read` | — | 200 → CouponUsageReportResponse |
| GET | `/api/v{version}/admin/reports/coupon-usage/export` | CSV export of the coupon usage report (SRS 12.18.2, task 128b). | Admin JWT + permission `reports.read` | — | 200 OK |
| GET | `/api/v{version}/admin/reports/customer-segmentation` | Customer segmentation report (SRS 12.18.1, task 128c). | Admin JWT + permission `reports.read` | — | 200 → CustomerSegmentationReportResponse |
| GET | `/api/v{version}/admin/reports/customer-segmentation/export` | CSV export of the customer segmentation report (SRS 12.18.2, task 128c). | Admin JWT + permission `reports.read` | — | 200 OK |
| GET | `/api/v{version}/admin/reports/exports` | Every export job the calling admin has requested, newest first (task 128d). | Admin JWT + permission `reports.read` | — | 200 → ExportJobStatusResponse[] |
| POST | `/api/v{version}/admin/reports/exports` | Requests an asynchronous export (SRS 12.18.2, task 128d) - for a large date range, generated by a background job rather than this request. | Admin JWT + permission `reports.write` | RequestExportJobRequest | 202 → ExportJobStatusResponse |
| GET | `/api/v{version}/admin/reports/exports/{jobId}` | Polls one export job's status (task 128d). | Admin JWT + permission `reports.read` | — | 200 → ExportJobStatusResponse |
| GET | `/api/v{version}/admin/reports/exports/{jobId}/download` | Downloads a completed export's CSV (task 128d). | Admin JWT + permission `reports.read` | — | 200 OK |
| GET | `/api/v{version}/admin/reports/refunds` | Refund report (SRS 12.18.1, task 128b). | Admin JWT + permission `reports.read` | — | 200 → RefundReportResponse |
| GET | `/api/v{version}/admin/reports/refunds/export` | CSV export of the refund report (SRS 12.18.2, task 128b). | Admin JWT + permission `reports.read` | — | 200 OK |
| GET | `/api/v{version}/admin/reports/support-tickets` | Support ticket volume/resolution-time report (SRS 12.18.1, task 128c). | Admin JWT + permission `reports.read` | — | 200 → SupportTicketReportResponse |
| GET | `/api/v{version}/admin/reports/support-tickets/export` | CSV export of the support ticket report (SRS 12.18.2, task 128c). | Admin JWT + permission `reports.read` | — | 200 OK |

### Reviews

Admin review moderation (SRS 12.15, task 122): filterable search (status, flagged, rating range, date, service/category), hide/unhide, flag/unflag, and CSV export. Read-only actions require "reviews.read"; every mutating action requires "reviews.write" (task 96b/96c) - same per-action split as `CustomersController`, since a role granted Write always also holds Read (see `AdminPermissionCatalog`).

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/reviews` | Filtered/paginated review search (SRS 12.15 "View reviews by service, category, date, rating"). | Admin JWT + permission `reviews.read` | — | 200 → ReviewModerationSearchResponse |
| GET | `/api/v{version}/admin/reviews/export` | CSV export of every review matching the filter (SRS 12.15 "Export reviews"). | Admin JWT + permission `reviews.read` | — | 200 OK |
| POST | `/api/v{version}/admin/reviews/{reviewId}/flag` | Flags a review as abusive/inappropriate content (SRS 12.15 "Flag abusive content"), independent of its hide/unhide visibility. | Admin JWT + permission `reviews.write` | ModerateReviewRequest | 200 → ReviewModerationResponse |
| POST | `/api/v{version}/admin/reviews/{reviewId}/hide` | Hides a review from the public/customer-facing side (SRS 12.15 "Hide/unhide reviews"). The original rating/text/tags are retained, never mutated. | Admin JWT + permission `reviews.write` | ModerateReviewRequest | 200 → ReviewModerationResponse |
| POST | `/api/v{version}/admin/reviews/{reviewId}/unflag` | Clears a review's abuse flag. | Admin JWT + permission `reviews.write` | ModerateReviewRequest | 200 → ReviewModerationResponse |
| POST | `/api/v{version}/admin/reviews/{reviewId}/unhide` | Restores a hidden review to public visibility (SRS 12.15 "Hide/unhide reviews"). | Admin JWT + permission `reviews.write` | ModerateReviewRequest | 200 → ReviewModerationResponse |

### ServiceAddOnGroups

Admin management of add-on groups and their selection rules (Phase 3 catalog redesign): CRUD, mapping to services. Flat, top-level route - same shape as `ServiceAddOnsController` - because add-on groups get their own admin-web tab rather than living only under one service's edit page. Gated behind the "catalog" permission module, same as `ServiceAddOnsController` (SRS 12.5-12.7 share one module).

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/catalog/addon-groups` | _(no doc comment)_ | Admin JWT + permission `catalog.read` | — | 200 → ServiceAddOnGroupAdminResponse[] |
| POST | `/api/v{version}/admin/catalog/addon-groups` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | ServiceAddOnGroupCreateRequest | 200 → ServiceAddOnGroupAdminResponse |
| DELETE | `/api/v{version}/admin/catalog/addon-groups/{id}` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | — | 204 No Content |
| GET | `/api/v{version}/admin/catalog/addon-groups/{id}` | _(no doc comment)_ | Admin JWT + permission `catalog.read` | — | 200 → ServiceAddOnGroupAdminResponse |
| PUT | `/api/v{version}/admin/catalog/addon-groups/{id}` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | ServiceAddOnGroupUpdateRequest | 200 → ServiceAddOnGroupAdminResponse |

### ServiceAddOns

Admin add-on management (SRS 12.7, task 107): CRUD and mapping to services, activation. Gated behind the "catalog" permission module, same as `CategoriesController`/`ServicesController` (SRS 12.5-12.7 share one module).

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/catalog/addons` | _(no doc comment)_ | Admin JWT + permission `catalog.read` | — | 200 → ServiceAddOnAdminResponse[] |
| POST | `/api/v{version}/admin/catalog/addons` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | ServiceAddOnCreateRequest | 200 → ServiceAddOnAdminResponse |
| GET | `/api/v{version}/admin/catalog/addons/{id}` | _(no doc comment)_ | Admin JWT + permission `catalog.read` | — | 200 → ServiceAddOnAdminResponse |
| PUT | `/api/v{version}/admin/catalog/addons/{id}` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | ServiceAddOnUpdateRequest | 200 → ServiceAddOnAdminResponse |
| POST | `/api/v{version}/admin/catalog/addons/{id}/activate` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/catalog/addons/{id}/deactivate` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | — | 204 No Content |

### ServiceGroups

Admin management of service groups - optional section headers for a subset of a category's services (e.g. "Repair &amp; gas refill" under "AC"): CRUD, activation. Flat, top-level route - same shape as `ServiceAddOnGroupsController` - because service groups get their own admin-web tab rather than living only under one category's edit page. Gated behind the "catalog" permission module, same as `CategoriesController` (SRS 12.5-12.7 share one module).

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/catalog/service-groups` | _(no doc comment)_ | Admin JWT + permission `catalog.read` | — | 200 → ServiceGroupAdminResponse[] |
| POST | `/api/v{version}/admin/catalog/service-groups` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | ServiceGroupCreateRequest | 200 → ServiceGroupAdminResponse |
| DELETE | `/api/v{version}/admin/catalog/service-groups/{id}` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | — | 204 No Content |
| GET | `/api/v{version}/admin/catalog/service-groups/{id}` | _(no doc comment)_ | Admin JWT + permission `catalog.read` | — | 200 → ServiceGroupAdminResponse |
| PUT | `/api/v{version}/admin/catalog/service-groups/{id}` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | ServiceGroupUpdateRequest | 200 → ServiceGroupAdminResponse |
| POST | `/api/v{version}/admin/catalog/service-groups/{id}/activate` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/catalog/service-groups/{id}/deactivate` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | — | 204 No Content |

### ServiceVariants

Admin management of a service's priced/timed variants (Phase 3 catalog redesign): CRUD, activation. Nested under a service, same as `ServicesController`'s gallery-media sub-resource. Gated behind the "catalog" permission module, same as `ServicesController` (SRS 12.5-12.7 share one module).

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/catalog/services/{serviceId}/variants` | _(no doc comment)_ | Admin JWT + permission `catalog.read` | — | 200 → ServiceVariantAdminResponse[] |
| POST | `/api/v{version}/admin/catalog/services/{serviceId}/variants` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | ServiceVariantCreateRequest | 200 → ServiceVariantAdminResponse |
| DELETE | `/api/v{version}/admin/catalog/services/{serviceId}/variants/{id}` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | — | 204 No Content |
| GET | `/api/v{version}/admin/catalog/services/{serviceId}/variants/{id}` | _(no doc comment)_ | Admin JWT + permission `catalog.read` | — | 200 → ServiceVariantAdminResponse |
| PUT | `/api/v{version}/admin/catalog/services/{serviceId}/variants/{id}` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | ServiceVariantUpdateRequest | 200 → ServiceVariantAdminResponse |
| POST | `/api/v{version}/admin/catalog/services/{serviceId}/variants/{id}/activate` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/catalog/services/{serviceId}/variants/{id}/deactivate` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | — | 204 No Content |

### ServiceabilityMappings

Admin category/city and service/pincode serviceability mapping (SRS 12.9.2, task 111): which categories are active in which city, which services are active in which pincode, and blackout/suspension via deactivation. Gated behind the "serviceability" module, same as `GeographyController`.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/serviceability-mappings/categories` | _(no doc comment)_ | Admin JWT + permission `serviceability.read` | — | 200 → CategoryLookupResponse[] |
| GET | `/api/v{version}/admin/serviceability-mappings/category-city` | _(no doc comment)_ | Admin JWT + permission `serviceability.read` | — | 200 → CategoryCityMappingResponse[] |
| POST | `/api/v{version}/admin/serviceability-mappings/category-city` | _(no doc comment)_ | Admin JWT + permission `serviceability.write` | CategoryCityMappingCreateRequest | 200 → CategoryCityMappingResponse |
| POST | `/api/v{version}/admin/serviceability-mappings/category-city/{id}/activate` | _(no doc comment)_ | Admin JWT + permission `serviceability.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/serviceability-mappings/category-city/{id}/deactivate` | Suspends a category's serviceability in a city (SRS 12.9.2 "Service blackout in selected areas"). | Admin JWT + permission `serviceability.write` | — | 204 No Content |
| GET | `/api/v{version}/admin/serviceability-mappings/coverage-gaps` | docs/OPEN-FIXES-FEATURES.csv "Serviceability and provider skills... Service to pincode mapping": a warning list of service/pincode pairs where an active provider already has matching skill + area coverage but no active serviceability mapping exists, so the pincode still shows the service as unbookable despite a qualified provider already being onboarded there. | Admin JWT + permission `serviceability.read` | — | 200 → ServiceabilityCoverageGapResponse[] |
| GET | `/api/v{version}/admin/serviceability-mappings/mapped-with-coverage` | Coverage gap map, fourth grid category - active service/pincode mappings with active provider coverage; see `ListMappedPincodesWithActiveProviderCoverageAsync`. | Admin JWT + permission `serviceability.read` | — | 200 → MappedPincodeWithActiveProviderCoverageResponse[] |
| GET | `/api/v{version}/admin/serviceability-mappings/mapped-without-coverage` | docs/OPEN-FIXES-FEATURES.csv "Admin Web, Proposed new page, Coverage gap map": the third grid category - active service/pincode mappings with no active provider actually able to fulfil them. Informational only; see `ListMappedPincodesWithoutProviderCoverageAsync`. | Admin JWT + permission `serviceability.read` | — | 200 → MappedPincodeWithoutProviderCoverageResponse[] |
| GET | `/api/v{version}/admin/serviceability-mappings/service-pincode` | _(no doc comment)_ | Admin JWT + permission `serviceability.read` | — | 200 → ServicePincodeMappingResponse[] |
| POST | `/api/v{version}/admin/serviceability-mappings/service-pincode` | _(no doc comment)_ | Admin JWT + permission `serviceability.write` | ServicePincodeMappingCreateRequest | 200 → ServicePincodeMappingResponse |
| POST | `/api/v{version}/admin/serviceability-mappings/service-pincode/{id}/activate` | _(no doc comment)_ | Admin JWT + permission `serviceability.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/serviceability-mappings/service-pincode/{id}/deactivate` | Suspends a service's serviceability in a pincode (SRS 12.9.2 "Temporary service suspension"). | Admin JWT + permission `serviceability.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/serviceability-mappings/service-pincode/{id}/pin` | docs/OPEN-FIXES-FEATURES.csv "Service to pincode mapping" follow-up: pins this mapping's active state as admin-owned, so `AutoEnableProviderCoverageAsync`/`AutoDisableUnservedMappingsAsync` skip it entirely from now on. | Admin JWT + permission `serviceability.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/serviceability-mappings/service-pincode/{id}/unpin` | Hands this mapping's active state back to auto-enable/auto-disable. | Admin JWT + permission `serviceability.write` | — | 204 No Content |
| GET | `/api/v{version}/admin/serviceability-mappings/service-pincode/{mappingId}/providers` | Drill-down for a "mapped with coverage" row - which providers cover it. | Admin JWT + permission `serviceability.read` | — | 200 → MappingCoveringProviderResponse[] |
| GET | `/api/v{version}/admin/serviceability-mappings/services` | _(no doc comment)_ | Admin JWT + permission `serviceability.read` | — | 200 → ServiceLookupResponse[] |
| GET | `/api/v{version}/admin/serviceability-mappings/unmapped-active-services` | docs/OPEN-FIXES-FEATURES.csv "Service pincode mapping coverage": a warning list, not a blocker - every active service that has no active pincode mapping anywhere, so an admin can catch a launched-but- unbookable service (like the AC installation flagship service the CSV row describes) before a customer does. | Admin JWT + permission `serviceability.read` | — | 200 → UnmappedActiveServiceResponse[] |

### Services

Admin service/package management (SRS 12.6, task 105): CRUD over the full field set, service option flags (12.6.3), gallery media, activation and featuring. Gated behind the "catalog" permission module, same as `CategoriesController` (SRS 12.5-12.7 share one module).

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/catalog/services` | _(no doc comment)_ | Admin JWT + permission `catalog.read` | — | 200 → ServiceAdminResponse[] |
| POST | `/api/v{version}/admin/catalog/services` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | ServiceCreateRequest | 200 → ServiceAdminResponse |
| GET | `/api/v{version}/admin/catalog/services/health` | docs/OPEN-FIXES-FEATURES.csv "Admin Web, Proposed new page, Catalog health": active services missing a city price, a cover image, an active serviceability mapping, or that have never been booked. Warning/audit only, same non-destructive approach as ServiceabilityMappingsController's unmapped-active-services and coverage-gap endpoints - see `ListHealthIssuesAsync`. | Admin JWT + permission `catalog.read` | — | 200 → CatalogHealthIssueResponse[] |
| GET | `/api/v{version}/admin/catalog/services/{id}` | _(no doc comment)_ | Admin JWT + permission `catalog.read` | — | 200 → ServiceAdminResponse |
| PUT | `/api/v{version}/admin/catalog/services/{id}` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | ServiceUpdateRequest | 200 → ServiceAdminResponse |
| POST | `/api/v{version}/admin/catalog/services/{id}/activate` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/catalog/services/{id}/deactivate` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/catalog/services/{id}/feature` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | — | 204 No Content |
| GET | `/api/v{version}/admin/catalog/services/{id}/media` | _(no doc comment)_ | Admin JWT + permission `catalog.read` | — | 200 → ServiceMediaResponse[] |
| POST | `/api/v{version}/admin/catalog/services/{id}/media` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | ServiceMediaCreateRequest | 200 → ServiceMediaResponse |
| DELETE | `/api/v{version}/admin/catalog/services/{id}/media/{mediaId}` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/catalog/services/{id}/unfeature` | _(no doc comment)_ | Admin JWT + permission `catalog.write` | — | 204 No Content |

### Slots

Admin slot configuration (SRS 12.10, tasks 113a-e): recurring windows and their day-of-week rules, holiday/blackout dates, per-city booking cutoffs and advance-booking limits, per-window capacity, and one-off availability overrides. Gated behind the "slots" permission module - distinct from `SlotsController`, which serves the unauthenticated customer-facing availability computed from this configuration.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/slots/blackouts` | _(no doc comment)_ | Admin JWT + permission `slots.read` | — | 200 → SlotBlackoutAdminResponse[] |
| POST | `/api/v{version}/admin/slots/blackouts` | _(no doc comment)_ | Admin JWT + permission `slots.write` | SlotBlackoutCreateRequest | 200 → SlotBlackoutAdminResponse |
| DELETE | `/api/v{version}/admin/slots/blackouts/{id}` | _(no doc comment)_ | Admin JWT + permission `slots.write` | — | 204 No Content |
| GET | `/api/v{version}/admin/slots/booking-policies` | _(no doc comment)_ | Admin JWT + permission `slots.read` | — | 200 → SlotBookingPolicyAdminResponse[] |
| PUT | `/api/v{version}/admin/slots/booking-policies` | _(no doc comment)_ | Admin JWT + permission `slots.write` | SlotBookingPolicyUpsertRequest | 200 → SlotBookingPolicyAdminResponse |
| GET | `/api/v{version}/admin/slots/categories` | _(no doc comment)_ | Admin JWT + permission `slots.read` | — | 200 → CategoryLookupResponse[] |
| GET | `/api/v{version}/admin/slots/cities` | _(no doc comment)_ | Admin JWT + permission `slots.read` | — | 200 → SlotCityLookupResponse[] |
| GET | `/api/v{version}/admin/slots/overrides` | _(no doc comment)_ | Admin JWT + permission `slots.read` | — | 200 → SlotAvailabilityOverrideAdminResponse[] |
| POST | `/api/v{version}/admin/slots/overrides` | _(no doc comment)_ | Admin JWT + permission `slots.write` | SlotAvailabilityOverrideCreateRequest | 200 → SlotAvailabilityOverrideAdminResponse |
| DELETE | `/api/v{version}/admin/slots/overrides/{id}` | _(no doc comment)_ | Admin JWT + permission `slots.write` | — | 204 No Content |
| GET | `/api/v{version}/admin/slots/services` | _(no doc comment)_ | Admin JWT + permission `slots.read` | — | 200 → ServiceLookupResponse[] |
| GET | `/api/v{version}/admin/slots/windows` | _(no doc comment)_ | Admin JWT + permission `slots.read` | — | 200 → SlotWindowAdminResponse[] |
| POST | `/api/v{version}/admin/slots/windows` | _(no doc comment)_ | Admin JWT + permission `slots.write` | SlotWindowCreateRequest | 200 → SlotWindowAdminResponse |
| PUT | `/api/v{version}/admin/slots/windows/{id}` | _(no doc comment)_ | Admin JWT + permission `slots.write` | SlotWindowUpdateRequest | 200 → SlotWindowAdminResponse |
| POST | `/api/v{version}/admin/slots/windows/{id}/activate` | _(no doc comment)_ | Admin JWT + permission `slots.write` | — | 204 No Content |
| PATCH | `/api/v{version}/admin/slots/windows/{id}/capacity` | _(no doc comment)_ | Admin JWT + permission `slots.write` | SlotWindowCapacityUpdateRequest | 200 → SlotWindowAdminResponse |
| POST | `/api/v{version}/admin/slots/windows/{id}/deactivate` | _(no doc comment)_ | Admin JWT + permission `slots.write` | — | 204 No Content |

### SubscriptionPlans

Admin CRUD for subscription plans (PRODUCT-ENHANCEMENTS.md #1, task 180): price, billing cycle, benefits, active window. Read-only actions require "subscription.read"; mutating actions require "subscription.write" - same per-action split as `CouponsController`.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/subscription-plans` | _(no doc comment)_ | Admin JWT + permission `subscription.read` | — | 200 → SubscriptionPlanAdminResponse[] |
| POST | `/api/v{version}/admin/subscription-plans` | _(no doc comment)_ | Admin JWT + permission `subscription.write` | SubscriptionPlanCreateRequest | 201 → SubscriptionPlanAdminResponse |
| GET | `/api/v{version}/admin/subscription-plans/{id}` | _(no doc comment)_ | Admin JWT + permission `subscription.read` | — | 200 → SubscriptionPlanAdminResponse |
| PUT | `/api/v{version}/admin/subscription-plans/{id}` | _(no doc comment)_ | Admin JWT + permission `subscription.write` | SubscriptionPlanUpdateRequest | 200 → SubscriptionPlanAdminResponse |
| POST | `/api/v{version}/admin/subscription-plans/{id}/activate` | _(no doc comment)_ | Admin JWT + permission `subscription.write` | — | 204 No Content |
| POST | `/api/v{version}/admin/subscription-plans/{id}/deactivate` | _(no doc comment)_ | Admin JWT + permission `subscription.write` | — | 204 No Content |

### SupportTicketDisputes

Admin dispute mark/resolve workflow on a support ticket (SRS 11.18.1 "wrong charge / pricing dispute", task 155). Gated behind "support.write" (task 96b/96c) - opening or resolving a dispute is a mutating support action, same module as the rest of the ticket workflow. The real capability lives in `IDisputeResolutionService`; this controller only adds the permission gate in front of it.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| POST | `/api/v{version}/support-tickets/{ticketId}/dispute` | Admin opens a formal dispute investigation on a ticket. | Admin JWT + permission `support.write` | — | 200 → SupportTicketDetailResponse |
| POST | `/api/v{version}/support-tickets/{ticketId}/dispute/resolve` | Admin resolves an open dispute as refund (valid) or close/rework (invalid). | Admin JWT + permission `support.write` | ResolveDisputeRequest | 200 → DisputeResolutionResponse |

### SupportTickets

Admin ticket workflow (SRS 12.14, 16.2, tasks 120a-f): search/detail across every customer, assign/unassign, respond, escalate, resolve/close, and link a booking. Read-only actions require "support.read"; every mutating action requires "support.write" (task 96b/96c) - same per-action split as `CouponsController`. The formal dispute mark/resolve sub-flow (task 155) stays on its own `SupportTicketDisputesController` - this controller does not duplicate it. <para> Booking link (task 120e): `LinkBooking` only records/validates a booking reference and returns its read-only summary (`LinkedBookingSummaryResponse`) - there is no admin booking-management API in this codebase yet to call cancel/refund against (BookingMgmt is a separate, not-yet-landed vertical). TODO(BookingMgmt): once an admin booking controller exists, add cancel/refund shortcut endpoints here (or have the admin-web UI call that controller directly using the linked booking's id) instead of read-only summary only. </para>

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/support-tickets` | Filtered/paginated ticket search across every customer (SRS 12.14.1: Ticket ID via GetById, Booking ID, Customer, Category, Priority, Status, Assigned agent, Date range). | Admin JWT + permission `support.read` | — | 200 → AdminSupportTicketSearchResponse |
| GET | `/api/v{version}/admin/support-tickets/assignable-admins` | Every active admin, for the "assign to" picker (task 120a). Registered before the "{id:guid}" route below so it is never captured as an id. | Admin JWT + permission `support.read` | — | 200 → AssignableAdminResponse[] |
| GET | `/api/v{version}/admin/support-tickets/{id}` | Full ticket detail - comment thread, assignee, and linked booking summary if any (SRS 16.3, task 120f). | Admin JWT + permission `support.read` | — | 200 → AdminSupportTicketDetailResponse |
| POST | `/api/v{version}/admin/support-tickets/{id}/assign` | Assigns a ticket to an admin/agent (SRS 12.14.2 "Assign to team/user", task 120a). | Admin JWT + permission `support.write` | AssignSupportTicketRequest | 200 → AdminSupportTicketDetailResponse |
| POST | `/api/v{version}/admin/support-tickets/{id}/close` | Moves the ticket to Closed (SRS 12.14.2 "Mark resolved/closed", task 120d). | Admin JWT + permission `support.write` | — | 200 → AdminSupportTicketDetailResponse |
| POST | `/api/v{version}/admin/support-tickets/{id}/escalate` | Moves the ticket to Escalated (SRS 12.14.2 "Mark escalated", task 120c). | Admin JWT + permission `support.write` | — | 200 → AdminSupportTicketDetailResponse |
| POST | `/api/v{version}/admin/support-tickets/{id}/link-booking` | Attaches (or re-attaches) a booking to the ticket (SRS 12.14.2 "Link... booking action", task 120e). See this controller's own doc comment for the cancel/refund-shortcut TODO. | Admin JWT + permission `support.write` | LinkSupportTicketBookingRequest | 200 → AdminSupportTicketDetailResponse |
| POST | `/api/v{version}/admin/support-tickets/{id}/resolve` | Moves the ticket to Resolved (SRS 12.14.2 "Mark resolved/closed", task 120d) - for tickets with no formal dispute open; use `SupportTicketDisputesController` instead when one is. | Admin JWT + permission `support.write` | ResolveSupportTicketRequest | 200 → AdminSupportTicketDetailResponse |
| POST | `/api/v{version}/admin/support-tickets/{id}/respond` | Appends an admin response to the ticket's comment thread (SRS 12.14.2 "Add response/note", task 120b). | Admin JWT + permission `support.write` | AddSupportTicketCommentRequest | 200 → AdminSupportTicketDetailResponse |
| POST | `/api/v{version}/admin/support-tickets/{id}/unassign` | Clears a ticket's current assignment (task 120a). | Admin JWT + permission `support.write` | — | 200 → AdminSupportTicketDetailResponse |

### SystemSettings

Admin-configurable system settings/feature-flag management (SRS 12.19, tasks 131a-131h): booking, slot, cancellation, reschedule, tax, wallet, coupon and feature-flag settings groups, each independently readable/editable. Gated behind "settings.read"/"settings.write" - the same two policies every module's `AdminModules` code already generates via `AdminPermissionCatalog`, so no new policy registration was needed.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/settings` | Every settings group at once, for the admin Settings landing page. | Admin JWT + permission `settings.read` | — | 200 → AllSystemSettingsResponse |
| GET | `/api/v{version}/settings/booking` | _(no doc comment)_ | Admin JWT + permission `settings.read` | — | 200 → BookingSettings |
| PUT | `/api/v{version}/settings/booking` | _(no doc comment)_ | Admin JWT + permission `settings.write` | BookingSettings | 200 → BookingSettings |
| GET | `/api/v{version}/settings/cancellation` | _(no doc comment)_ | Admin JWT + permission `settings.read` | — | 200 → CancellationSettings |
| PUT | `/api/v{version}/settings/cancellation` | _(no doc comment)_ | Admin JWT + permission `settings.write` | CancellationSettings | 200 → CancellationSettings |
| GET | `/api/v{version}/settings/coupon` | _(no doc comment)_ | Admin JWT + permission `settings.read` | — | 200 → CouponSettings |
| PUT | `/api/v{version}/settings/coupon` | _(no doc comment)_ | Admin JWT + permission `settings.write` | CouponSettings | 200 → CouponSettings |
| GET | `/api/v{version}/settings/features` | _(no doc comment)_ | Admin JWT + permission `settings.read` | — | 200 → FeatureFlagSettings |
| PUT | `/api/v{version}/settings/features` | _(no doc comment)_ | Admin JWT + permission `settings.write` | FeatureFlagSettings | 200 → FeatureFlagSettings |
| GET | `/api/v{version}/settings/reschedule` | _(no doc comment)_ | Admin JWT + permission `settings.read` | — | 200 → RescheduleSettings |
| PUT | `/api/v{version}/settings/reschedule` | _(no doc comment)_ | Admin JWT + permission `settings.write` | RescheduleSettings | 200 → RescheduleSettings |
| GET | `/api/v{version}/settings/slot` | _(no doc comment)_ | Admin JWT + permission `settings.read` | — | 200 → SlotSettings |
| PUT | `/api/v{version}/settings/slot` | _(no doc comment)_ | Admin JWT + permission `settings.write` | SlotSettings | 200 → SlotSettings |
| GET | `/api/v{version}/settings/tax` | _(no doc comment)_ | Admin JWT + permission `settings.read` | — | 200 → TaxSettings |
| PUT | `/api/v{version}/settings/tax` | _(no doc comment)_ | Admin JWT + permission `settings.write` | TaxSettings | 200 → TaxSettings |
| GET | `/api/v{version}/settings/wallet` | _(no doc comment)_ | Admin JWT + permission `settings.read` | — | 200 → WalletSettings |
| PUT | `/api/v{version}/settings/wallet` | _(no doc comment)_ | Admin JWT + permission `settings.write` | WalletSettings | 200 → WalletSettings |

### WalletTopUps

Admin view of customers' wallet top-ups: a filterable list that says which ones need attention (stuck, or a gateway callback that disagreed with the amount asked for), one top-up's detail, and "Reconcile now" to ask the gateway about a stuck one. Part of the Payments module - reading needs "payments.read", reconciling needs "payments.write" - because it is money moving through the payment gateway, the same surface `PaymentsController` covers for bookings. Reconciling cannot invent a credit: it runs the same gateway check, behind the same conditional update, as the background sweep. Crediting a wallet by hand stays the customer page's audited wallet adjustment.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/admin/wallet-top-ups` | Top-ups, newest first, filterable by status, "needs attention", search text and creation date, with the day's summary figures. | Admin JWT + permission `payments.read` | — | 200 → PagedAdminWalletTopUpResponse |
| GET | `/api/v{version}/admin/wallet-top-ups/{topUpId}` | One top-up. 404 when the id is not a top-up. | Admin JWT + permission `payments.read` | — | 200 → AdminWalletTopUpResponse |
| POST | `/api/v{version}/admin/wallet-top-ups/{topUpId}/reconcile` | Asks the gateway how this top-up ended and applies a definite answer (credit on success, Failed on a declined payment, nothing while the gateway still says pending). Audited. Safe to repeat. | Admin JWT + permission `payments.write` | — | 200 → AdminWalletTopUpReconcileResponse |

## PROVIDER-API (provider mobile/web)

### Auth

Provider authentication (task 146a/146b, PROVIDER.md API surface "Auth"). Task 372 added email+password login/registration/reset alongside OTP, mirroring consumer-api's `AuthController` in full.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| POST | `/api/v{version}/auth/login/otp` | Send a login OTP to an already-registered mobile number. | Public | RequestProviderLoginOtpRequest | 204 No Content |
| POST | `/api/v{version}/auth/login/otp/verify` | Login via mobile OTP. | Public | LoginProviderWithOtpRequest | 200 → ProviderLoginResponse |
| POST | `/api/v{version}/auth/login/password` | Login via email + password, when password auth is enabled (task 372). | Public | LoginProviderWithPasswordRequest | 200 → ProviderLoginResponse |
| POST | `/api/v{version}/auth/logout` | Invalidate a session's refresh token. | Public | LogoutProviderRequest | 204 No Content |
| POST | `/api/v{version}/auth/password/forgot` | Step 1 of the reset flow (task 372). Always 200 — see `RequestResetAsync` for why an unknown address is not reported as such. | Public | ForgotProviderPasswordRequest | 200 OK |
| POST | `/api/v{version}/auth/password/reset` | Step 2: set the new password once the OTP verifies (task 372). | Public | ResetProviderPasswordRequest | 204 No Content |
| POST | `/api/v{version}/auth/refresh` | Exchange a still-valid refresh token for a new access+refresh pair (rotation). | Public | RefreshProviderTokenRequest | 200 → ProviderLoginResponse |
| POST | `/api/v{version}/auth/registration` | Step 2: complete registration once the OTP has been verified. | Public | RegisterProviderRequest | 201 → ProviderSummaryResponse |
| POST | `/api/v{version}/auth/registration/email` | Email-first registration step 2: complete registration once the email OTP has been verified. | Public | RegisterProviderWithEmailRequest | 201 → ProviderSummaryResponse |
| POST | `/api/v{version}/auth/registration/email-otp` | Email-first registration step 1: send an OTP to an email address instead of a mobile number. | Public | RequestProviderRegistrationEmailOtpRequest | 204 No Content |
| POST | `/api/v{version}/auth/registration/otp` | Step 1: send a registration OTP to a mobile number. | Public | RequestProviderRegistrationOtpRequest | 204 No Content |

### Availability

Provider availability windows and blackout dates (task 149b, PROVIDER.md API surface "Availability"). Every action is scoped to the caller's own provider id taken from the JWT (SRS 28.3 IDOR), same pattern as `ProfileController`.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/availability` | Weekly availability windows plus blackout dates in one view. | Provider JWT | — | 200 → ProviderAvailabilityResponse |
| GET | `/api/v{version}/availability/blackout-dates` | List blackout dates. | Provider JWT | — | 200 → ProviderBlackoutDateResponse[] |
| POST | `/api/v{version}/availability/blackout-dates` | Add a blackout date range (leave, illness, personal unavailability). | Provider JWT | AddProviderBlackoutDateRequest | 201 → ProviderBlackoutDateResponse |
| DELETE | `/api/v{version}/availability/blackout-dates/{id}` | Remove a blackout date. | Provider JWT | — | 204 No Content |
| PUT | `/api/v{version}/availability/windows` | Replace the provider's whole recurring weekly schedule. | Provider JWT | UpdateProviderAvailabilityWindowsRequest | 200 → ProviderAvailabilityWindowResponse[] |

### Catalog

Read-only category/service lookup for the provider skills picker (task 205, PROVIDER.md's Capability &amp; Coverage domain). Before this controller, `ProfileController`'s skills endpoints took a bare `categoryId`/ `serviceId` with no lookup to resolve them against - provider-web's `SkillsSection` had providers hand-type raw GUIDs. This reuses the existing admin-facing `ICategoryManagementService`/ `IServiceManagementService` (same services `AdminApi.Controllers.CategoriesController`/`ServicesController` call) rather than adding new query services - only the response shape is new, trimmed to id/name (no SEO/media/pricing fields an admin screen needs but a provider picker never should see).

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/catalog/categories` | Active categories, for the skills picker's category dropdown. | Provider JWT | — | 200 → ProviderCatalogCategoryResponse[] |
| GET | `/api/v{version}/catalog/services` | Active services, optionally filtered to one category, for the skills picker's service dropdown. | Provider JWT | — | 200 → ProviderCatalogServiceResponse[] |

### Chat

Provider-facing chat over a booking thread (task 193's other reply view, PRODUCT-ENHANCEMENTS.md "3. IN-APP CHAT" - ConsumerApi's `ChatController` is the customer side, AdminApi's is the support console, this is the provider app/portal one). Every action is scoped to the caller's own provider id taken from the JWT - never a route/body parameter - same convention as `JobsController`. REST is the actual send/read path (works with or without a live socket); `ChatHub` (mapped at `/hubs/chat`, see Program.cs) only pushes live updates to a thread once a client has GETten/POSTed through here at least once.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| POST | `/api/v{version}/chat/threads` | Returns the thread for a booking this provider is the live assignment on, creating it on first use. | Provider JWT | GetOrCreateChatThreadRequest | 200 → ChatThreadResponse |
| GET | `/api/v{version}/chat/threads/{threadId}/messages` | Paginated history, oldest first. | Provider JWT | — | 200 → ChatMessagePageResult |
| POST | `/api/v{version}/chat/threads/{threadId}/messages` | Sends a message on a thread for a booking this provider is the live assignment on. | Provider JWT | SendChatMessageRequest | 201 → ChatMessageResponse |
| POST | `/api/v{version}/chat/threads/{threadId}/read` | Marks every message not sent by this provider as read. | Provider JWT | — | 204 No Content |

### CustomerRatings

Provider-side rating of the customer on a completed job - the reverse direction of consumer-api's `ReviewsController` (bidirectional reviews). Every action is scoped to the caller's own provider id from the JWT, same IDOR-safe pattern as `JobsController`.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/jobs/{bookingId}/customer-rating` | The rating already submitted for this job, if any. | Provider JWT | — | 200 → CustomerRatingResponse |
| POST | `/api/v{version}/jobs/{bookingId}/customer-rating` | Submits the job's one rating of the customer. | Provider JWT | SubmitCustomerRatingRequest | 201 → CustomerRatingResponse |
| GET | `/api/v{version}/jobs/{bookingId}/customer-rating/eligibility` | Whether this job is eligible for a rating right now. | Provider JWT | — | 200 → CustomerRatingEligibilityResponse |

### DeviceTokens

Push device token registration for providers (task 277), mirroring consumer-api's `DeviceTokensController` field for field - the only difference is the scheme and that the caller's id becomes a `DeviceTokenOwner` provider, not a customer.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/device-tokens` | The caller's active registered devices. | Provider JWT | — | 200 → DeviceTokenResponse[] |
| POST | `/api/v{version}/device-tokens` | Registers (or re-registers) the caller's device for push notifications. | Provider JWT | RegisterDeviceTokenRequest | 200 → DeviceTokenResponse |
| DELETE | `/api/v{version}/device-tokens/{id}` | Deactivates a device (e.g. on logout). | Provider JWT | — | 204 No Content |

### Earnings

Provider earnings and payouts (task 149c, PROVIDER.md API surface "Earnings" - summary, ledger, payouts list/detail), wired to a real `IProviderEarningsService` backed by the ledger/payout entities task 148 introduced. Every action is scoped to the caller's own provider id taken from the JWT (SRS 28.3 IDOR), same pattern as `ProfileController`.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/earnings/jobs` | Job-level earnings ledger for the caller (docs/OPEN-FIXES-FEATURES.csv "Earnings detail and payouts") - one row per completed job with its gross/commission/net breakdown and payout status, newest first, optionally narrowed to a completion-date range. | Provider JWT | — | 200 → ProviderEarningJobSearchResponse |
| GET | `/api/v{version}/earnings/ledger` | Append-only earnings ledger entries for the caller, newest first. | Provider JWT | — | 200 → ProviderEarningLedgerEntryResponse[] |
| GET | `/api/v{version}/earnings/payouts` | Payout batches for the caller. | Provider JWT | — | 200 → ProviderPayoutSearchResponse |
| GET | `/api/v{version}/earnings/payouts/{id}` | One payout's detail - 404s if it belongs to a different provider. | Provider JWT | — | 200 → ProviderPayoutResponse |
| GET | `/api/v{version}/earnings/summary` | Rolled-up earnings summary (current balance) for the caller. | Provider JWT | — | 200 → ProviderEarningsSummaryResponse |

### FeatureFlags

Public provider-facing feature flags (SRS 12.19 "Feature flags"). No auth - this gates navigation/UI before or without a session, same reasoning as `GeographyController`. Projects the admin-only `FeatureFlagSettings` group down to `ProviderFeatureFlagsResponse` - never the full admin settings shape, which would leak customer-side flags to an unauthenticated caller.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/feature-flags` | _(no doc comment)_ | Public | — | 200 → ProviderFeatureFlagsResponse |

### Geography

Read-only city/zone/pincode lookup for the provider service-areas picker (task 205, PROVIDER.md's Capability &amp; Coverage domain). Before this controller, `ProfileController`'s service-areas endpoints took bare `cityId`/`zoneId`/`pincodeId` with no lookup to resolve them against - provider-web's `ServiceAreasSection` had providers hand-type raw GUIDs. Reuses the existing admin-facing `IGeographyManagementService` (same service `AdminApi.Controllers.GeographyController` calls) rather than adding a new query service - only the response shape is new, trimmed to id/name (matching `ProviderServiceAreaInput`'s cityId/zoneId/pincodeId shape).

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/geography/cities` | Active cities, for the service-areas picker's city dropdown. | Provider JWT | — | 200 → ProviderGeographyCityResponse[] |
| GET | `/api/v{version}/geography/pincodes` | Active pincodes, optionally filtered to one city, for the service-areas picker's pincode dropdown. | Provider JWT | — | 200 → ProviderGeographyPincodeResponse[] |
| GET | `/api/v{version}/geography/zones` | Active zones, optionally filtered to one city, for the service-areas picker's zone dropdown. | Provider JWT | — | 200 → ProviderGeographyZoneResponse[] |

### Jobs

Provider jobs (task 149a, PROVIDER.md API surface "Jobs" - list/detail, accept/reject/start/complete, completion proof upload), wired to a real `IProviderJobService` backed by the `BookingProviderAssignment` bridge entity (task 147). Every action is scoped to the caller's own provider id taken from the JWT - there is no route or body parameter that could name a different provider (SRS 28.3 IDOR), same pattern as `ProfileController`/`AvailabilityController`.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/jobs` | List jobs ever assigned to the caller, optionally filtered by status and/or slot date. | Provider JWT | — | 200 → ProviderJobSearchResponse |
| GET | `/api/v{version}/jobs/{bookingId}` | Get one job's detail. | Provider JWT | — | 200 → ProviderJobDetailResponse |
| POST | `/api/v{version}/jobs/{bookingId}/accept` | Accept an assigned job. | Provider JWT | — | 200 → ProviderJobDetailResponse |
| POST | `/api/v{version}/jobs/{bookingId}/arrived` | Mark an en-route job as arrived - the provider has reached the address but has not begun the work (task 270). Idempotent on a re-tap, same as `MarkEnRoute`. | Provider JWT | — | 200 → ProviderJobDetailResponse |
| POST | `/api/v{version}/jobs/{bookingId}/complete` | Mark an in-progress job as completed. | Provider JWT | — | 200 → ProviderJobDetailResponse |
| POST | `/api/v{version}/jobs/{bookingId}/completion-photos` | Uploads one camera/gallery photo for job-completion evidence and returns a ref to feed into `SubmitCompletionVerification`'s `photoRefs`. Validated here rather than via a FluentValidation record validator since the payload is a multipart file, not JSON: content-type is checked against an image allowlist and size is capped before anything is read into memory or written to disk - both real trust-boundary checks (SRS "never trust client data"), not just yak-shaving, since a client can lie about either. | Provider JWT | object | 200 → UploadCompletionPhotoResponse |
| POST | `/api/v{version}/jobs/{bookingId}/completion-proof` | Attach completion proof (photo/file reference) to a job. | Provider JWT | UploadJobCompletionProofRequest | 200 → ProviderJobDetailResponse |
| GET | `/api/v{version}/jobs/{bookingId}/completion-verification` | The completion evidence submitted for this job, if any (task 198). | Provider JWT | — | 200 → BookingCompletionProofResponse |
| POST | `/api/v{version}/jobs/{bookingId}/completion-verification` | Submits (or resubmits) the completion evidence - photos plus checklist - required before `Complete` will succeed (tasks 195-197). Distinct from `UploadCompletionProof`'s single legacy proof-ref field. | Provider JWT | SubmitCompletionProofRequest | 200 → BookingCompletionProofResponse |
| POST | `/api/v{version}/jobs/{bookingId}/en-route` | Mark an accepted job as en route - the provider has set off for the customer's address (task 270). Optional: `Start` still works straight from an accepted job, so a provider who never taps this is not blocked. Re-tapping while already en route answers 200 with the unchanged job rather than a conflict, so a client retrying over a bad connection is not punished for it. | Provider JWT | — | 200 → ProviderJobDetailResponse |
| POST | `/api/v{version}/jobs/{bookingId}/extend-response-deadline` | Row 38, docs/OPEN-FIXES-FEATURES.csv: extends this job's response deadline - called by provider-web when its own accept attempt failed for a reason that was not the provider's fault (a transient error, not the 401/session-expiry case the client already retries transparently), so the response window is not silently lost while they retry. | Provider JWT | — | 200 → ProviderJobDetailResponse |
| POST | `/api/v{version}/jobs/{bookingId}/location` | Report the provider's current position for a job in flight (task 269). Fails closed: 403 unless the caller is the provider on this booking's live assignment, 409 unless the job has been accepted and the booking is still in a trackable state - so no position is ever collected before the provider accepts or after the job ends. Accepted fixes answer 200; fixes dropped by the per-booking throttle answer 202, since the client did nothing wrong and must not retry them. | Provider JWT | RecordProviderLocationRequest | 200 → RecordProviderLocationResponse |
| POST | `/api/v{version}/jobs/{bookingId}/reject` | Reject an assigned job (task 159 - returns the booking to the assignable pool for admin reassignment). | Provider JWT | RejectJobRequest | 200 → ProviderJobDetailResponse |
| POST | `/api/v{version}/jobs/{bookingId}/start` | Mark an accepted job as started (provider has arrived / begun work). | Provider JWT | — | 200 → ProviderJobDetailResponse |

### Notifications

A provider's in-app notification inbox (Provider Management UX pass) - backs the header bell and the dedicated /notifications list in provider-web. Every action is scoped to the caller's own provider id taken from the JWT (SRS 28.3 IDOR), same pattern as `EarningsController`.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/notifications` | Newest first, paged, plus the caller's current unread count. | Provider JWT | — | 200 → ProviderNotificationListResponse |
| POST | `/api/v{version}/notifications/read-all` | Marks every one of the caller's unread notifications read in one call. | Provider JWT | — | 204 No Content |
| POST | `/api/v{version}/notifications/{id}/read` | Marks a single notification read - 404s if it belongs to a different provider. | Provider JWT | — | 200 → ProviderNotificationResponse |

### Profile

Provider profile, KYC, service areas and skills (task 149a, PROVIDER.md API surface "Profile/Onboarding"). Every action is scoped to the caller's own provider id taken from the JWT — there is no route or body parameter that could name a different provider (SRS 28.3 IDOR), mirroring consumer-api's `CustomerProfileController`/`CustomerAddressController`.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| DELETE | `/api/v{version}/profile` | Permanently deletes the caller's own account (right to erasure). Job/earnings history is retained under this provider id for financial/legal reasons, but personal fields are anonymized and every active session is revoked immediately - login is impossible from this point on. Mirrors consumer-api's `CustomerProfileController.DeleteAccount`. | Provider JWT | — | 204 No Content |
| GET | `/api/v{version}/profile` | View profile. | Provider JWT | — | 200 → ProviderProfileResponse |
| PUT | `/api/v{version}/profile` | Edit legal name, display name and email. | Provider JWT | UpdateProviderProfileRequest | 200 → ProviderProfileResponse |
| GET | `/api/v{version}/profile/bank-account` | The caller's own structured bank account details for payouts (docs/PROVIDER.md OPEN DECISIONS #3). Sits alongside the `SubmitKycDocument` `BankAccountProof` upload - that photo remains supporting evidence; this structured record is what is actually used operationally by an admin processing a payout. | Provider JWT | — | 200 → ProviderBankAccountResponse |
| PUT | `/api/v{version}/profile/bank-account` | Submits or edits the caller's bank account details - an upsert, not an append (one row per provider, unlike KYC documents): editing already- verified details always resets verification back to Pending. The caller's provider id comes from the JWT, never from the body (SRS 28.3 IDOR) - same pattern as `SubmitKycDocument`. | Provider JWT | SubmitProviderBankAccountBody | 200 → ProviderBankAccountResponse |
| GET | `/api/v{version}/profile/go-live-status` | Go-live checklist (docs/OPEN-FIXES-FEATURES.csv "Provider Web, Proposed new page, Onboarding checklist and go-live status"): names the specific prerequisites the caller is still missing before they can start receiving work, so provider-web can show a persistent banner and a checklist naming the exact gap instead of an unexplained empty jobs list. | Provider JWT | — | 200 → ProviderGoLiveStatusResponse |
| GET | `/api/v{version}/profile/kyc` | Overall KYC picture: onboarding status plus every submitted document. | Provider JWT | — | 200 → ProviderKycStatusResponse |
| POST | `/api/v{version}/profile/kyc/documents` | Submit a KYC document. `FileRef` is a reference to an already-uploaded file (storage key/URL) — this endpoint does not itself accept a binary upload, matching `IProviderKycService`. | Provider JWT | SubmitProviderKycDocumentBody | 201 → ProviderKycDocumentResponse |
| POST | `/api/v{version}/profile/kyc/documents/upload` | Uploads a KYC document file and returns its URL for `SubmitKycDocument`'s `FileRef` — same upload-then-submit split as `UploadPhoto`/`UpdatePhoto`. Accepts PDF in addition to the photo allowlist: identity/address/bank proofs are commonly scanned or exported as PDF, unlike a profile photo. | Provider JWT | object | 200 → ProviderFileUploadResponse |
| PUT | `/api/v{version}/profile/photo` | Set or clear the profile photo (task 293). `PhotoUrl` is a reference to an already-hosted image (storage key/URL), not a binary upload - the same convention `SubmitKycDocument` uses. A new photo always re-enters admin moderation; customers see it only once it is approved. | Provider JWT | UpdateProviderPhotoRequest | 200 → ProviderProfileResponse |
| POST | `/api/v{version}/profile/photo/upload` | Uploads a profile photo file and returns its URL for `UpdatePhoto` — a separate call rather than accepting the file directly on `UpdatePhoto`, so that endpoint's existing JSON contract and validator are untouched. Mirrors provider-api's `JobsController.UploadCompletionPhoto`: content-type checked against an image allowlist and size capped before anything is read into memory or written to storage. | Provider JWT | object | 200 → ProviderFileUploadResponse |
| GET | `/api/v{version}/profile/service-areas` | List the provider's declared geography coverage. | Provider JWT | — | 200 → ProviderServiceAreaResponse[] |
| PUT | `/api/v{version}/profile/service-areas` | Replace the provider's whole geography coverage set. | Provider JWT | UpdateProviderServiceAreasRequest | 200 → ProviderServiceAreaResponse[] |
| GET | `/api/v{version}/profile/skills` | List the categories/services the provider is qualified for. | Provider JWT | — | 200 → ProviderSkillResponse[] |
| PUT | `/api/v{version}/profile/skills` | Replace the provider's whole declared skill set. | Provider JWT | UpdateProviderSkillsRequest | 200 → ProviderSkillResponse[] |

### ProviderApi

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| POST | `/api/v1/auth/dev/login-as-provider` | Dev-only QA auth bypass (Program.cs, provider-api) — issues a real provider session for a given mobile number without OTP/password. Gated by a shared-secret header, not a JWT. | Header `X-Dev-Auth-Key` must match `DevAuth:Key` config; no key configured (as in Production) makes this endpoint unusable. | DevProviderLoginRequest | 200 OK |

### Ratings

Ratings and feedback (docs/OPEN-FIXES-FEATURES.csv "Ratings and feedback"): the provider's own running average rating, review count, and recent customer reviews. Every action is scoped to the caller's own provider id taken from the JWT (SRS 28.3 IDOR), same pattern as `EarningsController`/`ProfileController`.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/ratings/reviews` | The caller's own recent reviews, newest first. | Provider JWT | — | 200 → ProviderReviewSearchResponse |
| GET | `/api/v{version}/ratings/summary` | The caller's running average rating and total review count. | Provider JWT | — | 200 → ProviderRatingsSummaryResponse |

### Referral

Refer &amp; Earn screen for providers (PROVIDER-REFERRAL.md): the caller's own referral code/share link/lifetime stats, and their own referral history. Mirrors consumer-api's `ReferralController` exactly - every action is scoped to the caller's own provider id.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/referral` | Code (lazily generated on first call), share link, and lifetime stats. | Customer JWT | — | 200 → ProviderReferralSummaryResponse |
| GET | `/api/v{version}/referral/history` | This provider's own referrals as referrer, newest first. | Customer JWT | — | 200 → ProviderReferralHistoryItemResponse[] |

### SupportTickets

Provider support tickets (Provider Management UX pass) - mirrors consumer-api's `SupportTicketsController`, scoped to the caller's own provider id from the JWT.

| Method | Path | Summary | Auth | Request | Success Response |
|---|---|---|---|---|---|
| GET | `/api/v{version}/support-tickets` | Lists all of the caller's tickets, newest first. | Provider JWT | — | 200 → ProviderSupportTicketSummaryResponse[] |
| POST | `/api/v{version}/support-tickets` | Raises a new ticket. | Provider JWT | CreateProviderSupportTicketRequest | 201 → ProviderSupportTicketDetailResponse |
| GET | `/api/v{version}/support-tickets/{id}` | Ticket detail with its full comment thread. | Provider JWT | — | 200 → ProviderSupportTicketDetailResponse |
| POST | `/api/v{version}/support-tickets/{id}/comments` | Appends a provider follow-up to the ticket's thread. | Provider JWT | AddProviderSupportTicketCommentRequest | 200 → ProviderSupportTicketDetailResponse |

<!-- END GENERATED ENDPOINT REFERENCE -->
