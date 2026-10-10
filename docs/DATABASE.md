# DATABASE.md

Database Design & PostgreSQL Standards

## PURPOSE

This document defines the database architecture, design principles, standards, and best practices for the Nestly platform.

It establishes a consistent approach for designing, implementing, maintaining, and optimizing the PostgreSQL database.

This document is the single source of truth for all database-related standards.

## DATABASE PLATFORM

Primary Database

- PostgreSQL

Data Access Technologies

- Entity Framework Core (Primary ORM)
- Dapper (Read Optimization)

Database technology should remain consistent unless officially approved.

## DATABASE OBJECTIVES

The database design must ensure:

- Data Integrity
- Consistency
- Performance
- Scalability
- Reliability
- Maintainability
- Security
- Auditability

## DATABASE DESIGN PRINCIPLES

Every database design should follow:

- Normalization where appropriate
- Clear ownership of data
- Referential Integrity
- Minimal redundancy
- Predictable relationships
- High cohesion
- Low coupling

Database structure should model the business domain rather than application implementation.

## DATA ACCESS STRATEGY

### Entity Framework Core

Use EF Core for:

- Create operations
- Update operations
- Delete operations
- Transactions
- Aggregate updates
- Business workflows
- Domain persistence
- Entity relationships
- Migrations

EF Core is the default persistence technology.

### Dapper

Use Dapper only when optimized read performance is required.

Typical scenarios:

- Reporting
- Dashboards
- Analytics
- Search
- Complex joins
- Large result sets
- Read-heavy queries

Do not use Dapper for business transactions.

## SCHEMA DESIGN

The schema should:

- Represent business concepts clearly
- Keep related data together
- Avoid unnecessary duplication
- Support future growth
- Maintain backward compatibility where possible

## TABLE DESIGN

Every table should:

- Represent a single business concept
- Have a primary key
- Use meaningful names
- Include audit information where required
- Avoid unnecessary nullable columns
- Avoid duplicate data

## PRIMARY KEYS

Requirements:

- Every table must have a primary key.
- Primary keys must be immutable.
- Keys should remain stable throughout the lifetime of the record.

## FOREIGN KEYS

Use foreign keys to maintain referential integrity.

Guidelines:

- Define explicit relationships.
- Prevent orphaned records.
- Avoid unnecessary cascading deletes.
- Preserve business consistency.

## INDEXING

Indexes should improve query performance without unnecessary overhead.

Consider indexes for:

- Primary Keys
- Foreign Keys
- Frequently searched columns
- Frequently sorted columns
- Frequently filtered columns
- Unique constraints

Avoid excessive indexing.

Review index usage periodically.

## QUERY DESIGN

Database queries should:

- Be efficient
- Return only required columns
- Avoid unnecessary joins
- Avoid SELECT *
- Use filtering effectively
- Support pagination where appropriate

Optimize queries only after measuring performance.

## TRANSACTIONS

Transactions should:

- Be as short as possible
- Maintain consistency
- Preserve atomicity
- Handle failures correctly

Long-running transactions should be avoided.

## CONCURRENCY

The application should safely handle concurrent operations.

Guidelines:

- Prevent lost updates
- Handle conflicting modifications
- Maintain data consistency

## MIGRATIONS

Schema changes should be managed through controlled migrations.

Guidelines:

- Keep migrations small
- Make migrations reversible where possible
- Review migration scripts before deployment
- Never modify historical migrations already applied in production

## AUDITING

Audit information should be maintained where required.

Typical fields include:

- Created Date
- Created By
- Modified Date
- Modified By

Business requirements determine audit scope.

### Column stamping vs. the audit trail

These are two distinct mechanisms and should not be confused:

- **Column stamping** — `IAuditable` entities get `CreatedOnUtc` /
  `ModifiedOnUtc` populated automatically by `AuditableEntityInterceptor`.
  This records *when a row last changed*, nothing more.
- **The audit trail (T020)** — the `audit_log` table records *who did what to
  which entity, from where*: actor type and id, entity name and id, action,
  before/after values, IP, correlation id, timestamp.

### Writing audit entries

Use `IAuditLogWriter` (Application layer). It enlists the entry in the current
unit of work and does **not** save — the caller's `SaveChangesAsync` commits it
in the same transaction as the change it describes. That is deliberate: a
rolled-back operation must not leave a phantom audit entry, and a committed one
must never be missing its record.

Attribution (actor, IP, correlation id) is resolved from the ambient request by
`IAuditContextProvider`; callers do not supply it and so cannot misattribute an
action. Work with no request — background jobs, tooling — is recorded as the
`System` actor.

`OldValues` / `NewValues` are `jsonb` and hold the changed fields only. Callers
must strip secrets and PII before constructing the entry; the writer cannot know
which fields of an arbitrary entity are sensitive, and the project rule is
absolute: never log passwords, tokens, or PII.

The table is append-only — the entity exposes no mutators. An audit trail that
can be edited after the fact is not an audit trail.

## RECURRING BOOKINGS

Phase 17 (task 296) models a customer's standing instruction to repeat a
booking on a schedule. The governing rule is that **a recurring booking is not
a second kind of booking**: every occurrence a plan produces is an ordinary
`booking` row, created through the same orchestration a customer's own "Book
now" tap uses, carrying a foreign key back to the plan that produced it. There
is no parallel booking model, no second copy of the pricing/serviceability
rules, and nothing downstream (payments, refunds, assignment, tracking,
reviews) needs to know a booking came from a plan in order to work.

### `recurring_booking_plan`

