# MONTHLY-SERVICE.md

Monthly Service module specification: maid-style, month-long service
engagements with the same professional on fixed days, attendance-based
month-end billing.

## STATUS

**Implemented (MVP)**, branch `feat/monthly-service-plans`, 2026-09-29.
This document is the specification; [ORIENTATION.md](ORIENTATION.md) owns
current repository state.

## PURPOSE

A customer engages one professional (maid, house help) to come on chosen
days of the week at a fixed time, for as long as they want, and pays at the
end of each month for the visits that actually happened. This is
structurally different from every existing prepaid/recurring module:

| | Recurring Booking Plan | Subscription | AMC | **Monthly Service** |
|---|---|---|---|---|
| Unit | One booking per occurrence | Discount/free-visit tier | Prepaid visit entitlement | Daily attendance |
| Cadence | Weekly / biweekly / monthly, one weekday | Billing cycle | Customer-initiated | Any set of weekdays (daily = all 7) |
| Payment | Each occurrence paid like a booking | Upfront per cycle | Upfront | **Postpaid, month end, per attended visit** |
| Professional | Standing provider, may be reassigned | N/A | Any | **Same professional, no replacement** |
| Record of work | Booking lifecycle | N/A | Booking lifecycle | Attendance register |

A daily maid visit is not a booking: creating 26 bookings a month, each with
its own payment order, slot capacity, assignment offer and escrow, would be
wrong for the customer (26 payments) and for the professional (26 offers to
accept). The module therefore does **not** create bookings. Its record of
work is an attendance register.

## BUSINESS DECISIONS (product owner, 2026-09-29)

1. **Plan basis: both.** A plan is either *hourly* (e.g. 2 hours per visit)
   or *task-based* (e.g. sweeping + mopping + dishes). Task-based plans list
   the included tasks; hourly plans may list them too as guidance.
2. **Customer absence is handled by attendance.** Every scheduled day has an
   attendance record; only visits that happened are billed.
3. **Same professional throughout.** If the professional takes leave, that
   day is not billed and no replacement is sent. Replacing the professional
   is an admin action only (e.g. they leave the platform). On replacement
   the outgoing professional is notified to stop, their upcoming days and
   leave go to the new professional, and the customer's skips carry over.
   Cancelling removes every upcoming day, including skips and leave.
4. **Billing at month end.** Postpaid: one invoice per contract per calendar
   month, for billable visits x rate per visit.

## SCHEDULES

Each plan fixes how its visits are scheduled; the customer then picks the
specific days when requesting:

| Plan schedule | Example | Customer picks |
|---|---|---|
| `Weekdays` | Maid, Mon-Sat | Any set of weekdays (all seven = daily) |
| `TimesPerWeek` (N) | Car wash 3x a week | Exactly N weekdays |
| `TimesPerMonth` (N) | Car wash 4x a month | Exactly N dates of the month, 1-28 |

Per-month dates stop at 28 so every month, February included, has every
chosen date. Everything downstream - attendance, skip/leave, disputes,
per-visit month-end billing, conflict checks between a professional's
contracts - works the same for all three.

## HOW IT WORKS

```
Admin publishes a MonthlyServicePlan (service, city, basis, rate per visit, commission %)
        |
Customer requests a contract: plan, address, weekdays, visit start time, start date
        |  -> Status PendingAssignment
Admin assigns a professional (conflict-checked against their other contracts)
        |  -> Status Active; attendance rows materialized for the next 14 days
Every scheduled day:
        |  professional checks in with the customer's 4-digit code for that day -> Present
        |  customer skipped before cutoff                                        -> CustomerSkipped
        |  professional marked leave in advance                                   -> ProviderLeave
        |  professional arrived, customer unavailable                             -> CustomerUnavailable
        |  nothing recorded by end of day                                         -> Absent
Month end (invoice day, default the 2nd):
        |  invoice = billable visits x rate (disputed months wait for admin resolution)
        |  due in 7 days; unpaid 7 days after due -> contract Paused
Invoice paid (online or recorded offline by admin)
        |  -> professional's earning ledger credited (amount - commission)
```

## ATTENDANCE SYSTEM

One `monthly_service_attendance` row per contract per scheduled date, created
ahead of time by the daily job (14-day horizon) so the customer and the
professional both see the upcoming schedule.

| Status | Set by | Billable |
|---|---|---|
| `Scheduled` | System (materialization) | - |
| `Present` | Professional check-in with the day's code, or customer confirmation | Yes |
| `CustomerSkipped` | Customer, before the skip cutoff (default 2 h before visit start) | No |
| `ProviderLeave` | Professional, before the visit start | No |
| `CustomerUnavailable` | Professional, after visit start (door locked, nobody home) | Yes (configurable) |
| `Absent` | System, day closed with nothing recorded | No |

