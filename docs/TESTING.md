# TESTING.md

Testing Strategy & Quality Assurance Standards

## PURPOSE

This document defines the testing strategy, quality standards, and verification practices for the Nestly platform.

Its objective is to ensure every feature is reliable, maintainable, and production-ready through consistent testing.

This document is the single source of truth for application testing standards.

## TESTING OBJECTIVES

Every release should ensure:

- Functional Correctness
- Reliability
- Stability
- Regression Safety
- Performance Confidence
- Security Verification
- Production Readiness

Testing is an essential part of development and must not be treated as an optional activity.

## TESTING PYRAMID

Testing should follow the standard testing pyramid.

```
        End-to-End Tests
     ─────────────────────
       Integration Tests
  ───────────────────────────
          Unit Tests
```

Prefer a larger number of Unit Tests, fewer Integration Tests, and only the required End-to-End Tests.

## UNIT TESTING

Unit Tests verify individual business logic in isolation.

Requirements:

- Test business rules
- Test validation
- Test edge cases
- Test negative scenarios
- Keep tests independent
- Avoid external dependencies

Unit tests should be fast, deterministic, and repeatable.

## INTEGRATION TESTING

Integration Tests verify interaction between application components.

Typical scenarios include:

- Database interaction
- Repository integration
- External service integration
- Background processing
- API pipeline verification

Integration tests should validate that components work together correctly.

## API TESTING

Every public API should be verified.

API testing should validate:

- Request validation
- Response structure
- HTTP status codes
- Authentication
- Authorization
- Error handling
- Pagination
- Filtering
- Sorting

API behavior should remain consistent across versions.

## END-TO-END TESTING

End-to-End Tests validate complete user workflows.

Typical examples:

- User Registration
- Login
- Booking Lifecycle
- Payment Flow
- Order Completion
- Administrative Operations

Focus on critical business journeys rather than exhaustive UI coverage.

### Auto-schedule, wallet and provider-reservation journeys

What covers the daily/prepaid plan, wallet top-up and provider-reservation work:

- **Unit/integration (Catalog.Tests, real database):** `WalletTopUpServiceTests` (credit
  exactly once, amount mismatch, forged callback, late success, limits, the admin switch
  and balance cap), `PrepaidRecurringPlanTests` (one order for N visits, dropped dates,
  group webhook, cancellation refunds), `RecurringPlanControlsTests` (pause reason,
  resume, skip, change time, auto-pause after unpaid visits, low-balance warning) and
  `ProviderPlanReservationTests` (the regular professional is kept free for the plan's
  visits - horizon, cadence, priority between plans, paused/cancelled plans).
- **E2E (customer-web, `e2e/`):** `298-recurring-opt-in.spec.ts` (daily plan with skip /
  change time / pause / resume / cancel, daily plan paid from the wallet, prepaid plan paid
  once and cancelled, plan-type tiles) and `wallet-topup.spec.ts` (sandbox top-up credits
  and shows in the ledger, the summary's "Add money" link, the return page's way back).
  The wallet spec needs the consumer API started with `WalletTopUp__Enabled=true`.