The schedule itself. Written by the customer (create/pause/resume/cancel),
read by the generator. It holds no pricing, payment, or serviceability state.

| Column | Type | Notes |
| --- | --- | --- |
| `id` | `uuid` PK | |
| `customer_id` | `uuid` NOT NULL | FK → `customer`, Restrict |
| `service_id` | `uuid` NOT NULL | FK → `service`, Restrict. The category is reached through the service; it is not duplicated here |
| `city_id` | `uuid` NOT NULL | FK → `city`, Restrict |
| `locality_id` | `uuid` NOT NULL | FK → `locality`, Restrict |
| `address_id` | `uuid` NOT NULL | FK → `customer_address`, Restrict |
| `slot_window_id` | `uuid` NOT NULL | FK → `slot_window`, Restrict. This is the "preferred slot" — a slot window, not a raw wall-clock time |
| `quantity` | `integer` NOT NULL | |
| `frequency` | `varchar(20)` NOT NULL | `Weekly` / `Biweekly` / `Monthly` / `Daily`. `Daily` takes neither a day of week nor a day of month. It is last in the C# enum because the enum crosses the wire as its ordinal |
| `recurrence_day_of_week` | `varchar(20)` NULL | Required for Weekly/Biweekly, must be null for Monthly |
| `recurrence_day_of_month` | `integer` NULL | Required (1–31) for Monthly, must be null otherwise |
| `start_date` | `date` NOT NULL | |
| `end_date` | `date` NULL | |
| `occurrence_count` | `integer` NULL | Both this and `end_date` null = an open-ended ("until I cancel") plan; the scheduler only ever books `LeadTimeDays` ahead, so it never creates the whole future at once |
| `completed_occurrence_count` | `integer` NOT NULL | Successfully booked occurrences only |
| `next_occurrence_date` | `date` NOT NULL | The generator's cursor |
| `status` | `varchar(20)` NOT NULL | `Active` / `Paused` / `Cancelled` / `Completed` |
| `created_at_utc` | `timestamptz` NOT NULL | |
| `prepaid_upfront` | `boolean` NOT NULL DEFAULT false | The plan's visits are paid for a cycle at a time, in one checkout. Fixed at creation. The daily scheduler never generates occurrences for such a plan |
| `pending_prepayment_lead_booking_id` | `uuid` NULL | While a prepaid cycle is unpaid: the booking whose payment page settles it (the booking placed with the plan for cycle 1, the first visit of the cycle for a renewal). Null once paid |
| `pending_prepayment_through_date` | `date` NULL | Last date of the unpaid cycle; becomes `prepaid_through_date` when it is paid, discarded if it never is |
| `prepaid_through_date` | `date` NULL | Last date covered by a **paid** cycle. Drives renewal of an open-ended prepaid plan |
| `prepaid_cycles_paid` | `integer` NOT NULL DEFAULT 0 | 0 = the plan has never been started (first cycle unpaid or abandoned) |
| `pause_reason` | `varchar(20)` NULL | Why the plan is `Paused`: `Customer` / `UnpaidVisits` / `PaymentFailure`. Null whenever the plan is not paused (and for plans paused before the column existed) |
| `skip_until_date` | `date` NULL | The last "skip visits until" date asked for. Informational once it has passed - the plan is `Active` throughout |
| `skip_ranges_used` | `integer` NOT NULL DEFAULT 0 | How many "skip visits until" requests the plan has used; capped by `RecurringBookings:MaxSkipRangesPerPlan` |

Indexes: `(status, next_occurrence_date)` — the exact filter the generator's
due-set query uses — plus `customer_id` and the FK indexes.

`recurring_booking_plan_addon` (`id`, `recurring_booking_plan_id`, `add_on_id`,
`quantity`, Cascade) carries the add-on selections the plan repeats onto every
occurrence.

### Prepaid plans (pay for all visits in one checkout)

A prepaid plan changes **when the customer pays, not what a booking is**. The
customer places a booking (visit 1 of the purchase, *not* a plan occurrence),
buys a plan with it, and the API creates every remaining visit of the cycle
immediately as an ordinary unpaid `booking` (`RecurringBookingSchedulerService.MaterializePrepaidCycleAsync`,
the same booking orchestration and occurrence log the daily job uses, minus the
per-visit notifications). The lead booking's payment page then pays for all of
them with **one gateway order**.

* **Per-booking money stays per booking.** Each visit keeps its own
  `payment_transaction` / `payment_attempt`, so commission, the escrow hold,
  refunds and provider payouts are unchanged and per visit.
