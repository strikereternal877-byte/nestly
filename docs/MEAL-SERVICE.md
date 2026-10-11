# MEAL-SERVICE.md

Meal-priced services: a cook booked by meals and number of people, one-time
or monthly.

## STATUS

**Implemented**, branch `feat/monthly-service-plans`, 2026-10-11. Builds on
[MONTHLY-SERVICE.md](MONTHLY-SERVICE.md) (monthly engagements) and the
normal booking flow (one-time visits).

## BUSINESS DECISIONS (product owner, 2026-10-11)

1. **Both one-time and monthly.** A cook can be booked for a single visit
   (normal booking: slot, payment, assignment) or as a monthly engagement
   (same cook on chosen weekdays, billed at month end for days cooked).
2. **Two visits a day when needed.** Breakfast and lunch are cooked on a
   morning visit, snacks and dinner on an evening visit. A household that
   wants both gets two visits, each priced and timed on its own.
3. **Price = meal rate + extra people.** Each meal has a base rate covering
   up to N people and a rate per extra person. Cooking time grows the same
   way.

## PRICING RULES

Per meal (`MealOption`, stored on the service):

| Field | Meaning |
|---|---|
| `BaseRate` | Price for up to `IncludedPersons` |
| `IncludedPersons` | People covered by the base rate (1-12) |
| `ExtraPersonRate` | Added for each person beyond that |
| `BaseMinutes` | Cooking time for up to `IncludedPersons` (15-480) |
| `MinutesPerExtraPerson` | Added time per extra person (0-60) |
| `DefaultStartTime` | When the meal's visit normally starts; must fall in the meal's session |

For the meals chosen, grouped by session (morning = before 15:00):

```
visit rate     = sum over its meals of  BaseRate + max(0, people - IncludedPersons) x ExtraPersonRate
visit minutes  = sum over its meals of  BaseMinutes + max(0, people - IncludedPersons) x MinutesPerExtraPerson
visit start    = earliest DefaultStartTime of its meals (monthly: the customer may move it +/- 1 hour)
```

The pure rules live in `Nestly.Domain.Meals.MealPricing`; the customer site
mirrors them (`lib/meals.ts`) only to show live prices. The server always
recomputes.

The service's listing price (`Service.Price`, "Starts at") is kept equal to
the cheapest meal's base rate whenever the meal rates are saved.

## ONE-TIME BOOKING

- `PriceCalculationRequest` / `BookingSummaryRequest` carry
  `Meals: { meals, persons }`. Required for, and only accepted by, a
  `MealBased` service.
- One booking is one visit: meals spanning both sessions are rejected
  (`Pricing.MealsSpanSessions`), and the slot must start in the meals'
  session (`Booking.MealSlotMismatch`).
- The quote is snapshotted through the booking's existing variant fields:
  `VariantNameSnapshot = "Lunch + Dinner · 4 persons"`,
  `VariantDurationMinutesSnapshot = cooking minutes`. Every booking screen
  (customer, provider, admin) already shows these, so no booking-table
  change was needed.
- Quantity is always 1; the "repeat this booking" opt-in is hidden (the
  monthly plan is the recurring path).
- Rescheduling keeps the session: the new slot must be in the same session
  as the booking's own (`Reschedule.MealSessionMismatch`), and the
  reschedule page offers only those slots.

## MONTHLY

- A `MonthlyServicePlan` with `Basis = MealBased` carries no rate
  (`RatePerVisit = 0`) and must use `Frequency = Weekdays`. Its
  `MonthlyDiscountPercent` (0-50) makes each monthly visit that much cheaper
  than the same meals booked one-time.
- `POST /api/v1/monthly-service/meal-contracts` takes plan, address, days,
  start date, meals, persons and optional morning/evening visit times, and
  creates **one contract per session** (so up to two), validated together
  before any is saved.
- Each contract snapshots its quote: `RatePerVisitSnapshot`,
  `HoursPerVisitSnapshot` (minutes / 60), `IncludedTasksSnapshot` (the meal
  names), plus `PersonsSnapshot` and `MealsMask`. Attendance, invoicing,
  disputes and the professional's schedule-clash check then work unchanged.
- One live contract per service, address **and session**: a morning and an
  evening cook can both run at one address; a second morning cook cannot.
- The generic `POST monthly-service/contracts` refuses a MealBased plan
  (no meals given).

## PAYMENT

The customer chooses on the cook page:

| | Online | Wallet |
|---|---|---|
| **One-time** | Normal checkout payment | Checkout opens with "use wallet balance" on (`wallet=1`). The wallet covers what it can; the rest is paid online. A booking the wallet covers fully is confirmed with no payment step (existing checkout rule) |
| **Monthly** | One bill at month end (`BillingMode = MonthEnd`); can be set to auto-pay from wallet on the contract page | **Default.** Each day paid from the wallet before the visit (`BillingMode = DailyWallet`); a day the wallet can't pay for is not booked - see MONTHLY-SERVICE.md DAILY WALLET |

## ADMIN

- Set the service's pricing type to `MealBased`, then
  `PUT /api/v1/admin/catalog/services/{id}/meal-options` with the full list
  of meal rates (audited as `MealOptionsUpdated`).
- Create a monthly plan with basis `MealBased` for each city.
- Professionals need the service's category as a skill, like any service.

## DATA

| Column | Purpose |
|---|---|
| `service.meal_options` | JSON array of `MealOption` (text column, read whole with the service) |
| `monthly_service_contract.persons_snapshot` | People cooked for (meal plans) |
| `monthly_service_contract.meals_mask` | Meals cooked per visit, bit = `MealType` ordinal |

Migration: `20261010193735_AddMealBasedPricing`. Local seed:
`database/seed/dev-home-cook-seed.sql` (category Cooking, service Home Cook,
Jaipur mappings, monthly plan).

## KNOWN LIMITS

1. **City pricing.** Meal rates are per service, not per city. City price
   overrides do not apply to meal-priced services.
