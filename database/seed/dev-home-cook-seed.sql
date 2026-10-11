-- Home Cook: a meal-priced service (docs/MEAL-SERVICE.md) bookable one-time
-- and as a monthly plan, for local development and demos in Jaipur.
--
-- Creates, if missing:
--   1. Top-level category "Cooking", visible in Jaipur
--   2. Service "Home Cook" (pricing_type MealBased) with sample meal rates -
--      its price (80.00) is the cheapest meal, the rule Service.SetMealOptions
--      keeps whenever an admin edits the rates
--   3. Service-to-pincode mappings for every Jaipur pincode
--   4. Monthly plan "Home Cook - Jaipur" (MealBased, chosen weekdays, 10% off one-time)
--
-- The rates are sample values, not a pricing decision - change them from
-- admin-web (Catalog > Services > Home Cook > Meal rates).
--
-- Bookings still need a professional with the Cooking skill in Jaipur
-- (Providers > profile > Skills), or BookingService rejects them with
-- Booking.NoProviderAvailable.
--
-- IDEMPOTENT. Usage: psql "$DATABASE_URL" -f database/seed/dev-home-cook-seed.sql

\set ON_ERROR_STOP on

BEGIN;

-- 1. Category ------------------------------------------------------------------
INSERT INTO category (id, name, slug, description, is_active, is_featured, sort_order)
SELECT gen_random_uuid(), 'Cooking', 'cooking',
       'A trained cook for your daily meals - priced by meals and people.',
       TRUE, TRUE, 0
WHERE NOT EXISTS (SELECT 1 FROM category WHERE slug = 'cooking');

INSERT INTO category_city_mapping (id, category_id, city_id, is_active)
SELECT gen_random_uuid(), cat.id, c.id, TRUE
FROM category cat
JOIN city c ON c.name = 'Jaipur'
WHERE cat.slug = 'cooking'
  AND NOT EXISTS (SELECT 1 FROM category_city_mapping m WHERE m.category_id = cat.id AND m.city_id = c.id);

-- 2. Service -------------------------------------------------------------------
INSERT INTO service (id, category_id, name, slug, description, short_description, inclusions, exclusions,
                     price, duration_minutes, is_active, sort_order, pricing_type, is_add_on_allowed,
                     is_quantity_allowed, is_featured, cancellation_policy, meal_options)
SELECT gen_random_uuid(), cat.id, 'Home Cook', 'home-cook',
       'A trained cook who makes fresh, home-style meals in your kitchen - for one day or every day.',
       'Fresh home-style meals, cooked in your kitchen',
       'Cooking the chosen meals for the chosen number of people
Chopping and prep
Wiping the cooking area after cooking',
       'Groceries and gas (your kitchen, your ingredients)
Washing all utensils
Serving or clearing the table',
       80.00, 60, TRUE, 0, 'MealBased', FALSE, FALSE, TRUE,
       'Free cancellation up to 3 hours before the visit.',
       '[{"meal":"Breakfast","baseRate":100,"includedPersons":2,"extraPersonRate":30,"baseMinutes":45,"minutesPerExtraPerson":10,"defaultStartTime":"07:30:00"},' ||
       '{"meal":"Lunch","baseRate":150,"includedPersons":2,"extraPersonRate":40,"baseMinutes":60,"minutesPerExtraPerson":10,"defaultStartTime":"11:30:00"},' ||
       '{"meal":"Snacks","baseRate":80,"includedPersons":2,"extraPersonRate":20,"baseMinutes":30,"minutesPerExtraPerson":5,"defaultStartTime":"16:30:00"},' ||
       '{"meal":"Dinner","baseRate":150,"includedPersons":2,"extraPersonRate":40,"baseMinutes":60,"minutesPerExtraPerson":10,"defaultStartTime":"19:00:00"}]'
FROM category cat
WHERE cat.slug = 'cooking'
  AND NOT EXISTS (SELECT 1 FROM service WHERE slug = 'home-cook');

-- 3. Bookable in every Jaipur pincode ---------------------------------------------
INSERT INTO service_pincode_mapping (id, service_id, pincode_id, is_active)
SELECT gen_random_uuid(), sv.id, p.id, TRUE
FROM service sv
CROSS JOIN pincode p
JOIN city c ON c.id = p.city_id AND c.name = 'Jaipur'
WHERE sv.slug = 'home-cook'
  AND NOT EXISTS (SELECT 1 FROM service_pincode_mapping m WHERE m.service_id = sv.id AND m.pincode_id = p.id);

-- 4. Monthly plan ----------------------------------------------------------------
INSERT INTO monthly_service_plan (id, service_id, city_id, name, description, basis, hours_per_visit, included_tasks,
                                  rate_per_visit, commission_percent, frequency, times_per_period, is_active,
                                  created_at_utc, updated_at_utc, monthly_discount_percent)
SELECT gen_random_uuid(), sv.id, c.id, 'Home Cook - Jaipur',
       'The same cook on your chosen days, paid day by day from your wallet or with one bill at month end.',
       'MealBased', NULL, '', 0, 15, 'Weekdays', NULL, TRUE, now(), now(), 10
FROM service sv
JOIN city c ON c.name = 'Jaipur'
WHERE sv.slug = 'home-cook'
  AND NOT EXISTS (SELECT 1 FROM monthly_service_plan WHERE name = 'Home Cook - Jaipur');

COMMIT;