* **`payment_group`** is the single gateway order: `gateway_order_id` (PayU
  `txnid`, unique), `total_amount` (sum of the members' amounts), `visit_count`,
  `status` (`Pending` / `Success` / `Failed`), `lead_booking_id`, and the
  gateway's shared `gateway_payment_ref`. A retry after a failed payment is a
  **new group** over the same member transactions.
* **`payment_attempt.payment_group_id`** (nullable, FK Restrict) marks a member's
  attempt. Its `gateway_order_id` is a synthetic `<group order id>~<n>` (the
  column is unique); only the group's id is ever sent to the gateway. A webhook
  or verify call resolves the group first, then applies the one outcome to every
  member in a single database transaction, guarded by a conditional
  `Pending -> resolved` UPDATE on the group.
* **Cycles.** A bounded plan is one cycle: its end date is pinned to the last
  bought visit, so a date that cannot be booked is *dropped and reported, not
  made up later*. An open-ended plan sells `RecurringBookings:PrepaidCycleDays`
  (30) at a time; `ProcessPrepaidRenewalsAsync` creates the next cycle
  `PrepaidRenewalLeadDays` (5) before the paid one ends and sends the ordinary
  payment-due notification. An unpaid renewal pauses the plan; an unpaid first
  cycle ends it.
* **Unpaid purchases release everything.** The first cycle uses the ordinary
  checkout expiry window; when its lead booking expires or is cancelled,
  `RecurringPlanOccurrenceReleaseHandler` abandons the cycle and expires the
  other unpaid visits (`UnpaidBookingReleaseService` - the same release logic the
  expiry sweep uses: slot seat, wallet credit, coupon).
* **A visit nobody can serve** is released and dropped from the order at payment
  time (same provider-eligibility gate as a single booking); only an unstaffable
  *lead* blocks the purchase.
* **One confirmation, not N.** Every member reaches `Confirmed`, but only the
  lead sends the confirmation / payment-received messages (with the group total);
  the others' intents are resolved `Skipped`.
* **Cancelling** a prepaid plan cancels its not-yet-done visits one by one
  through the ordinary cancellation service (policy fee applied, refund to the
  original method). The booking placed with the plan is its own visit and is not
  cancelled with it.
* Limits: at most `RecurringBookings:MaxPrepaidVisits` (60) visits per purchase;
  auto-charge and per-visit wallet credit do not apply to a prepaid plan.

### Pay-as-you-go ("Daily") plans: unpaid visits, skipping, changing the time

The other way to buy a plan: nothing is paid up front. The booking placed with
the plan is paid on the payment page as usual; every later visit is created by
the daily job `LeadTimeDays` ahead as an unpaid booking and paid as it is
created - from the wallet when the plan has `apply_wallet_credit` (a visit the
wallet covers in full is `Confirmed` at creation; a partly covered one waits
for the customer to pay the rest). The customer chooses between this and a
prepaid plan on the booking summary ("Daily plan" / "Prepaid plan"); a daily
plan is always `Daily` frequency, a prepaid plan takes any cadence.

* **An unpaid visit never gets a professional.** Provider assignment is only
  promoted for a *paid* booking, and an unpaid occurrence expires after 24 hours
  (`RecurringPlanOccurrenceReleaseHandler`).
* **Repeated non-payment pauses the plan.** After
  `RecurringBookings:PauseAfterUnpaidVisits` (2) *consecutive* unpaid expiries the
  plan is paused with `pause_reason = UnpaidVisits` and the customer is told
  (`NotificationEventType.RecurringPlanPaused`). "In a row" is read newest visit first: a visit still
  awaiting payment is not counted either way, an expiry extends the streak, and anything else
  (paid, completed, cancelled by the customer) ends it - one missed payment between paid ones
  never pauses a plan.
  `Resume` is the customer's call, and moves the cursor past dates that have
  already gone by (so a long pause does not produce a burst of "skipped"
  notices).
* **Low-balance warning.** When a wallet-paid plan creates a visit and the
  balance no longer covers `RecurringBookings:WalletLowBalanceVisits` (3) more,
  `WalletLowBalance` is sent - at most once a day per customer
  (`INotificationEventRepository.GetLatestCreatedAtUtcAsync`).
* **Skip visits until a date** - `POST /recurring-booking-plans/{id}/skip-visits`
  (`resumeOn`, `cancelBookedVisits`). The plan stays `Active`; the cursor moves to
  the first occurrence on or after `resumeOn`. Skipped dates are not consumed from
  `occurrence_count`, and a plan bounded by `end_date` has it pushed out by the
  days skipped. Limits: `MaxSkipDays` (30) from today, `MaxSkipRangesPerPlan` (2)
  per plan. With `cancelBookedVisits` the few visits already created before the
  date are cancelled through the ordinary cancellation service (policy fee
  applies); when the scheduler has already booked past the date, that is all the
  request does. Not available on a prepaid plan.
* **Change time** - `POST /recurring-booking-plans/{id}/slot` (`slotWindowId`).
  For an `Active` pay-as-you-go plan only. The new window must be able to serve
  `next_occurrence_date`. It applies to visits created from now on; visits
  already created keep their slot (rescheduling those is the ordinary per-booking
  reschedule, with its own policy), so a change can never fail half-way through a
  batch.
* A prepaid plan's visits already exist and are paid for, so skip and change-time
  are refused there with a pointer to per-visit reschedule/cancel.

### What the customer is told about their plan

Nothing about a plan should have to be inferred from a screen that has since closed, so every change
and every payment shortfall is stated in words - by notification (SMS / e-mail / push) and on the plan card.

* **System-driven** (the daily job and the release handler): a visit created and paid
  (`RecurringBookingUpcoming`), a visit that needs payment (`RecurringBookingPaymentDue`, with amount and
  deadline), a card auto-charge coming (`RecurringAutoChargeScheduled`), a date that could not be booked
  (`RecurringBookingSkipped`), the wallet running low (`WalletLowBalance`) and the plan pausing itself after
  unpaid visits (`RecurringPlanPaused`).
* **Wallet could not cover a visit** - `RecurringWalletShortfall`. For a plan that pays from the wallet (and
  has no card auto-charge, whose advance notice keeps its own message), a visit left waiting on payment sends
  this instead of the generic reminder: how much the wallet paid ("your wallet was empty" / "₹x was paid from
  your wallet"), how much is still to pay, and the deadline.
* **Customer-driven** - `RecurringPlanChanged`, one event for pause, resume, skip visits, change time and
  cancel. `RecurringPlanChangeMessages` (pure; unit-tested) builds a title, a short summary (SMS / push) and
  the full text (e-mail) from the facts of what the action actually did - visits still booked, visits
  cancelled, fees kept, amount refunded - and `RecurringPlanNotifier` sends it **after** the action succeeded.
  It is best effort: a confirmation that cannot be sent is logged and never fails or undoes the action, and a
  failed action sends nothing. The messages are explicit about what customers wrongly assume: pausing, skipping
  and changing the time are free; visits **already booked** are untouched by a pause or a time change and are
  still charged unless cancelled; cancelling a booked visit follows the cancellation policy.
* **The plan card** - the list/detail reads (not the action responses) add: the time window
  (`slot_window_name`, start/end), `visit_amount` (the newest real visit's total, wallet credit included),
  `upcoming_booked_visit_dates`, `visit_awaiting_payment` (a created-but-unpaid visit, with a Pay link) and the
  platform's cancellation policy for one booked visit (the free window and late-fee percentage as an admin saved
  them in Settings, else `CancellationPolicy:*` configuration) so the pause / skip / cancel dialogs state the real charge. They are read for
  all of a customer's plans in one query (`IBookingRepository.ListVisitSummariesByPlansAsync`, bounded to the
  last 14 days onward), not one query per plan.
* Both new events are stored by name in `notification_template` (one row per channel), seeded by
  `20261001114813_SeedPlanChangedAndWalletShortfallNotificationTemplates`. Their ordinals in the C# enum
  (`RecurringPlanChanged` = 31, `RecurringWalletShortfall` = 32) matter only to the admin-web template screen's
  mirror of the enum and when merging with a branch that also appends events.

### The regular professional is reserved for the plan's visits

The double-booking guard (`ProviderScheduleConflictService`) only sees jobs that are
already **assigned**, and a plan's visit is created `LeadTimeDays` (3) ahead and
assigned only inside the fulfilment window (`PromotionLeadTimeHours`, 24). For any
date further out a plan's regular professional therefore looked free, and an
unrelated order at the same time could be handed to them - after which the plan's
visit lost the professional the customer was promised.

`ProviderPlanReservationService` closes that gap. A provider is **reserved** - and so
not eligible for another booking - when they are the *regular professional* of a
plan (whoever served its newest assigned, non-cancelled visit; the same rule
`RecurringPlanProviderContinuityService` uses) and that plan has, or is due to
have, a visit on the booking's date at an overlapping time:

* **Visits that exist** but have no professional yet (`PaymentPending` /
  `Confirmed` / `AwaitingFulfilment`, unassigned or assigned to this provider) reserve
  their date. A visit already given to someone else, expired, or cancelled does not.
* **Visits not created yet**: an `Active` plan reserves each date in its own
  projection (`PreviewUpcomingOccurrenceDates`, which honours cadence, end date and
  remaining visit count) whose slot window overlaps. Paused, cancelled and completed
  plans reserve nothing they have not already booked.
* **Horizon.** Only dates up to `RecurringBookings:ProviderReservationHorizonDays`
  (30, the booking window) ahead; `0` switches the rule off.
* **Priority.** A one-off booking yields to every such plan; a booking that is itself
  a plan's visit yields only to plans *created before* its own plan, and never to its
  own - so two plans sharing a professional cannot exclude each other (the older keeps
  them).
* **Where it applies.** `PlanReservationAwareEligibilityService` wraps the existing
  `ProviderAssignmentEligibilityService`, so every automatic path gets it: auto-assignment,
  the booking-creation and payment provider-availability gates, and a plan visit's
  regular-professional check. The reservation check (database reads only) runs first,
  so a reserved provider never costs the inner gate's billed route lookup. **Manual
  assignment by an admin does not pass through it** (it uses the double-booking guard
  directly), so an admin can still override.
* **Consequence to expect:** with few professionals, a slot a daily plan holds can
  answer "no professional available" for other customers earlier than before - that is
  the point, not a fault.

### `recurring_booking_occurrence`

An append-only audit row recording what the generator did for **one scheduled
date**, whatever the outcome. Same discipline as `booking_status_history` and
`wallet_ledger_entry`: no mutators, never rewritten.

| Column | Type | Notes |
| --- | --- | --- |
| `id` | `uuid` PK | |
| `recurring_booking_plan_id` | `uuid` NOT NULL | FK → `recurring_booking_plan`, Cascade |
| `scheduled_date` | `date` NOT NULL | |
| `outcome` | `varchar(30)` NOT NULL | `Booked` / `SkippedSlotUnavailable` / `SkippedOrchestrationRejected` / `BookedProviderReassigned` / `BookedProviderUnavailable` |
| `booking_id` | `uuid` NULL | FK → `booking`, Restrict. Set for every `Booked*` outcome, null for every `Skipped*` one |
| `skip_reason` | `varchar(500)` NULL | Human-readable only — never a raw exception or stack trace. Also carries a `Booked*` occurrence's provider note (task 297) |
| `processed_at_utc` | `timestamptz` NOT NULL | |

Indexes: `(recurring_booking_plan_id, scheduled_date)` UNIQUE — this is the
generator's idempotency guard, so a Hangfire retry or an overlapping run
cannot double-book a date — and `booking_id` UNIQUE, so one booking is claimed
by at most one occurrence (Postgres treats every NULL as distinct, so the
skipped rows are unconstrained).

**The three booked outcomes (task 297).** `outcome` answers "did the visit
happen" and, separately, "who is going to do it". A recurring plan is a
standing relationship, so the generator asks whether the professional who
served the plan's last occurrence can serve this date — derived from the
plan's own booking history through `booking.recurring_booking_plan_id`, never
stored on the plan, because a stored preferred-provider column would drift the
moment that provider is suspended, rejects a job, or is reassigned away.

- `Booked` — no standing provider yet, or they can serve the date.
- `BookedProviderReassigned` — they cannot, but somebody else can. The visit
  is booked and handed to the existing reassignment flow; the swap itself is
  made by `ProviderAutoAssignmentHandler` when the booking reaches
  `AwaitingFulfilment`, which raises `BookingProviderChangedEvent` and tells
  the customer their professional changed.
- `BookedProviderUnavailable` — nobody is eligible. Still booked, recorded
  with its reason and logged as a warning, and left in the manual admin
  assignment queue. Deliberately not a skip: the generator runs
  `RecurringBookingOptions.LeadTimeDays` ahead of the date precisely so a
  supply problem surfaces early, and supply that is short today may not be
  short on the day — throwing the customer's slot away over a forecast would
  be the worse error. A date that genuinely cannot be booked at all never
  reaches this point, because the orchestration refuses to create the booking
  and the occurrence lands on a `Skipped*` outcome instead.

No schema change was needed for any of this: `outcome` is a `varchar(30)`
with no CHECK constraint, and both new names fit.

### Where the plan link lives, and why it lives in both places

The link is expressed twice, deliberately. They answer different questions and
neither replaces the other.

- **`booking.recurring_booking_plan_id`** (nullable FK → `recurring_booking_plan`,
  Restrict, indexed) is the forward link and the primary contract. It makes
  *"is this job recurring, and on what frequency?"* answerable from a booking
  row a list query already loaded — no join, no second query, no dependence on
  the audit log. The admin plan view and the provider "recurring" badge both
  read it. Putting the link **only** on the occurrence table would force every
  provider/admin booking list to join through an audit table and then filter
  out its skipped rows, on a hot read path, to answer a yes/no question.
- **`recurring_booking_occurrence`** covers what the column structurally
  cannot: the scheduled dates that produced **no booking at all**. A skipped
  date has no `booking` row to hang off, so the "why did my plan not run on the
  12th" answer, and the generator's idempotency guard, both have to live in
  their own table.

`booking.recurring_booking_plan_id` is a **real** foreign key, unlike
`booking.source_address_id`, `booking.slot_window_id` and
`booking.subscription_id`, which are traceability-only precisely because they
point at mutable catalog/config rows that may be edited or deleted after the
snapshot was taken. A plan is different: it is never hard-deleted — it is
Cancelled or Completed and kept — so `Restrict` can never block a legitimate
operation, and it guarantees the join is never dangling.

The booking stores **no snapshot** of the plan's own fields (frequency,
day-of-week, …). A customer who changes their plan's frequency expects the
badge on their upcoming jobs to reflect the new frequency, so those are read
live through the key rather than frozen at generation time. This is the one
place a booking deliberately does *not* follow the snapshot convention, and it
is safe because none of those fields participate in the price the booking is
contractually bound to.

