# REFERRAL.md

Referral (Refer & Earn) module specification.

## STATUS

Not implemented. Scoped as **Phase 9**, after every core module (0–7) and the
deferred Provider module (8). It has a real dependency on Provider's earning
concepts existing first only in spirit — in practice it depends only on
Wallet, Coupon, Customer, and Booking, all of which are Phase 0–4 modules
already built. It is placed after Phase 8 because it's a growth feature on
top of a working marketplace, not a prerequisite for one.

## PURPOSE

A customer who refers someone gets a reward when that referral turns into
real business; the person they referred gets a reward too, as an incentive to
complete their first booking. This document defines the **Referral** module:
how a code is generated and shared, what "counts" as a qualifying referral,
how the reward is paid out, and how it plugs into Wallet, Coupon, Booking,
and Notifications without duplicating any of them.

## DESIGN PRINCIPLE: REUSE, DON'T DUPLICATE

Nestly already has two mechanisms this feature needs — this module is
deliberately built as a thin layer on top of them, not a parallel system:

- **"Points" are wallet credit.** `WalletLedgerEntry` (SRS 14.5, SRS 23.4) is
  already an append-only, audited, source-tracked ledger — exactly what a
  points balance needs to be. Referral rewards are credited there via a new
  `WalletSourceType.ReferralReward`, the same way a refund or a promotional
  credit is today (see [Wallet.cs](../backend/shared/Domain/WalletLedgerEntry.cs)).
  There is no separate "points" table.
- **"Coupon" rewards are `Coupon` rows**, issued programmatically at the
  qualifying moment, using the entity that already exists (SRS 11.10, 14.2)
  — not a new discount mechanism.
