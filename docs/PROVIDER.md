# PROVIDER.md

Provider / Vendor module specification.

## STATUS

**In implementation.** The open decisions below are resolved (task 144); the
data model (tasks 145a-145f) and provider auth/onboarding foundation (tasks
146a-146c) are being built against those decisions. Out of scope for Phase 1
per the SRS (§4.2 Excluded Direct End-User Interfaces, §34 Open Decision #9)
— this is the SRS's own release-phase terminology, unrelated to the
backlog's numbered phases below.

In the backlog (`tasks.csv`), Provider is scheduled as **Phase 7**, ahead of
Hardening & Launch (Phase 8) — moved there explicitly so provider/provider
work is done before launch, not after it. No longer "deferred" in the sense
of "after everything else"; only in the sense of "not yet built."

## PURPOSE

Nestly connects customers to services, but a person must actually fulfill each booking. This document defines the **Provider** (service provider) module: identity, availability, assignment, earnings, and how it integrates with the existing Customer/Booking/Admin domains without breaking module boundaries.

Note on terminology: the SRS uses "vendor" only to mean external third-party providers (payment gateway, SMS/Email/WhatsApp). The platform role described here — the person or company who fulfills a booking — is called **Provider**, matching the module name already listed in PROJECT.md's core module list.

## WHY THIS MODULE IS NEEDED

- Phase 1 assumes admins manually coordinate fulfillment behind the scenes. This does not scale past a small booking volume.
- A Provider role becomes necessary once providers need to see their own jobs, accept/reject work, mark completion, and get paid without an admin doing it by hand for every booking.
- Already referenced in PROJECT.md's core module list ("Identity, Customer, Provider, Catalog...") — this document is the detailed spec for that module.

## SCOPE BOUNDARY

- This module must remain independent of the Customer, Booking, and Admin domains.
- The Booking domain should depend on Provider through exactly one bridge entity (`booking_provider_assignment`) plus one denormalized display field (`assigned_provider_id`) on `booking`.
- No other Booking logic should read Provider internals directly.
- This boundary is what keeps the module extractable into a separate service later, consistent with ARCHITECTURE.md's modular monolith principle.

## DATA MODEL

### Identity Domain

| Table | Purpose |
|---|---|
| `provider` | id, legal_name, display_name, provider_type (individual/company), phone, email, status (pending_verification / active / suspended / deactivated), onboarding_status, created_at |
| `provider_auth_identity` / `provider_session` / `provider_otp` | Auth, mirrors the customer auth tables |
| `provider_kyc_document` | doc_type, doc_number, file_ref, verification_status, verified_by, verified_at |
| `provider_address` | Base/operating address(es) |

### Capability & Coverage Domain

| Table | Purpose |
|---|---|
| `provider_skill_mapping` | provider_id → category/service they're qualified for |
| `provider_service_area` | provider_id → city/zone/pincode coverage |
| `provider_availability` | Day-of-week windows, blackout dates — feeds the existing Slot Engine |
| `provider_capacity` | Max jobs per day/slot, if capacity-based dispatch is used |

### Assignment Bridge

| Table | Purpose |
|---|---|
| `booking_provider_assignment` | booking_id, provider_id, assigned_by (admin/system), assigned_at, status (assigned/accepted/rejected/reassigned), response_deadline |

### Financial Domain

| Table | Purpose |
|---|---|
| `provider_earning_ledger` | Append-only, mirrors `wallet_ledger` — credit per completed job, debit for penalties, references booking_id |
| `provider_payout` | payout_id, provider_id, period_start/end, total_amount, status (pending/processing/paid/failed), payout_reference |

### Reputation & Ops Domain

| Table | Purpose |
|---|---|
| `provider_rating_summary` | Rolled-up average/count (raw reviews stay in the existing `review` table plus a new `provider_id` column) |
| `provider_note` | Admin-facing notes, mirrors customer notes |
| `provider_status_history` | Audit trail, mirrors `booking_status_history` |

## API SURFACE

### Provider-Facing (new `provider-api`, same pattern as `admin-api` / `consumer-api`)

- **Auth:** register, otp/send, otp/verify, login, refresh, logout
- **Profile/Onboarding:** get/update profile, upload KYC documents, get KYC status, update service areas, update skills
- **Availability:** get/update availability, set blackout dates
- **Jobs:** list jobs (filter by status/date), get job detail, accept/reject/start/complete job, upload completion proof
- **Earnings:** get earnings summary, get earnings ledger, list payouts, get payout detail

### Admin-Facing Additions (extend existing `admin-api`)

- Provider CRUD: list/create/update providers, get provider detail
- KYC approval: approve/reject provider KYC
- Assignment: assign provider to a booking
- Performance: get provider performance metrics
- Payouts: run payout batch, list payouts

## RBAC ADDITIONS

Two new permission modules added to the existing matrix (SRS §20):

- **Provider** — View / Create / Edit / Approve / Suspend
- **Payout** — View / Process / Approve

## REPOSITORY PLACEMENT

```
backend/
  provider-api/              new project, same shape as admin-api/consumer-api
  shared/
    Domain/Provider/         Provider, ProviderKycDocument, ServiceArea, Availability,
                             BookingProviderAssignment, EarningLedger, Payout
    Application/Provider/    RegisterProvider, VerifyKyc, AssignProviderToBooking,
                             AcceptJob, CompleteJob, CalculatePayout
    Infrastructure/Provider/ repositories, EF configurations
```

Booking domain changes are minimal: one nullable `AssignedProviderId` field for display; no other structural change.

## OPEN DECISIONS — RESOLVED (task 144)

All five decisions below are resolved for v1. Each pick is the simplest option
that does not block extending the model later — every decision keeps the door
open for the richer option (automatic assignment, company providers, gateway
payouts, rating-weighted assignment, multi-provider bookings) without a
breaking schema change, so a future phase can extend rather than migrate.

1. **Assignment: manual (admin-driven) in v1.** An admin explicitly assigns a
   provider to a booking via `booking_provider_assignment` (task 147). No
   auto-dispatch/matching engine is built now — that requires ranking logic
   (distance, skill, capacity, rating) that doesn't exist yet and would be
   premature to guess at. The bridge table's `assigned_by` column already
   distinguishes `admin` from `system`, so an automatic assignment engine can
   be added later purely as a new writer of that same table.

2. **Provider type: always an individual in v1.** `provider_type` is modeled as
   an enum with both `Individual` and `Company` values (matching this
   document's DATA MODEL), but the domain entity's public constructor only
   accepts `Individual` for now and rejects `Company` — there is no
   sub-technician concept, roster, or company-level auth in this phase. This
   keeps the column/enum shape ready for company providers later without
   implementing the (materially larger) multi-user-per-provider auth and
   assignment model now.

3. **Payouts: manual bank transfer in v1.** `provider_payout.status`
   (pending/processing/paid/failed) and `payout_reference` are free-text/
   admin-updated rather than driven by a payment-gateway webhook — an admin
   runs a payout batch and records the bank transfer reference by hand. No
   new gateway integration is added. (Note: `provider_payout` itself is part
   of the Financial Domain, scheduled beyond task 146c — this decision
   governs its eventual implementation, not something built in this pass.)

4. **Rating does not affect assignment in v1.** `provider_rating_summary`
   exists for display (provider performance views, admin provider detail) but
   the manual assignment flow (decision 1) does not read it to rank or
   restrict candidates. Once automatic assignment exists, rating becomes a
   natural input to that ranking — deferred, not discarded.

5. **Exactly one provider per booking.** `booking_provider_assignment` models a
   single current assignment per booking (reassignment replaces it, tracked
   via the `reassigned` status rather than a second concurrent row). No
   multi-provider/crew booking support in v1.

## NEXT STEPS

1. ~~Resolve the open decisions above.~~ Done (task 144).
2. Add table-by-table schema to DATABASE.md.
3. Add endpoint contracts to API.md.
4. Create `backend/provider-api`, mirroring the existing `admin-api`/`consumer-api` structure (task 149).
5. Extend the RBAC permission matrix and admin UI for provider management (task 150).