### Termination and status semantics

- A plan must be bounded by an end date, an occurrence count, or both — an
  unbounded plan would schedule forever with nothing to ever complete it.
- Whichever bound is reached first wins; the plan then moves to `Completed`.
- `Completed` is distinct from `Cancelled`: "delivered everything it promised"
  and "stopped early by a human" are different outcomes for reporting, even
  though both are terminal for scheduling.
- `Cancelled` is a one-way door — a cancelled plan can never be resumed
  (create a new one instead), the same convention `BookingLifecycle` uses for
  its own terminal states.
- A skipped occurrence advances `next_occurrence_date` but does **not**
  increment `completed_occurrence_count`. A supply-side miss is not charged
  against the customer's occurrence budget, so the plan effectively extends by
  one date rather than delivering one fewer visit than promised.
- Monthly recurrence clamps to the actual month length per month (31 → 28/29
  in February) without ratcheting the stored rule down: the requested day of
  month remains the source of truth every month.

### Enum storage

`frequency`, `status` and `outcome` are persisted as **strings**
(`HasConversion<string>()`), matching every other enum column in this schema —
a `varchar` column is readable in a dump and survives a reordered enum.

Over the wire is a different matter: the APIs serialize enums with the default
`System.Text.Json` behaviour, i.e. as **ordinals**. The house rule from Phase
16 therefore applies to all three: values may only ever be **appended**, never
reordered and never inserted into the middle. `RecurringBookingPlanStatus`
already shows the pattern — `Completed` sits after `Cancelled` because it was
added later, not in its "natural" lifecycle position.