**Proof of presence.** Each attendance row carries a random 4-digit code,
shown only in the customer's app for that day. The professional enters it at
check-in. A code the professional cannot see cannot be entered without being
at the house, which is the point. Check-in is accepted from 60 minutes before
to 180 minutes after the visit start; device coordinates are stored with the
check-in when the browser provides them. Check-out is optional and records
the actual time spent (useful for hourly plans).

**Fallback.** If the code flow fails (phone off, network), the customer can
confirm the visit themselves on the same day.

**Disputes.** The customer can dispute any billable day of a month that has
not been invoiced yet. An open dispute holds that contract's invoice for that
month until an admin resolves it (upheld: the day becomes non-billable with a
corrected status; rejected: unchanged). Once a month is invoiced its
attendance is locked.

## BILLING

- Period: calendar month in the business timezone (Asia/Kolkata).
- Generated on `InvoiceDayOfMonth` (default 2) of the next month, for every
  contract with at least one attendance row in the period. A month with zero
  billable visits produces no invoice.
- Amount = billable visits x rate per visit (snapshotted on the contract).
- Commission = amount x commission % (snapshotted); professional net = amount - commission.
- Due `InvoiceDueDays` (default 7) after issue. After a further
  `OverdueGraceDays` (default 7) unpaid, the contract is paused and its future
  schedule removed; paying the invoice resumes it.
- Payment: customer pays online (same sandbox gateway seam every other
  module uses today, see "Open decisions") or an admin records an offline
  payment (cash / UPI / bank transfer) with a reference.
- On payment the professional's earning ledger is credited with the net
  amount (`ProviderEarningSourceType.MonthlyServiceInvoice`), idempotently.
  Existing payout batches pick these credits up unchanged.

## DATA MODEL

| Table | Purpose |
|---|---|
| `monthly_service_plan` | Admin catalog: service, city, name, description, basis (Hourly/TaskBased), hours per visit, included tasks, rate per visit, commission %, schedule (frequency + times per period), active flag |
| `monthly_service_contract` | Aggregate root: customer, plan + term snapshots (incl. frequency), address, weekdays (bitmask), month dates (bitmask, per-month plans), visit start time, start/end date, assigned professional, status (PendingAssignment/Active/Paused/Cancelled), pause reason |
| `monthly_service_attendance` | One row per contract per date: status, day code, check-in/out time and coordinates, note, dispute fields, invoice id once invoiced |
| `monthly_service_invoice` | One per contract per month: visit counts by status, rate, amount, commission, net, status (Issued/Overdue/Paid), due date, payment method/reference |

## API SURFACE

Customer (`consumer-api`):
`GET monthly-service/plans`, `POST monthly-service/contracts`,
`GET me/monthly-service-contracts`, `GET me/monthly-service-contracts/{id}`,
`GET me/monthly-service-contracts/{id}/attendance?year&month`,
`POST me/monthly-service-contracts/{id}/cancel`,
`POST me/monthly-service-attendance/{id}/skip|unskip|confirm|dispute`,
`GET me/monthly-service-invoices`, `POST me/monthly-service-invoices/{id}/pay`.

Professional (`provider-api`):
`GET monthly-service/contracts`, `GET monthly-service/contracts/{id}/attendance?year&month`,
`GET monthly-service/visits?date`,
`POST monthly-service/visits/{id}/check-in|check-out|customer-unavailable|leave|cancel-leave`.

Admin (`admin-api`):
`admin/monthly-service-plans` (CRUD, activate/deactivate),
`admin/monthly-service-contracts` (search, detail, attendance, eligible
professionals, assign, pause, resume, cancel),
`admin/monthly-service-disputes` (open disputes, resolve),
`admin/monthly-service-invoices` (search, record offline payment).

RBAC: plans under `subscription.*` (same as AMC plans); contracts,
attendance and disputes under `bookings.*`; invoices and offline payment
recording under `payments.*`. No new admin module.

## OPEN DECISIONS

1. **Real payment gateway.** Online payment goes through the existing
   `IPaymentGateway` seam, which is the sandbox implementation platform-wide
   today (same position as Subscription billing and AMC purchase). Offline
   payment recording is fully functional and is expected to be the main
   channel at launch.
2. **Notifications.** No push/SMS for "maid checked in", "invoice issued" or
   "overdue" in this MVP; everything is visible in the apps. Wiring into the
   existing notification trigger framework is the next step.
3. **Conflicts with one-off bookings.** Assignment checks conflicts against
   the professional's other monthly contracts only, not against their
   one-off booking jobs. Professionals doing monthly work are expected to be
   dedicated to it at launch.
4. **Leave allowance.** Professional leave is unlimited and unbilled. A paid
   leave allowance (e.g. 2 days/month billed anyway) can be added as a plan
   field if the business wants it.
