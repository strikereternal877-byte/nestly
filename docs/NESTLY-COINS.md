# NESTLY-COINS.md

Nestly Coins (loyalty/incentive currency) module specification.

## STATUS

Not implemented. Scoped as **Phase 11**, after Referral & Growth (Phase 9) and
Product Enhancements (Phase 10). It depends only on Wallet, Booking, Customer,
and Provider (all already built) — placed last because it is a growth/retention
lever on top of a working marketplace, not a prerequisite for one, same
reasoning as REFERRAL.md's phase placement.

## PURPOSE

Nestly Coins are awarded to **both customers and providers** to encourage
repeat activity — a customer re-ordering a service, a provider accepting and
completing more jobs — rather than a one-time acquisition incentive (that's
Referral's job, see [REFERRAL.md](REFERRAL.md)). This document defines: how
coins are earned, how they're redeemed, expiry, admin configuration, and fraud
prevention, so the two existing growth mechanisms (Referral, Coins) stay
clearly scoped and don't duplicate each other.

**Coins vs. Referral, at a glance:**

| | Referral | Nestly Coins |
|---|---|---|
| Trigger | Inviting a new person who converts | Any qualifying order (repeat or first) by an existing user |
| Who earns | Referrer + referee, once per referee | Customer or provider, every qualifying order |
| Purpose | Acquisition | Retention / reordering incentive |

## DESIGN PRINCIPLE: REUSE, DON'T DUPLICATE

Same principle as REFERRAL.md — Nestly Coins is a thin layer over
infrastructure that already exists:

- **Coins are wallet credit**, exactly like Referral rewards. Coin credits use
  a new `WalletSourceType.NestlyCoinsReward` value on the existing
  `WalletLedgerEntry` (SRS 14.5, SRS 23.4) — there is no separate "coins
  balance" table for customers. A customer's wallet balance and their coin
  balance are the same number; the ledger's `SourceType` is what tells you a
  given credit came from a reorder incentive rather than a refund.
- **Provider coins are provider earning-ledger credit**, reusing
  `ProviderEarningLedgerEntry` (PROVIDER.md's Financial domain,
  `ProviderEarningSourceType`) the same way a completed job already credits a
  provider — a new `ProviderEarningSourceType.NestlyCoinsReward` value, not a
  parallel provider wallet.
- **The admin config pattern already exists** (Coupon, Commission #157,
  `ReferralProgramConfig`) — `NestlyCoinsProgramConfig` follows the same
  shape: one admin-editable row governing earn rates and rules, not hardcoded
  values.

## HOW IT WORKS

```
Qualifying order placed (reorder, or first order if the program allows it)
        │
        ▼
Order reaches Completed
        │
        ▼
Coins credited to the customer's wallet (WalletSourceType.NestlyCoinsReward)
        │
        ▼
If a provider fulfilled it: coins also credited to the provider's earning
ledger (ProviderEarningSourceType.NestlyCoinsReward)
```

Coins are credited **once per completed order**, at completion — same timing
as when a provider's job-completion earning is credited, and same "no
speculative pre-crediting before the service is actually delivered" principle
Referral's qualifying-booking design already established.

## GUIDELINES (what "proper guidelines" means in practice)

These are the rules the admin config and the earn/redeem logic must enforce —
written down now so implementation doesn't have to re-derive them:

1. **Coins are earned on order value, admin-configured, not hardcoded.**
   `NestlyCoinsProgramConfig` defines an earn rate (e.g. coins per ₹100
   spent), separately configurable for customers and providers, and a
   `MinimumOrderAmount` below which no coins accrue (prevents gaming via
   many tiny orders).
2. **Reordering is incentivized, not just any order.** The default program
   only credits coins on a customer's 2nd+ completed order for a given
   service/category (configurable via `RequireReorder: bool` on the config)
   — a first-time order is Referral's or a promotional coupon's job, not
   Coins'. Admin can turn this off to run a blanket earn-on-every-order
   campaign if desired.
3. **Coins expire — this is a first-class design constraint, not an
   afterthought.** Unlike a plain wallet credit, Nestly Coins carry an
   `ExpiresAt` the sweep job checks. This requires the same **FIFO
   consumption-tracking model** Referral's own FUTURE ENHANCEMENTS #175
   ("Expiring wallet credit") already flagged as a real, non-trivial
   prerequisite — `WalletLedgerEntry` today has no concept of how much of
   one specific credit remains unspent once other debits draw against the
   balance. **Nestly Coins must not be built before that FIFO model is
   designed**, or expiry becomes unenforceable the moment a customer spends
   part of their balance. If #175 is picked up first, Coins should build on
   it directly rather than inventing a second expiry mechanism.
4. **Redemption is spending down the wallet balance, not a separate
   "redeem coins" action.** Since coins are wallet credit, redemption already
   works exactly like today's wallet-balance-at-checkout flow — no new
   redemption UI or endpoint, only a breakdown showing how much of the
   applied balance was coins vs. other credit (for transparency, using the
   same ledger `SourceType` tagging).
5. **One coins program per side, admin-versioned.** Customer-side and
   provider-side configs are independent rows (different earn rates, different
   minimums) — a provider accepting more jobs and a customer reordering more
   are different behaviors being incentivized, not the same lever.

## FRAUD / ABUSE PREVENTION

Same posture as REFERRAL.md — a coins program is a direct cash-cost surface:

- **Credited only on `Completed` orders**, never on `Confirmed`/`InProgress`
  — a cancelled-after-credit order must reverse via an explicit debit entry
  (never a deletion; the ledger is append-only, SRS 14.5), same as Referral's
  fraud-reversal rule.
- **Per-customer/per-provider earn cap**, admin-configurable
  (`MaxCoinsPerMonth`, nullable = unlimited) — bounds exposure the same way
  Referral's `max_referrals_per_customer` does.
- **Order-cancellation clawback window**: if an order that already credited
  coins is cancelled/refunded within `ClawbackWindowDays` of completion, the
  credit is reversed via a debit entry with a clear audit reason — not a
  silent adjustment.

## DATA MODEL

### New

| Table | Purpose |
|---|---|
| `nestly_coins_program_config` | Admin-editable: `audience` (customer/provider), `earn_rate_per_100`, `minimum_order_amount`, `require_reorder`, `max_coins_per_month` (nullable), `expiry_days`, `clawback_window_days`, `is_active`, `effective_from`, `effective_to` |

### Reuses (no new balance tables)

| Existing entity | How Coins uses it |
|---|---|
| `WalletLedgerEntry` | New `WalletSourceType.NestlyCoinsReward`; `SourceReferenceId` points at the completed booking |
| `ProviderEarningLedgerEntry` | New `ProviderEarningSourceType.NestlyCoinsReward`; same `SourceReferenceId` convention as `JobCompletion` |
| `Booking` | Read-only trigger point: the existing completion path (where `RefundProcessed`/`BookingConfirmed`/Referral's qualifying-booking check already hook in) gains one more check |
| `NotificationEvent` | New `NotificationEventType` values: `NestlyCoinsCredited` (customer), `NestlyCoinsCreditedProvider` |

## API SURFACE

### Customer-Facing (extend `consumer-api`)

- `GET /me/wallet` — unchanged shape; ledger entries with
  `sourceType: NestlyCoinsReward` are how a customer sees their coins history
  (no new endpoint — this is why reusing Wallet matters).
- `GET /nestly-coins/program` — public: current earn rate/rules, for
  in-app messaging ("earn coins on your next order").

### Provider-Facing (extend `provider-api`)

- `GET /earnings/ledger` — unchanged shape; entries with
  `sourceType: NestlyCoinsReward` are how a provider sees their coins history.

### Admin-Facing (extend `admin-api`)

- Coins program config: get/update, one per audience (mirrors
  Coupon/Commission/Referral admin config).
- Reports: coins issued vs. redeemed, program cost over a date range (mirrors
  Referral's funnel/cost report).

## RBAC ADDITIONS

One new permission module (SRS §20): **NestlyCoins** — View / Configure /
Export. No Approve-Fraud action (unlike Referral) — clawback is automatic on
cancellation, not a manual review queue, since it is a straightforward
percentage-of-order reversal rather than a suspected-fraud judgment call.

## NOTIFICATION EVENTS

- `NestlyCoinsCredited` → customer notified their coins landed in their
  wallet after a qualifying order completes.
- `NestlyCoinsCreditedProvider` → provider notified the same, for their
  earning ledger.

Dispatched through the existing email/SMS/push channels (SRS 19.1) — no new
delivery mechanism, same as every other module in this backlog.

## REPOSITORY PLACEMENT

```
backend/
  shared/
    Domain/                       WalletSourceType.NestlyCoinsReward,
                                   ProviderEarningSourceType.NestlyCoinsReward
                                   (new enum members on existing types)
    Domain/NestlyCoins/           NestlyCoinsProgramConfig
    Application/NestlyCoins/      EvaluateQualifyingOrder, CreditCustomerCoins,
                                   CreditProviderCoins, ClawbackOnCancellation
    Infrastructure/NestlyCoins/   repository, EF configuration, migration
  consumer-api/.../               no new controller - GET /nestly-coins/program
                                   only; wallet already exposes the ledger
  admin-api/.../Controllers/      AdminNestlyCoinsController.cs
  admin-web/.../nestly-coins/     program config, reports
```

Booking domain changes: **none structural** — the completion path gains one
more read (qualifying-order check), the same shape as the existing
notification-trigger and Referral qualifying-booking checks.

## DECISIONS

1. **Resolved — Coins are wallet/earning-ledger credit only, never a coupon.**
   Unlike Referral (which lets an admin choose wallet-credit vs. coupon per
   campaign), Coins has exactly one reward type. A reorder incentive is
   naturally a running balance a customer builds up over many orders — a
   single-use coupon per order doesn't fit that shape, and offering a choice
   here would just be unused flexibility (the CLAUDE.md principle against
   speculative configurability applies).
2. **Resolved — providers earn Coins too, unlike Referral (provider side is
   out of Referral's scope entirely).** Nestly Coins' whole purpose is
   reordering/repeat-activity incentive on both sides of the marketplace,
   not just the demand side — a provider accepting and completing more jobs
   is exactly the behavior this program exists to reward.

## OPEN DECISIONS — resolved 2026-08-02 (task 199)

1. **Resolved — first order does not qualify; `RequireReorder: true` stays
   the shipped default.** Kept as this doc's own default rather than
   overridden: a blanket first-order incentive would compete directly with
   Referral's acquisition budget for the same event (a new-to-the-platform
   customer's first order), which is exactly the overlap PURPOSE's
   Coins-vs-Referral table exists to prevent. `RequireReorder` stays
   admin-toggleable per GUIDELINES #1 so this can be revisited with real
   usage data without a code change — the open question was which value
   ships as the default, not whether the toggle exists.
2. **Resolved — reorder counting stays "any completed order" (not scoped to
   the same service/category).** `RequireReorder` is a single boolean on
   `NestlyCoinsProgramConfig`, not a per-category counter, matching the
   "no speculative configurability" principle already applied to DECISIONS
   #1 (single reward type) — a per-category reorder count would require a
   new per-customer-per-category counter table for a distinction the doc's
   own fraud/abuse posture doesn't need (the earn cap and clawback window
   already bound exposure regardless of what "reorder" means precisely).
   Narrow this later only if usage data shows the blanket definition is
   too generous, per the original open-decision text.
3. **Resolved — the wallet FIFO consumption-tracking prerequisite is fully
   satisfied on `main`, including the scheduled sweep.** Verified directly
   against `main` (commit `fc37980`) - **correction, superseding an earlier
   pass of this same resolution that had it wrong**: `WalletLedgerEntry.
   RemainingAmount`/`ExpiresAtUtc` exist, `WalletService.ConsumeExpiringCreditsAsync`
   draws down the soonest-to-expire outstanding credit first on every debit,
   `IWalletService.ExpireCreditAsync` is a working write-off primitive, AND
   `WalletCreditExpirySweepJob.SweepAsync` **is** registered via Hangfire
   (`RecurringJob.AddOrUpdate<IWalletCreditExpirySweepJob>(..., Cron.Daily)`
   in `admin-api/Program.cs`, guarded by `BackgroundJobOptions.ServerEnabled`)
   and covered by `WalletCreditExpiryTests.cs`. An earlier version of this
   section claimed the sweep didn't exist on `main` and only lived in an
   unmerged worktree - that was wrong, based on trusting task 175's stale
   `tasks.csv` status instead of the actual code (`git log` confirms the
   sweep landed in commit `7a8c36f`, an ancestor of this branch's fork
   point). Task 175's `tasks.csv` row has been corrected to `done`
   accordingly. Consequence for Coins: tasks 200-203 can credit coins with
   `expiresAtUtc` set and get both correct FIFO consumption ordering *and*
   automatic write-off of anything left unspent past expiry - GUIDELINES #3
   is fully unblocked, no follow-up sweep work needed.
4. **New, real gap found while implementing task 201, tracked in NEXT STEPS
   #1**: `WalletService.ExpireCreditAsync` unconditionally tags every
   write-off `WalletSourceType.ReferralCreditExpiry`, regardless of the
   expiring credit's actual origin - written when only Referral credits
   could expire, never revisited once the mechanism became reusable. An
   expired, never-spent Nestly Coins credit will be swept and correctly
   debited, but mislabeled in the ledger as a Referral event. Not fixed in
   this task's scope (task 201 is EvaluateQualifyingOrder/CreditCustomerCoins/
   CreditProviderCoins/ClawbackOnCancellation, not the shared sweep), and
   deliberately not worked around by giving Coins a second, competing sweep
   either - see NEXT STEPS #1.

## NEXT STEPS

1. **Open, tracked gap**: `WalletService.ExpireCreditAsync` needs to tag its
   write-off entry using the *expiring credit's own* source (e.g.
   `WalletSourceType.NestlyCoinsExpiry` for a Coins credit) instead of
   unconditionally `ReferralCreditExpiry`. Small, contained fix (the sweep
   job already has the original entry in hand to pass its source through)
   but touches Referral's existing, tested `IWalletService.ExpireCreditAsync`
   signature, so it's left as its own follow-up rather than folded into
   task 201.
2. Add `nestly_coins_program_config` to DATABASE.md.
3. Add the endpoint contracts to API.md.
4. Extend the RBAC permission matrix and admin UI for the NestlyCoins module.
5. Wire the booking-completion path's qualifying-order check (customer side)
   and job-completion path's check (provider side).