## WALLET TOP-UPS

A customer adding their own money to the wallet through the payment gateway
(PayU Hosted Checkout). The booking payment tables cannot carry this -
`payment_transaction.booking_id` is required - so a top-up is its own aggregate.

### `wallet_top_up`

| Column | Type | Notes |
| --- | --- | --- |
| `id` | `uuid` PK | |
| `customer_id` | `uuid` NOT NULL | FK → `customer`, Restrict |
| `amount` | `numeric(12,2)` NOT NULL | What was asked for. A callback whose paid amount differs is refused, never credited |
| `currency` | `varchar(3)` NOT NULL | `INR` |
| `gateway_order_id` | `varchar(100)` NOT NULL | PayU `txnid`. **Unique** across top-ups; the webhook finds the top-up by it |
| `status` | `varchar(20)` NOT NULL | `Pending` / `Success` / `Failed` |
| `gateway_payment_ref` | `varchar(100)` NULL | |
| `failure_reason` | `varchar(500)` NULL | |
| `wallet_ledger_entry_id` | `uuid` NULL | The credit this top-up produced; set exactly once |
| `created_at_utc` / `completed_at_utc` | `timestamptz` | |
| `review_reason` | `varchar(300)` NULL | Why a person must look at this top-up. Set when a gateway callback disagrees with the amount asked for (nothing is credited); cleared when the top-up resolves. Surfaces in the admin top-up list |
| `review_flagged_at_utc` | `timestamptz` | When it was flagged. Both review columns were added by `AddWalletTopUpReviewFlag` (additive, nullable) |

