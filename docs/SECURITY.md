# SECURITY.md

Authentication, Authorization & Security Standards

## PURPOSE

This document defines the security standards, principles, and best practices for the Nestly platform.

It establishes consistent rules for authentication, authorization, data protection, secret management, secure development, and application security.

This document is the single source of truth for security-related standards.

## SECURITY OBJECTIVES

The application must ensure:

- Confidentiality
- Integrity
- Availability
- Accountability
- Least Privilege
- Defense in Depth
- Secure by Default

Security requirements take priority over convenience.

## AUTHENTICATION

Authentication verifies the identity of a user or system.

Project standards:

- Use ASP.NET Core Identity.
- Use JWT Access Tokens.
- Support Refresh Tokens.
- Enforce secure password policies.
- Require email verification where applicable.
- Support Multi-Factor Authentication (future-ready).

Never create custom authentication mechanisms.

## AUTHORIZATION

Authorization controls access to application resources.

Guidelines:

- Follow Role-Based Access Control (RBAC).
- Enforce least privilege.
- Validate permissions on every protected request.
- Perform authorization on the server.
- Never rely solely on frontend authorization.

## PASSWORD SECURITY

Passwords must:

- Be hashed using approved algorithms.
- Never be stored or logged in plain text.
- Meet minimum complexity requirements.
- Support secure reset workflows.

Passwords must never be reversible.

## TOKEN SECURITY

Guidelines:

- Keep access tokens short-lived.
- Rotate refresh tokens.
- Validate token expiry.
- Revoke compromised tokens.
- Transmit tokens only over HTTPS.

Never expose tokens in logs or URLs.

## SECRET MANAGEMENT

Secrets include:

- Connection strings
- API keys
- JWT signing keys
- OTP hashing pepper
- Certificates
- Third-party credentials

Rules:

- Never hardcode secrets.
- Store secrets outside source code.
- Use environment-specific configuration.
- Rotate secrets periodically.
- Restrict access to secrets.

## INPUT VALIDATION

Validate all external input.

Include:

- Required fields
- Length limits
- Data types
- Allowed values
- File validation

Reject invalid input before business processing.

## OUTPUT SECURITY

Responses should:

- Return only necessary data.
- Mask sensitive information.
- Avoid exposing internal implementation details.
- Use standardized error responses.

## DATA PROTECTION

Sensitive data should:

- Be encrypted where appropriate.
- Be masked in logs and reports.
- Be transmitted only over secure channels.
- Be retained according to business requirements.

Protect Personally Identifiable Information (PII).

## API SECURITY

All APIs should:

- Require authentication where appropriate.
- Validate authorization.
- Validate all input.
- Return consistent error responses.
- Apply rate limiting where required.

Public endpoints should be explicitly identified.

## FILE SECURITY

For file uploads:

- Validate file type.
- Validate file size.
- Sanitize file names.
- Scan uploaded files when applicable.
- Store files outside publicly accessible locations.

Never trust uploaded content.

## SESSION SECURITY

Guidelines:

- Expire inactive sessions.
- Invalidate sessions after logout.
- Prevent session fixation.
- Protect against session hijacking.

Where the web apps keep the signed-in session (customer-web and provider-web, `src/lib/session-storage.ts`):

- In a browser tab: `sessionStorage`, so the session ends with the tab.
- In an app installed to the home screen: `localStorage`. An installed app has no tab to keep open, so with
  `sessionStorage` a customer or provider would be signed out every time they closed it, and a provider would
  miss job offers until they logged in again. Installed apps are the main way both are used, on phones.
- Signing out clears both stores.
- Known limitation, unchanged: tokens in Web Storage can be read by any script on the origin, so an XSS bug
  is a session-theft bug. Moving token issuance to an httpOnly cookie is the real fix and is tracked as
  hardening work. Keeping the session on an installed phone widens that window. What limits it: the access
  token lasts 15 minutes, the refresh token lasts 30 days and is revoked and replaced every time it is used,
  and signing out revokes it.

## COMMUNICATION SECURITY

All communication must:

- Use HTTPS.
- Use modern TLS protocols.
- Prevent insecure transport.
- Protect sensitive headers.

Never transmit sensitive information over insecure channels.

## LOGGING & AUDITING

Log security-relevant events such as:

- Login attempts
- Failed authentication
- Permission failures
- Administrative actions
- Critical configuration changes

Never log:

- Passwords
- Tokens
- Secrets
- Sensitive personal data

## DEPENDENCY SECURITY

Use only trusted dependencies.

Requirements:

- Keep libraries updated.
- Remove unused packages.
- Monitor known vulnerabilities.
- Prefer officially supported packages.

## SECURE DEVELOPMENT

Developers should:

- Follow secure coding practices.
- Validate all inputs.
- Handle errors safely.
- Minimize attack surface.
- Avoid unnecessary privileges.

Security should be considered throughout development, not added later.

## WALLET TOP-UPS (MONEY HANDLING)

A top-up moves customers' money into a balance the business is then liable for, so it is
held to a stricter standard than most endpoints. What is enforced, and where:

- **Credited exactly once.** A gateway can report one outcome by several routes (webhook,
  redelivery, the return page asking us to verify, the reconciliation sweep) and they can
  race. `WalletTopUpService.ResolveAsync` is the only place the wallet is credited: a
  conditional UPDATE out of `Pending` plus the ledger credit in one `Serializable`
  transaction, so one route wins and the rest change nothing.
- **The amount is checked.** A callback whose paid amount differs from the amount saved
  for that top-up is refused and never credited.
- **Callbacks are authenticated.** The webhook signature is verified before anything is
  read from the body; an order we do not know is a 404, not a guess.
- **A late success is not lost.** A top-up the sweep wrote off as `Failed` still credits if
  the gateway later reports success - money taken and never credited is the worse error.
- **Fails closed.** Starting a top-up needs the deployment flag *and* the admin setting; an
  unreadable settings group refuses rather than allows.
- **Bounded.** Per-top-up min/max, a wallet balance cap that counts top-ups in flight, a
  per-customer daily attempt cap, and the existing `payment` rate-limit policy on the
  create endpoint.
- **Scoped.** Every read and action is for the caller's own top-ups only (customer JWT).
- **No card data touches us.** Payment happens on PayU's hosted checkout; the server sends
  a hashed form and receives a signed outcome.
- **The sandbox shortcut cannot reach a real gateway.** `/wallet/top-ups/{id}/simulate`
  is refused unless the active gateway is the sandbox.
- **Open-redirect safe.** The "back to your booking" destination remembered across the
  trip to the gateway is validated as a same-site path (`isSafeReturnPath`) before it is
  ever rendered as a link.
- **Not withdrawable.** There is no path from wallet balance to a bank account; the
  screen says so before the customer pays.

## SECURITY REVIEW CHECKLIST

Before releasing any feature, verify:

- Authentication is enforced.
- Authorization is validated.
- Input validation exists.
- Sensitive data is protected.
- Secrets are externalized.
- APIs expose only required data.
- Logs contain no sensitive information.
- Dependencies have no known critical vulnerabilities.

## OUT OF SCOPE

This document does not define:

- Business requirements
- System architecture
- API design
- Database schema
- Coding conventions
- Testing strategy
- Deployment process

Refer to the corresponding project documents for these topics.