- **customer-web unit tests:** `npm run test:unit` (Node's built-in runner through `tsx`, no extra dependency) runs
  `src/lib/geolocation.test.ts` - "Allow location" in one tap: permission already granted, a prompt still open (the
  browser failing the call at once, or the call just waiting), a real refusal, a prompt nobody answers, and a location
  provider that is not ready yet. It also runs `src/lib/booking-actions.test.ts` - what the booking detail page offers
  at each status (cancel and reschedule follow `BookingLifecycle`, review only once completed, "Amount due" vs
  "Amount paid", and "On the way" / "Arrived" / "Service in progress" instead of a stale "Professional confirmed").
  Not wired into CI yet.
- **What an admin sees and does** (Catalog.Tests): `AdminWalletTopUpServiceTests` (the list newest
  first, the 24-hour summary, stuck vs needs-review, filters and search, paging, a mismatched
  callback flagged and shown, Reconcile now - credit once, mark failed, still pending, late
  payment after a write-off, audit entry) and `RecurringBookingPlanAdminControlsTests` (how
  each plan is paid for and why a paused one is paused, the plan detail with wallet balance
  and visits, admin pause / resume / cancel with the customer told and the reason audited,
  a support-paused plan the customer cannot resume, a failing notifier never undoing the
  action, the wording never saying "you" for something support did).
- **What the customer is told** (Catalog.Tests): `RecurringPlanChangeMessagesTests` (the wording of every
  confirmation - free pause, booked visits still charged, singular/plural, fees and refunds),
  `RecurringPlanConfirmationsTests` (each action sends its confirmation with real facts; a failed action sends
  nothing; a failing notifier never fails the action; the plan card's data), the shortfall tests in
  `RecurringPlanControlsTests` (wallet empty / part-covered / not a wallet plan) and the cancellation
  explanation tests in `CancellationServiceTests` (cut-off time, fee basis, carried-over reschedule charge).
  `140c-cancel-reschedule.spec.ts` asserts the cancel screen's explanation before and after cancelling.
- **Rescheduling and the professional on the job** (Catalog.Tests): `RescheduleServiceTests` - the
  eligibility explanation (free-until / last-moment / late-now fee / locked-in cancellation fee), the outcome
  explanation, who is told what (kept, released, a plan reservation for a customer vs an admin, the assigner
  handing the job to somebody else, re-offering the same professional, nobody on it before - the last three
  through a stand-in for the in-process auto-assigner and the **real** assignment service), and
  `ProviderAutoAssignmentHandlerTests` (the professional on a rescheduled booking is kept over a nearer one,
  replaced when the new time does not suit them, and nothing is preferred outside a reschedule).
  The collection switch is covered too (off: a late reschedule records the fee, takes nothing and needs no wallet; the
  eligibility says so; a fee collected earlier is still credited after it is switched off; the setting's default and an
  older saved value reading as off, in `BookingPolicyProviderTests`).
  The late fee's collection is covered in `RescheduleServiceTests` (wallet debited and tagged with the reschedule,
  platform revenue booked with the booking's held balance untouched, refused with nothing moved when the wallet is
  short, nothing taken when it is not late or an admin moves it, the wallet check in the eligibility response, the fee
  handed back when the booking fails to save, and the credit against a later cancellation - including when more was
  paid than the cancellation fee), the professional's cancel notice (customer, admin) in the same class, and which
  policy applies in `BookingPolicyProviderTests` (configuration until an admin saves, then the admin's value; an
  unreadable or out-of-range value falls back; through the real settings table), the platform rules the same way
  (`PlatformRulesProvider`: null until saved, one read per request), and each rule in its own engine's suite:
  `SlotAvailabilityServiceTests` (horizon, lead time, same-day cutoff, same-day off, overbooking, nothing saved),
  `BookingServiceTests` (the active-booking cap, unpaid and plan visits excluded), `PriceCalculationServiceTests`
  (default tax, a city's own tax wins), `BookingSummaryServiceTests` (wallet share, never above the balance),
  `CouponServiceTests` / `CouponManagementServiceTests` (off switch, active-coupon cap, maximum percentage and the
  legacy-coupon edit rule), plus one test each in
  `RescheduleServiceTests` / `CancellationServiceTests` that the engine follows an admin-saved policy.
  `NotificationTriggerWiringTests` pins that a reschedule notification names the new slot and carries the short
  booking reference rather than the 36-character id (the reference comes from the shared payload builder, so every
  booking notification uses it, but only this one is asserted).
  `140c-cancel-reschedule.spec.ts` asserts the rules and the result on the reschedule screen. A bare test
  context dispatches no domain events, so anything that depends on the auto-assigner running during a
  reschedule needs a context built with `DomainEventDispatchInterceptor`, as those tests do.
- **Known time-of-day dependency:** `ProviderJobServiceTests.StartAsync_succeeds_once_the_providers_other_job_has_been_completed`
  completes a 09:00-11:00 job "now" and then assigns a 14:00-16:00 job the same day; run after 14:00 business
  time the first job still occupies the provider until its actual finish, so the assignment is (correctly)
  refused and the test fails. It is unrelated to recurring plans and passes when run earlier in the day.
- **Local environment notes:** the suite needs the E2E provider mapped to the current E2E
  zone (apply `database/seed/dev-provider-e2e-capacity-seed.sql`, as CI does) or every
  booking fails the provider-availability gate; a customer address saved before the E2E
  pincode existed has no resolved pincode and cannot be priced (delete it via the API and
  let the seed recreate it). Where Docker or Playwright's bundled browser is unavailable,
  run with an installed Edge/Chrome via a throwaway config that sets the project's
  `channel`.

## TEST CASE DESIGN

Every feature should include:

- Positive Scenarios
- Negative Scenarios
- Boundary Conditions
- Invalid Inputs
- Exception Cases
- Business Rule Validation

Tests should represent real business behavior.

## REGRESSION TESTING

Regression testing should ensure:

- Existing functionality remains unaffected.
- Previously fixed defects do not reappear.
- Critical workflows continue to function correctly.

Regression tests should execute before every release.

## PERFORMANCE VERIFICATION

Performance testing should verify:

- Response Time
- Throughput
- Concurrent Requests
- Resource Utilization
- Scalability

Performance should be measured using realistic workloads.

## SECURITY TESTING

Security verification should include:

- Authentication
- Authorization
- Input Validation
- Access Control
- Sensitive Data Exposure
- Common Vulnerability Checks

Security testing should be performed before production releases.

## TEST DATA

Test data should be:

- Predictable
- Repeatable
- Isolated
- Non-production

Sensitive production data must never be used directly.

## TEST AUTOMATION

Automate tests wherever practical.

Priority:

1. Unit Tests
1. Integration Tests
1. API Tests
1. End-to-End Tests

Automated tests should execute consistently in local development and CI/CD pipelines.

## CODE COVERAGE

Code coverage is a quality indicator, not the primary objective.

Prioritize:

- Business-critical logic
- High-risk modules
- Core workflows

Meaningful tests are more valuable than high coverage percentages.

## DEFECT MANAGEMENT

When defects are identified:

- Reproduce the issue
- Fix the root cause
- Add or update automated tests
- Verify related functionality
- Prevent regression

Every significant defect should result in a new regression test.

## RELEASE VALIDATION

Before deployment, verify:

- All automated tests pass
- Critical business workflows are validated
- No unresolved critical defects exist
- Performance is acceptable
- Security verification is complete

Only production-ready builds should be released.

## TEST REVIEW CHECKLIST

Before completing any feature, confirm:

- Unit Tests exist.
- Integration Tests are updated.
- API behavior is verified.
- End-to-End scenarios are covered where required.
- Edge cases are tested.
- Negative scenarios are validated.
- Regression impact is assessed.
- No critical failures remain.

## QUALITY PRINCIPLES

Testing should be:

- Repeatable
- Reliable
- Independent
- Maintainable
- Fast
- Automated where practical
- Focused on business value

Testing should increase confidence in the system, not simply increase the number of test cases.

## OUT OF SCOPE

This document does not define:

- Business requirements
- System architecture
- Coding standards
- Database implementation
- API design
- Security implementation
- Deployment process

Refer to the corresponding project documents for these topics.