Indexes: `(customer_id, created_at_utc)`, unique `gateway_order_id`,
`(status, created_at_utc)` (the reconciliation sweep's filter).

* **Credited exactly once.** The gateway can report one outcome by several routes
  - its webhook (redelivered), the customer's return page asking us to verify,
  the sweep - and they can race. `WalletTopUpService.ResolveAsync` is the only
  place the wallet is credited: it flips the row out of `Pending` with a
  conditional UPDATE and appends the `wallet_ledger_entry` (source type `TopUp`)
  in the same `Serializable` transaction, so one route wins and the rest change
  nothing.
* **A write-off is not final for success.** The sweep marks a top-up `Failed`
  once the gateway has no completed payment for it, but a slow checkout can still
  finish afterwards; a later success callback therefore still credits
  (`Failed -> Success`).
* **One webhook URL.** `PaymentCallbackRouter` hands a callback to the booking
  payment handler first and only an order no booking claims to the top-up service.
* **Reconciliation.** Hangfire job `wallet-top-up-reconciliation` (admin-api,
  every 10 minutes, only where `BackgroundJobs:ServerEnabled`) asks the gateway
  about a `Pending` top-up once it is `ReconcileAfterMinutes` (15) old, and stops
  after `ReconcileUpToDays` (7). The admin's **Reconcile now** runs the same check
  (`IWalletTopUpService.ReconcileNowAsync`) for one named top-up whatever its age,
  and also for a `Failed` one, so a payment that lands after a write-off can still
  be credited without waiting for a webhook. It runs in admin-api, which therefore
  needs the same `PayU__*` settings as consumer-api (see DEVOPS.md) - on the
  sandbox gateway every answer is "pending".
* **Admin view.** `GET /admin/wallet-top-ups` lists top-ups newest first with
  status / needs-attention / search filters. A top-up is **Stuck** when still
  `Pending` after 30 minutes (`IAdminWalletTopUpService.StuckAfterMinutes`) and
  **Needs review** when `review_reason` is set; the list also carries the pending,
  stuck and needs-review counts and the total credited in the last 24 hours, to set
  against the gateway's settlement report. Reconciling is audited (`AdminReconcile`
  on entity `WalletTopUp`). Permissions: `payments.read` / `payments.write`.
* **Switches.** Off by default and must stay off in production until the
  business and legal groundwork for holding customers' money is done. Starting a
  top-up needs **both** `WalletTopUp:Enabled` (deployment) **and** the admin
  setting `wallet.allowWalletTopUp` (runtime kill switch); the balance cap is the
  lower of `WalletTopUp:MaxWalletBalance` and the admin's `maxWalletBalance`. An
  unreadable/never-seeded settings group fails *closed*. Only starting is gated -
  a top-up already in flight can always finish, because the customer's money has
  already moved.
* **Limits** (`WalletTopUp:*`): `MinAmount` 100, `MaxAmount` 10000,
  `MaxWalletBalance` 20000, `MaxTopUpsPerDay` 10 (counts every attempt, blunting
  card testing), `PendingReuseMinutes` 15 (a double-click or reload reuses the
  open checkout instead of opening a second).
* **Not withdrawable.** There is no path from wallet balance back to a bank
  account; the screen says so.
* **API** (consumer-api, `WalletController`): `GET /wallet/top-up/config`,
  `POST /wallet/top-ups`, `GET /wallet/top-ups/{id}`,
  `POST /wallet/top-ups/{id}/verify`, and - sandbox gateway only, refused against
  a real one - `POST /wallet/top-ups/{id}/simulate`. PayU returns the browser to
  `/wallet/topup/{id}/return`.

## PROVIDER PHOTO AND PROVIDER-SCOPED REVIEWS

Task 293 closes the two gaps that made `BookingProviderSummary.PhotoUrl` and
`.Rating` structurally always-null: there was no photo column anywhere on
`provider`, and `review` was scoped to a **service**, not to a person. The
governing rule is that **a customer must never be shown a number or an image
that is not actually about the professional at their door** — which is why
both halves are schema changes rather than something derived in a response
mapper.

### `provider` — the photo columns

A photo is a *reference* to an already-hosted image, exactly like
`provider_kyc_document.file_ref`, `cms_media.url` and the completion-proof
photo refs. This schema still has no blob storage and these columns do not
introduce one.

| Column | Type | Notes |
| --- | --- | --- |
| `photo_url` | `varchar(2000)` NULL | Absolute http/https URL only — enforced in `Provider.SubmitPhoto`, not just in the request validator, because the value is rendered into an `img src` and a `javascript:`/`data:` reference there is script execution |
| `photo_moderation_status` | `varchar(20)` NULL | `Pending` / `Approved` / `Rejected`. **Null exactly when `photo_url` is** — same both-or-neither discipline as `latitude`/`longitude`/`location_updated_at_utc` on this table |
| `photo_moderated_by_admin_user_id` | `uuid` NULL | Traceability only, deliberately not a FK — same rationale as `review.moderated_by_admin_user_id` |
| `photo_moderated_at_utc` | `timestamptz` NULL | |
| `photo_moderation_note` | `varchar(1000)` NULL | The rejection reason, shown back to the provider so a rejection is actionable rather than a silent disappearance |

