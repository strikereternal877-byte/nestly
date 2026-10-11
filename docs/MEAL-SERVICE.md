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
4. **No trial discount** (2026-10-11). A first one-time visit is the trial,
   at the normal price; after it, the customer is offered to keep that cook
   monthly.
5. **Food preferences** (2026-10-11): diet, spice and cuisines, asked when
   a cook is booked, saved on the customer and pre-filled next time.

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
visit start    = earliest DefaultStartTime of its meals
```

Monthly: the customer may move a visit's start (product owner, 2026-10-11)
from an hour before its first meal's usual time to its last meal's usual
time, and never less than an hour either side of the first, in half-hour
steps, between 06:00 and 21:30, a morning visit staying before 15:00
(`MealVisitQuote.EarliestStart` / `LatestStart`, enforced when the contract
is created; `startChoices` in `lib/meals.ts` offers the same times). So
breakfast (7:30) + lunch (11:30) can start 6:30-11:30, snacks (16:30) +
dinner (19:00) 15:30-19:00, dinner alone 18:00-20:00. The cook page names
each visit's meals and shows how long the cook stays.

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
- The date and time window are picked on the cook page itself, laid out like
  the monthly schedule (day chips, then time pills). Only windows in the
  meals' session are offered, and the first open day and the window the
  meals' usual start time falls in (else the nearest) are pre-selected, as
  monthly pre-selects its start time. They go to checkout as `date` and
  `slot`. Checkout shows them read-only with a Change button; it asks only
  for the address and payment.
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
- The cook page picks meals, people, days, start times and first day, then
  sends them in the URL to `/monthly-service/summary`, the monthly twin of
  checkout: the customer picks the address there (same address list as
  checkout; picked for them like checkout's, `lib/addresses.ts`: an address
  in the area chosen in the header, else one in the plan's city, never one
  outside), confirms how to pay, sees the price per day and per month, and
  presses "Request cook". It is laid out like checkout (Service, Service
  address, Schedule where checkout has Slot, the same price summary). If
  the customer already has a live cook for that plan, address and session,
  the page says so with a link to it and keeps "Request cook" off (the
  server refuses that anyway). Nothing is charged on confirm (product owner,
  2026-10-11): a daily-wallet plan pays each day the night before, a monthly
  bill comes at month end. A wallet that can't pay for the first day gets a
  warning, not a block.
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

The cook page doesn't ask; the customer chooses on the next page (checkout
for one-time, `/monthly-service/summary` for monthly), with the wallet chosen
by default:

| | Online | Wallet |
|---|---|---|
| **One-time** | Normal checkout payment | **Default.** Checkout opens with "use wallet balance" on (`wallet=1`). The wallet covers what it can; the rest is paid online. A booking the wallet covers fully is confirmed with no payment step (existing checkout rule) |
| **Monthly** | One bill at month end (`BillingMode = MonthEnd`); can be set to auto-pay from wallet on the contract page | **Default.** Each day paid from the wallet before the visit (`BillingMode = DailyWallet`); a day the wallet can't pay for is not booked - see MONTHLY-SERVICE.md DAILY WALLET |

## FOOD PREFERENCES

- `MealPreferences` (Domain/Meals): diet (`Veg`, `NonVeg`, `Eggetarian`,
  `Jain`, `NoOnionGarlic`), spice (`Mild`, `Medium`, `Spicy`) and cuisines
  (`NorthIndian`, `Rajasthani`, `SouthIndian`, `Gujarati`, `Punjabi`,
  `Bengali`; none means any). Ordinals on the wire, like `MealType`.
- Asked on both review pages (checkout and `/monthly-service/summary`) in
  the same card. Pre-filled from `GET /api/v1/profile/meal-preferences`
  (204 when none saved yet); with nothing saved it starts at veg, medium
  spice, any cuisine (product owner, 2026-10-11: veg by default).
- Sent as `MealPreferences` on the booking request and the monthly
  meal-contracts request. The booking item and each contract keep a copy
  (what the cook sees: the job card, the monthly visit list, admin's
  contract page), and the customer's saved set is replaced
  (`customer_meal_preference`, one row per customer) in the same save.

## KEEP THIS COOK MONTHLY

- A completed one-time cook booking whose cook is known shows "Keep this
  cook monthly" on its detail page. It opens the cook page in monthly mode
  with the same meals and people and the cook named in the URL.
- The monthly request carries `PreferredProviderId`. The server accepts only
  a professional with a completed booking for this customer
  (`MonthlyService.PreferredProviderUnknown` otherwise) and stores it on the
  contract. It is a wish, not an assignment: the admin still assigns, and
  the assign screen pre-picks and marks the asked-for cook first.

## COOK PAGE URL

The cook page reads its starting choices from its URL (`readCookPageState`
in `lib/meals.ts`; anything malformed is dropped): `mode`, `meals`,
`persons`, `days`, `start`, `morning`, `evening`, `date`, `slot`,
`provider` + `providerName`. Both review pages' "Change" links and "keep
this cook monthly" use it, so the page opens as the customer left it.

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
| `monthly_service_contract.meal_preferences_snapshot` | Food preferences when requested (JSON text) |
| `monthly_service_contract.preferred_provider_id` | The cook asked for ("keep this cook monthly"); not a foreign key |
| `booking_item.meals_mask`, `persons_snapshot`, `meal_preferences_snapshot` | A one-time cook booking's meals, people and food preferences |
| `customer_meal_preference` | Each customer's last food preferences, unique per customer |

Migrations: `20261010193735_AddMealBasedPricing`, `20261011174018_AddMealPreferences`. Local seed:
`database/seed/dev-home-cook-seed.sql` (category Cooking, service Home Cook,
Jaipur mappings, monthly plan).

## KNOWN LIMITS

1. **City pricing.** Meal rates are per service, not per city. City price
   overrides do not apply to meal-priced services.
