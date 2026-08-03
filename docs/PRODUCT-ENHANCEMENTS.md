# PRODUCT-ENHANCEMENTS.md

Product enhancement modules specification: Subscription, Recurring Bookings,
In-App Chat, Service Completion Verification.

## STATUS

Not implemented. Scoped as **Phase 10**, after Referral & Growth (Phase 9) —
these build on Booking, Payments, Wallet, Notifications, and Provider
(Phase 7), all of which exist or are substantially done. Backlog: `tasks.csv`
`#177`–`#198`.

## PURPOSE

Four independent features suggested during a "what else would help this
product" exploration, approved together and grouped into one phase because
they share dependencies (payments, notifications, the booking lifecycle)
rather than because they're one feature. Each section below stands alone —
implement in any order, subject to its own dependencies.

---

## 1. SUBSCRIPTION / MEMBERSHIP PLANS

**Tasks:** `#177`–`#183`

A paid recurring plan ("Nestly Plus") giving a customer ongoing benefits —
free visits, a standing discount, priority slot access — instead of a
one-time coupon.

### Design principle: reuse, don't duplicate

- **Recurring billing is a Hangfire-scheduled job calling the existing
  payment gateway interface** (`#68a`), not a new payment integration.
- **The subscriber discount is applied at the same booking-price calculation
  step Coupon already hooks into** (`#57`/`#58`), not a parallel pricing path.
- **Free-visit consumption is an atomic counter**, mirroring
  `Coupon.RedemptionCount`'s already-proven concurrency-safe pattern
  (`ICouponRepository.TryRedeemAsync`) — a subscription benefit racing
  against itself under concurrent bookings needs the same guarantee a coupon
  redemption does.

### Data model

| Table | Purpose |
|---|---|
| `subscription_plan` | name, price, billing_cycle, free_visits_included, discount_percent, priority_slot_flag, is_active |
| `customer_subscription` | customer_id, plan_id, status (active/cancelled/expired/payment_failed), current_period_start/end, free_visits_remaining, next_billing_date |

### API surface

- Customer-facing (`consumer-api`): browse plans, subscribe, cancel, view
  active subscription + remaining benefits
- Admin-facing (`admin-api`): plan CRUD (price, cycle, benefits, active window)

### Failure handling

A failed recurring charge retries with backoff before the subscription
auto-suspends (not auto-cancels) — a subscriber shouldn't lose an active
plan over one declined card without a chance to fix payment details.

---

## 2. RECURRING / SCHEDULED BOOKINGS

**Tasks:** `#184`–`#188`

A customer sets up a repeating booking (e.g. weekly cleaning); the system
creates a real booking ahead of each occurrence automatically.

### Design principle: reuse, don't duplicate

**The scheduler creates a booking through the existing creation
orchestration** (`#58`, which already enforces identity, active catalog,
serviceable address, valid slot, price snapshot, payment policy) — this is
not a second, parallel booking-creation path with its own validation rules.

### Data model

| Table | Purpose |
|---|---|
| `recurring_booking_plan` | customer_id, service_id, address_id, recurrence_rule (weekly/biweekly/monthly + day/time), status (active/paused/cancelled), start_date, end_date/occurrence_count |

### Occurrence scheduling

A Hangfire job runs ahead of each occurrence (not at the moment it's due —
enough lead time to catch and surface a problem before the customer expects
the visit) and calls `#58`'s orchestration. **If the slot is no longer
available, the occurrence is skipped and the customer is notified** — it
does not silently fail, and it does not book a different slot without
asking.

### API surface

Create/pause/cancel a recurring plan; list upcoming occurrences
(`consumer-api`).

---

## 3. IN-APP CHAT

**Tasks:** `#189`–`#194`

Real-time messaging tied to a booking or a support ticket — not a general
messaging platform.

### Design principle: reuse, don't duplicate

**Threads are scoped to an existing context** (`booking` or
`support_ticket`, via `context_type`/`context_id`) — there is no
free-standing "start a conversation with anyone" surface. **Transport is
SignalR**, ASP.NET Core's native real-time option — no new third-party
messaging dependency for something the framework already provides.

### Data model

| Table | Purpose |
|---|---|
| `chat_thread` | context_type (booking / support_ticket), context_id |
| `chat_message` | thread_id, sender_id, sender_type, body, sent_at, read_at — append-only, mirrors the append-only pattern already used for `WalletLedgerEntry` and `booking_status_history` |

### API surface

Get-or-create thread for a booking/ticket; send message; mark read;
paginated history (`consumer-api`). Admin support console and the provider
app/portal (`#149`) get their own reply view.

### Offline delivery

A message to an offline recipient falls back to push/SMS through the
**existing** notification dispatch (`#156`) — chat does not need its own
delivery channel, only its own trigger into the one that exists.

---

## 4. SERVICE COMPLETION VERIFICATION

**Tasks:** `#195`–`#198`

Photo proof and a checklist, required before a booking can be marked
complete — not an optional attachment a provider may or may not bother with.

### Design principle: this is a status-transition guard, not a form

The existing booking status transition matrix (`BookingStatus`, SRS 13.1/31)
allows `InProgress → Completed`. Task `#196` makes that transition
**conditional on a submitted `booking_completion_proof` row existing** —
enforced at the transition, not merely offered as a UI step a provider can
skip. This is the single highest-leverage change in Phase 10 for reducing
"did they even show up" disputes (`#155`), because it makes the evidence a
precondition of the status the dispute would otherwise contest.

### Data model

| Table | Purpose |
|---|---|
| `booking_completion_proof` | booking_id, photo_refs, checklist_answers, submitted_by (provider), submitted_at |
| `completion_checklist_template` (optional) | per-category/service checklist definition |

### Visibility

The customer sees the proof on their order detail/history (`#65`); admin
sees it as evidence during dispute review (`#155`) — the same artifact
serves both, not two separate records of "what happened."

---

## RBAC ADDITIONS

- **Subscription** — View / Configure
- **Chat** — View (support console access to threads)
- No new RBAC module for Recurring Bookings or Completion Verification —
  they extend existing Booking-module permissions rather than introducing a
  new administrative surface.

## OPEN DECISIONS

1. Subscription: is a lapsed/cancelled subscriber's unused
   `free_visits_remaining` forfeited immediately, or does it survive a grace
   period? (Not yet decided — affects whether cancellation needs its own
   wind-down state beyond simple `cancelled`.)
2. Recurring Bookings: does a missed occurrence (slot unavailable) count
   against the plan's `occurrence_count`, or does the plan extend by one? (Not
   yet decided.)
3. Chat: is message history retained indefinitely, or does it follow a
   retention policy tied to the booking/ticket's own lifecycle? (Not yet
   decided — has a direct SECURITY.md/data-retention angle.)
4. Completion Verification: can an admin override the guard for an edge case
   (provider's phone died mid-job, proof genuinely can't be captured), or is
   there truly no path to `Completed` without proof? (This doc assumes an
   admin override exists, logged and distinct from the normal provider-side
   submission — a hard block with zero override risks a real booking being
   unable to close for a reason that has nothing to do with the dispute the
   guard exists to prevent.)

## NEXT STEPS

1. Resolve the open decisions above, per feature.
2. Add the new tables to DATABASE.md as each feature moves from documented
   to implemented (not before — matches how PROVIDER.md and REFERRAL.md
   handle this).
3. Add endpoint contracts to API.md.
4. Extend the RBAC permission matrix for Subscription and Chat.