Index: `photo_moderation_status`, which is the admin moderation queue's only
filter and would otherwise scan the whole `provider` table on every load of
that screen.

**Moderation is the house standard here, not an extra.** Every other class of
user-supplied content in this schema goes through an admin verdict before it
counts — `provider_kyc_document.verification_status`, `review.status`/
`is_flagged` — so a photo does too. The gate is expressed **once**, as
`Provider.PublicPhotoUrl` (`photo_url` if and only if the status is
`Approved`), and every customer-facing mapper reads that rather than the raw
column. A gate re-implemented per call site is a gate that will eventually be
missed. Replacing an already-approved photo returns it to `Pending` and clears
the previous verdict: otherwise swapping the image after approval would be a
way to publish an unreviewed one under someone else's sign-off.

### `review.provider_id` — and why it is nullable forever

| Column | Type | Notes |
| --- | --- | --- |
| `provider_id` | `uuid` **NULL** | FK → `provider`, `Restrict`. Deleting a provider must not delete the reviews written about them |

Index: `(provider_id, status)` — the exact filter the per-provider aggregate
uses (`IReviewRepository.GetProviderRatingAsync`), which runs on the booking
detail and the polled live-tracking read, so it must not be a scan. The
aggregate counts **`Visible` reviews only**: a hidden review is hidden from
the rating too, or moderation would be cosmetic. A provider with no visible
reviews has **no rating at all** rather than a rating of zero — "new
professional" and "badly rated" must stay distinguishable all the way to the
screen.

Going forward the column is populated at submission time from the booking's
own `assigned_provider_id` (`ReviewService`): the person being rated is the
one who was on the job when the customer rated them, so capturing it then is
what stops a later reassignment moving the answer.

**The column cannot be `NOT NULL`, and this is a permanent property, not a
migration convenience.** Two populations legitimately resolve to no provider:

1. Historic reviews on bookings that were **reassigned** — see below.
2. Any review on a booking that completed without a provider recorded at all.

A null means *not attributable*. Such a review counts towards nobody's rating.

### The backfill's reassignment rule

`booking.assigned_provider_id` names whoever is on the booking **now**, which
on a reassigned booking may be someone who never did the work. Backfilling
straight from it would put a one-star review on the wrong professional — the
single worst outcome this feature can produce.

So the backfill (`AddProviderPhotoAndProviderScopedReviews.BackfillSql`)
attributes a review **only when the booking's assignment history names exactly
one provider**:

```sql
AND NOT EXISTS (
    SELECT 1 FROM booking_provider_assignment AS a
    WHERE a.booking_id = b.id AND a.provider_id <> b.assigned_provider_id
)
```

Any booking that ever involved a second provider leaves its review's
`provider_id` NULL. That covers every reassignment shape — a rejected offer,
an admin swap mid-job, a withdrawal followed by a new assignment — without
having to interpret assignment statuses, because none of those shapes lets us
prove who was standing in the customer's home when the review was written.
**Prefer null over a wrong attribution** is the rule; the loss is a slightly
thinner rating history, and the alternative is blaming the wrong person.

The statement is idempotent (`AND r.provider_id IS NULL`), so a replayed
migration cannot reattribute a review that has since been corrected by hand.
It is also exposed as a constant rather than inlined, so
`ProviderScopedReviewBackfillTests` executes **that exact string** against a
seeded database — unlike `AddProviderNoDoubleBooking`'s exclusion constraint,
which no test can reach. A rule about who gets blamed for a bad review
deserves coverage rather than a hand-check.

### What deliberately was **not** built

No `provider_rating_summary` rollup table. The aggregate is two numbers over
an indexed `(provider_id, status)` lookup, read once per booking detail and
once per tracking snapshot, and only when a provider is actually assigned. A
denormalised rollup would add a second source of truth that moderation
actions, review edits and backfills would all have to keep in step, to save a
query that is already cheap. Revisit only with a measurement showing this
lookup is hot.

## SERVICEABILITY AUTO-MANAGEMENT

`service_pincode_mapping.is_active` (SRS 12.9.2) used to be purely
admin-set. It is now also driven automatically by live provider coverage:
`IServiceabilityMappingManagementService.AutoEnableProviderCoverageAsync`
activates a mapping once a provider with matching skill+area coverage
appears, and `AutoDisableUnservedMappingsAsync` deactivates one once the last
covering provider is gone. Three columns exist purely to make that safe.

### `service_pincode_mapping` — the safety-net columns

| Column | Type | Notes |
| --- | --- | --- |
| `is_pinned` | `boolean` NOT NULL, default `false` | Admin override. When true, both auto-enable and auto-disable skip this mapping entirely — it stays exactly as an admin last set it regardless of live coverage. Set/cleared via the admin `pin`/`unpin` endpoints |
| `last_auto_toggled_at_utc` | `timestamptz` NULL | When this mapping was last changed by auto-enable/auto-disable (never by an admin). Null until the first auto-toggle. Drives the flap-protection cooldown (`ServiceabilityAutoManagementDefaults.AutoToggleCooldownMinutes`, 15 min): a mapping just auto-toggled is not auto-toggled again within the cooldown even if coverage flips back |
| `pending_auto_disable_since` | `timestamptz` NULL | Set the moment auto-disable first observes lost coverage; cleared if coverage returns, or once the mapping is actually auto-disabled. Auto-disable only acts once this has been in the past for at least `ServiceabilityAutoManagementDefaults.AutoDisableGracePeriodMinutes` (30 min) — the grace period that keeps a brief provider suspend/reactivate blip from taking a pincode dark. Deliberately does not apply to auto-enable, which is never delayed |