- **The admin config pattern already exists** (Coupon, Commission #157) —
  `ReferralProgramConfig` follows the same shape: one admin-editable row (or
  small versioned set) that governs behavior, not hardcoded values.

## HOW IT WORKS

```
Referrer shares their code/link
        │
        ▼
Referee registers using the code  →  Referral row created (status: Registered)
        │
        ▼
Referee completes their FIRST booking, amount ≥ configured minimum
        │
        ▼
Referral marked Qualified  →  Reward disbursed to both sides  →  Referral marked Rewarded
```

A referral that never reaches a qualifying booking within the configured
expiry window is marked **Expired** — no reward, no error, just a closed row.

## DATA MODEL

### Referral Domain

| Table | Purpose |
|---|---|
| `referral` | id, referrer_customer_id, referee_customer_id, referral_code_used, status (registered / qualified / rewarded / expired / fraud_flagged), qualifying_booking_id (nullable), registered_at, qualified_at, rewarded_at, expires_at |
| `referral_program_config` | Admin-editable, mirrors `Coupon`'s shape: referrer_reward_type (wallet_credit / coupon), referrer_reward_value, referee_reward_type, referee_reward_value, min_qualifying_order_amount, referral_expiry_days, max_referrals_per_customer (fraud cap, nullable = unlimited), is_active, effective_from, effective_to |

### Reuses (no new tables)

| Existing entity | How referral uses it |
|---|---|
| `Customer` | Gains one nullable `ReferralCode` field + a `GenerateReferralCode()` method, generated lazily on first request — not at signup, since most customers never share it |
| `WalletLedgerEntry` | New `WalletSourceType.ReferralReward` value; `SourceReferenceId` points at the `referral` row |
| `Coupon` | When `referee_reward_type = coupon`, a single-use `Coupon` row is created for that specific referee at the qualifying moment (not a shared code) |
| `NotificationEvent` | New `NotificationEventType` values: `ReferralRegistered`, `ReferralRewardCredited` |
| `Booking` | Read-only trigger point: the booking-completion path checks for a pending `referral` row keyed by the customer, exactly once, the same way it already triggers `RefundProcessed`/`BookingConfirmed` notifications |

## FRAUD / ABUSE PREVENTION

A referral program is a direct cash-cost surface — this is not optional
scope, it is the first thing an attacker probes:

- **Self-referral block**: referrer and referee must not resolve to the same
  person by mobile number or email (checked at registration, not just at
  reward time).
- **One referral per referee, ever**: a customer can be the *referee* on at
  most one `referral` row, enforced by a unique constraint on
  `referee_customer_id` — you cannot be "referred" twice for repeat rewards.
- **Per-customer referral cap**: `max_referrals_per_customer` on
  `referral_program_config` limits how many times one referrer can be
  rewarded, admin-configurable, not hardcoded.
- **Manual review queue, not auto-block, for soft signals**: same
  device/payment-method as the referrer, or a qualifying booking cancelled
  right after reward — these flag the row `fraud_flagged` for admin review
  rather than silently reversing money, since the wallet ledger is
  append-only and cannot un-credit an entry (SRS 14.5) — a fraud reversal
  must be its own explicit debit entry with its own audit trail, never a
  deletion.

## API SURFACE

### Customer-Facing (extend existing `consumer-api`)

- `GET /me/referral` — code, shareable link, lifetime stats (invited /
  qualified / rewarded / total earned)
- `GET /me/referral/history` — list of this customer's referrals with status
- `POST /auth/register` — extended to accept an optional `referralCode`

### Admin-Facing (extend existing `admin-api`)

- Referral program config: get/update (mirrors Coupon/Commission admin config)
- Referral list/detail: filter by status, search by customer
- Fraud review: approve or reject a `fraud_flagged` row
- Reports: funnel (invited → registered → qualified → rewarded) and total
  program cost over a date range

## RBAC ADDITIONS

One new permission module added to the existing matrix (SRS §20):

- **Referral** — View / Configure / Approve-Fraud / Export

## NOTIFICATION EVENTS

Two additions to the existing trigger-wiring framework (SRS 19.1, tasks
#87–#88, #156), dispatched through the same email/SMS/push channels already
built — no new delivery mechanism:

- `ReferralRegistered` → referrer notified their invite was used
- `ReferralRewardCredited` → both referrer and referee notified their reward
  landed in their wallet (or their coupon is ready)

## REPOSITORY PLACEMENT

```
backend/
  shared/
    Domain/Referral/         Referral, ReferralProgramConfig, ReferralStatus,
                              RewardType (wallet_credit / coupon)
    Application/Referral/    GenerateReferralCode, RegisterWithReferralCode,
                              EvaluateQualifyingBooking, DisburseReferralReward,
                              FlagForFraudReview
    Infrastructure/Referral/ repositories, EF configurations, migration
  consumer-api/.../Controllers/ReferralController.cs
  admin-api/.../Controllers/AdminReferralController.cs
  admin-web/.../referral/    program config, referral list/detail, fraud queue, reports
  customer-web/.../referral/ refer-and-earn screen
```

Booking domain changes: **none structural.** The completion path gains one
read of `IReferralRepository` to check for a pending referral, the same
shape as the existing notification-trigger check — no new field on `Booking`
itself.

## DECISIONS

1. **Resolved** — the referee's reward requires their *own* qualifying
   booking to complete; it is not credited on registration alone. Both sides
   of the referral are rewarded on the same event (the referee's qualifying
   completion), which closes the "create a throwaway account for the signup
   bonus, never book" loophole and is what task #165 (reward disbursement)
   and #164 (qualifying hook) are built against.

## FUTURE ENHANCEMENTS (tasks #174–176)

Queued after the base loop (#161–173), not before it — each depends on the
base pieces existing first:

- **Milestone rewards** (#174) — a bonus on top of the per-referral reward
  when a referrer's qualified-referral count crosses an admin-set threshold
  (5th, 10th, ...). New `referral_milestone` table; disbursed through the
  same reward-disbursement path as a normal referral reward, not a second one.
- **Expiring wallet credit** (#175) — referral credit that expires unused
  instead of sitting on the books indefinitely. This is **not** a small
  addition: `WalletLedgerEntry` today is a single running-balance ledger
  (SRS 14.5) with no concept of tracking how much of *one specific* credit
  entry remains unspent once other debits have drawn against the balance.
  The sweep job must not be built before that consumption-tracking (FIFO
  allocation) model is designed — task #175 says this explicitly so it
  isn't skipped under time pressure.
- **Contextual prompts** (#176) — surface Refer & Earn right after a
  completed booking or a 4–5★ review, not only as a static screen a customer
  has to go find. Reuses the existing notification trigger framework; no new
  delivery mechanism.

**Explicitly not queued**: a public referrer leaderboard. It's a
gamification feature, not a conversion lever — it requires its own
privacy/opt-in decision (ranking exposes a customer's referral activity
publicly) and, worse, incentivizes exactly the fake-account behavior the
fraud review queue (#166) exists to catch, before that queue has been
proven out. Revisit only after the base loop and fraud queue are live.

## OPEN DECISIONS (CLOSED)

Closed 2026-08-01, ahead of task 161, same convention as SRS §34 (`docs/SRS.md`)
and PROVIDER.md's open-decisions lists — resolved with a documented rationale
rather than re-litigated per task.

1. **Reward type default: admin's choice per campaign**, via `RewardType` on
   `referral_program_config` (confirms this doc's working assumption). A
   fixed platform-wide choice would prevent running a coupon-based campaign
   for slow categories and a cash-like wallet-credit campaign for
   high-value ones at the same time — the config-driven shape this doc
   already specifies is strictly more capable at no extra cost, so there's
   no reason to narrow it.
2. **Self-redemption of one's own code: blocked** (confirms this doc's
   working assumption). The self-referral block already required for
   registration-time abuse prevention (mobile/email match, task 163) covers
   this for free — a customer's own code fails the same check a stranger's
   attempted double-identity would.
3. **`fraud_flagged` pauses only that referral's payout, not the referrer's
   ability to keep referring.** A flagged row is a *soft* signal (same
   device/payment method as the referrer, a suspiciously-timed
   post-reward cancellation) — not a confirmed finding. Suspending
   referral eligibility on a soft signal punishes false positives (a
   household sharing a device/card is common and legitimate) before a
   human has reviewed anything. The per-customer referral cap
   (`max_referrals_per_customer`) already bounds one referrer's exposure
   while a flag sits in the queue; if a pattern is confirmed abusive, the
   existing customer block/unblock action (task 101c) is the right,
   already-audited tool to actually stop them — not a second,
   referral-specific suspension mechanism duplicating it.

## NEXT STEPS

1. Resolve the open decisions above.
2. Add the two new tables to DATABASE.md.
3. Add the endpoint contracts to API.md.
4. Extend the RBAC permission matrix and admin UI for referral management.
5. Wire the booking-completion path's read of `IReferralRepository`.