A recurring Hangfire job (`IServiceabilityAutoDisableSweepJob`, registered via
`ScheduleServiceabilityAutoDisableSweepJob`) re-checks every mapping with a
`pending_auto_disable_since` past the grace-period cutoff and disables the
ones still unserved, clearing the timer on any that have regained coverage or
been pinned/deactivated by hand in the meantime — the grace period would
otherwise only be honoured the next time something else touched the mapping.

Every real auto-toggle (not a no-op skip) writes an `AuditEntry` on
`ServicePincodeMapping` with a system `AuditContext` (`IAuditLogWriter`'s
explicit-context overload), so an "AutoEnabled"/"AutoDisabled" change is
distinguishable from an admin action in the audit trail.

The whole mechanism is gated behind `FeatureFlagSettings.AutoManageServiceabilityEnabled`
(default true, admin-only — see docs/API.md's SystemSettings section):
false makes every auto-enable/auto-disable call and the sweep job no-op
before doing any work, without touching `is_pinned` or the timers.

## LATE-RESCHEDULE FEE COLLECTION

When `ReschedulePolicy:CollectLateFeeFromWallet` is on (it is **off by default**; see DEVOPS.md), a customer's own
late reschedule (inside `ReschedulePolicy:LateFeeThresholdHours`, the slot actually changing) costs
`LateRescheduleFeePercentage` of what the booking was funded by, **taken from the customer's wallet in the same step as the
move**. An admin's reschedule never charges the customer, and a reschedule that leaves the slot where it was charges
nothing. With the switch off the lateness and the fee under the policy are still recorded on `booking_reschedule`
(`is_late`, `fee_amount`) but `fee_collected_amount` stays 0 and no wallet or escrow row is written.

* **`booking_reschedule.fee_collected_amount`** (`numeric(12,2) NOT NULL DEFAULT 0`) - what was actually
  taken. `fee_amount` stays what the fee was under the policy of the day, so an admin's late reschedule still
  records its lateness with `fee_collected_amount = 0`, and every reschedule before this existed reads 0. It
  only ever holds money that really moved: it is what a later cancellation counts against its own fee.
* **Wallet.** One `wallet_ledger` debit, `source_type = RescheduleFee`, `source_reference_id` = the
  `booking_reschedule.id`, description "Late reschedule fee for booking GLX-...". Taken through
  `IWalletService.DebitAsync` (serialisable, the same path as every other wallet debit), so two spends cannot
  both pass the balance check. If the wallet cannot cover the fee the reschedule is refused with 422
  `Reschedule.LateFeeWalletShort` **before** a seat on the new slot is taken, and again, authoritatively, at the
  debit. If the booking then fails to save, the fee goes back as a `RescheduleFeeReversal` credit and the seat is
  released.
* **Escrow.** `platform_escrow_ledger`, `source_type = RescheduleFeeCollected`: a hold and a release of the same
  amount, so the fee is recognised as platform revenue while the booking's own held balance - what completion
  pays the provider out of - is left exactly as it was. A failure to write it is logged for reconciliation and
  does not fail the reschedule (the debit and the history row are the record of the money).
* **Credit against a later cancellation.** `CancellationService` counts `SUM(fee_collected_amount)` for the
  booking against its own fee: it retains `max(0, fee - collected)` and refunds the rest, never crediting more
  than the cancellation fee itself (a reschedule fee paid beyond it is not handed back). Only the retained part is
  released to revenue as `CancellationFeeRetained`; the credited part was recognised when it was paid. So a
  customer who paid a late reschedule fee and then cancels pays the cancellation fee once, in total.
* **Order of the unit of work** (`RescheduleService.ExecuteRescheduleAsync`): fee worked out, wallet pre-check,
  seat reserved, wallet debited, booking saved, history row written (with the amount collected), escrow booked,
  then the provider assignment reconciled. The history row is written before the reconcile, which can still fail,
  so money that has moved is never without its row.

## SOFT DELETE

Where business requirements require record retention:

- Prefer soft delete
- Preserve historical data
- Exclude deleted records from normal queries

Permanent deletion should be intentional and controlled.

## DATA VALIDATION

Database constraints should enforce:

- Required values
- Uniqueness
- Referential integrity
- Valid relationships

Business validation belongs in the application/domain layer.

## PERFORMANCE

Performance considerations include:

- Efficient indexing
- Optimized queries
- Proper pagination
- Minimal locking
- Reduced network traffic
- Appropriate batching

Measure performance before optimizing.

## SECURITY

The database should:

- Enforce least privilege
- Restrict direct access
- Protect sensitive data
- Use parameterized queries
- Prevent SQL Injection

Sensitive information should never be stored insecurely.

## BACKUP & RECOVERY

The database strategy should support:

- Regular backups
- Point-in-time recovery
- Disaster recovery
- Restore verification

Recovery procedures should be tested periodically.

## DATABASE REVIEW CHECKLIST

Before releasing database changes, verify:

- Schema follows standards.
- Relationships are correct.
- Indexes are appropriate.
- Queries are optimized.
- Transactions are safe.
- Migrations are reviewed.
- Constraints enforce integrity.
- Performance impact is acceptable.
- Security requirements are satisfied.

## OUT OF SCOPE

This document does not define:

- Business requirements
- System architecture
- .NET implementation
- API design
- Coding standards
- Security policies
- Testing strategy
- Deployment process

Refer to the corresponding project documents for these topics.
