-- Bootstrap a migrated database into a state where a customer can actually
-- book something (task 389).
--
-- WHY THIS EXISTS
--
-- A database built from migrations alone is unbookable, and says nothing
-- about it. docs/QA-REPORT-2026-08-18.md Phase 1 found zero rows in
-- service_pincode_mapping and slot_window_rule for any seeded city: no
-- customer could book any service, in any environment using that seed set.
-- SlotAvailabilityService and SlotWindowRepository both fail closed by
-- design, so the APIs keep returning correct, empty answers and nothing
-- looks broken. BookabilityProbe (added by the same task) is the diagnostic;
-- this script is the remedy it points at.
--
-- WHAT IT DOES AND DELIBERATELY DOES NOT DO
--
-- It closes the geography -> serviceability -> slot chain for one city:
-- state, city, zone, pincodes, localities, slot windows and their
-- day-of-week rules, then maps every already-active service into that
-- city's pincodes and lists their categories in the city.
--
-- It does NOT create catalog. Categories, services and their pricing are
-- product data an operator owns through the admin UI, and fabricating them
-- in SQL would put unreviewed commercial content into a production
-- database. So the order is: create the catalog in admin-web first, then
-- run this to make it reachable. Running it against an empty catalog is
-- harmless but pointless - it will report that it mapped zero services, and
-- BookabilityProbe will still say bookability.no_active_service.
--
-- IDEMPOTENT. Every insert is guarded, so re-running adds nothing and
-- changes nothing. Safe to run again after adding services.
--
-- USAGE
--
--   psql "$DATABASE_URL" \
--     -v state_name="Karnataka" -v state_code="KA" \
--     -v city_name="Bengaluru" \
--     -v pincodes="560001,560002,560034" \
--     -v capacity=5 \
--     -f database/seed/bootstrap-bookability.sql
--
-- Every variable is required except capacity (per-slot booking cap).
-- Re-run once per city you serve.

\set ON_ERROR_STOP on
\if :{?capacity} \else \set capacity 5 \endif

BEGIN;

-- 1. State ------------------------------------------------------------------
INSERT INTO state (id, name, code, is_active)
SELECT gen_random_uuid(), :'state_name', :'state_code', TRUE
WHERE NOT EXISTS (SELECT 1 FROM state WHERE code = :'state_code');

-- 2. City -------------------------------------------------------------------
-- Matched on (name, state) rather than name alone: docs/QA-REPORT-2026-08-18.md
-- records two distinct "Bengaluru" rows already coexisting in the dev
-- database, and this script must not add a third.
INSERT INTO city (id, state_id, name, is_active)
SELECT gen_random_uuid(), s.id, :'city_name', TRUE
FROM state s
WHERE s.code = :'state_code'
  AND NOT EXISTS (
      SELECT 1 FROM city c WHERE c.state_id = s.id AND c.name = :'city_name');

-- 3. Zone -------------------------------------------------------------------
-- One default zone per city. Zones exist to group localities for operational
-- routing; a single catch-all is the minimum that makes localities insertable
-- and is what an operator subdivides later.
INSERT INTO zone (id, city_id, name, is_active)
SELECT gen_random_uuid(), c.id, :'city_name' || ' - Default Zone', TRUE
FROM city c
JOIN state s ON s.id = c.state_id AND s.code = :'state_code'
WHERE c.name = :'city_name'
  AND NOT EXISTS (SELECT 1 FROM zone z WHERE z.city_id = c.id);

-- 4. Pincodes ---------------------------------------------------------------
INSERT INTO pincode (id, city_id, code, is_active)
SELECT gen_random_uuid(), c.id, trim(p.code), TRUE
FROM city c
JOIN state s ON s.id = c.state_id AND s.code = :'state_code'
CROSS JOIN unnest(string_to_array(:'pincodes', ',')) AS p(code)
WHERE c.name = :'city_name'
  AND trim(p.code) <> ''
  AND NOT EXISTS (
      SELECT 1 FROM pincode x WHERE x.city_id = c.id AND x.code = trim(p.code));

-- 5. Localities -------------------------------------------------------------
-- One locality per pincode. This is the link the QA sweep found missing and
-- it is load-bearing twice over: a customer address is joined to geography
-- through locality, and the slot API is entered by locality id. A pincode
-- with no locality is invisible to both.
INSERT INTO locality (id, zone_id, pincode_id, name, is_active)
SELECT gen_random_uuid(), z.id, p.id, :'city_name' || ' ' || p.code, TRUE
FROM pincode p
JOIN city c ON c.id = p.city_id
JOIN state s ON s.id = c.state_id AND s.code = :'state_code'
JOIN zone z ON z.city_id = c.id
WHERE c.name = :'city_name'
  AND NOT EXISTS (SELECT 1 FROM locality l WHERE l.pincode_id = p.id);

-- 6. Serviceability ---------------------------------------------------------
-- Every active service becomes serviceable in every pincode of this city.
-- Deliberately blanket: a marketplace launching a city offers its catalog
-- there, and narrowing coverage per service is an admin-UI decision made
-- afterwards, not a launch-day one.
INSERT INTO service_pincode_mapping (id, service_id, pincode_id, is_active)
SELECT gen_random_uuid(), sv.id, p.id, TRUE
FROM service sv
CROSS JOIN pincode p
JOIN city c ON c.id = p.city_id
JOIN state s ON s.id = c.state_id AND s.code = :'state_code'
WHERE c.name = :'city_name'
  AND sv.is_active
  AND NOT EXISTS (
      SELECT 1 FROM service_pincode_mapping m
      WHERE m.service_id = sv.id AND m.pincode_id = p.id);

-- 7. Category visibility ----------------------------------------------------
-- Without this a service is bookable by API but unreachable by browsing:
-- CategoryRepository.ListServiceableInCityAsync filters on this table, so the
-- app would never offer the category. That is BookabilityProbe's
-- bookability.no_category_city_mapping gap, and the reason it reports
-- IsDiscoverable separately from IsBookable.
-- DISTINCT is on (category_id, city_id) in the subquery, before id is
-- generated - a DISTINCT that included gen_random_uuid() in the same SELECT
-- would never dedupe, since the random id makes every row unique on its own.
INSERT INTO category_city_mapping (id, category_id, city_id, is_active)
SELECT gen_random_uuid(), t.category_id, t.city_id, TRUE
FROM (
    SELECT DISTINCT sv.category_id, c.id AS city_id
    FROM service sv
    CROSS JOIN city c
    JOIN state s ON s.id = c.state_id AND s.code = :'state_code'
    WHERE c.name = :'city_name'
      AND sv.is_active
) t
WHERE NOT EXISTS (
    SELECT 1 FROM category_city_mapping m
    WHERE m.category_id = t.category_id AND m.city_id = t.city_id);

-- 8. Slot windows -----------------------------------------------------------
-- Three standard windows. Times are intervals from midnight, matching the
-- column type.
INSERT INTO slot_window (id, city_id, name, start_time, end_time, is_active, max_bookings_per_slot)
SELECT gen_random_uuid(), c.id, w.name, w.starts, w.ends, TRUE, :capacity
FROM city c
JOIN state s ON s.id = c.state_id AND s.code = :'state_code'
CROSS JOIN (VALUES
    ('Morning (9 AM - 12 PM)',   INTERVAL '9 hours',  INTERVAL '12 hours'),
    ('Afternoon (12 PM - 3 PM)', INTERVAL '12 hours', INTERVAL '15 hours'),
    ('Evening (3 PM - 6 PM)',    INTERVAL '15 hours', INTERVAL '18 hours')
) AS w(name, starts, ends)
WHERE c.name = :'city_name'
  AND NOT EXISTS (
      SELECT 1 FROM slot_window sw WHERE sw.city_id = c.id AND sw.name = w.name);

-- 9. Slot window rules ------------------------------------------------------
-- A window with no rule row is configured but never offered on any date - the
-- second half of the QA sweep's finding, and the subtler one, because the
-- admin UI shows the window as existing. All seven days; an operator removes
-- the days they do not serve.
INSERT INTO slot_window_rule (id, slot_window_id, day_of_week)
SELECT gen_random_uuid(), sw.id, d.day
FROM slot_window sw
JOIN city c ON c.id = sw.city_id
JOIN state s ON s.id = c.state_id AND s.code = :'state_code'
CROSS JOIN generate_series(0, 6) AS d(day)
WHERE c.name = :'city_name'
  AND NOT EXISTS (
      SELECT 1 FROM slot_window_rule r
      WHERE r.slot_window_id = sw.id AND r.day_of_week = d.day);

COMMIT;

-- What the run actually achieved, so the operator does not have to go
-- looking. Compare against /health/bootstrap, which is the authoritative
-- verdict (this is row counts; that walks the real chain).
SELECT
    (SELECT count(*) FROM pincode p JOIN city c ON c.id = p.city_id WHERE c.name = :'city_name') AS pincodes,
    (SELECT count(*) FROM locality l JOIN pincode p ON p.id = l.pincode_id JOIN city c ON c.id = p.city_id WHERE c.name = :'city_name') AS localities,
    (SELECT count(*) FROM service_pincode_mapping m JOIN pincode p ON p.id = m.pincode_id JOIN city c ON c.id = p.city_id WHERE c.name = :'city_name') AS service_mappings,
    (SELECT count(*) FROM slot_window sw JOIN city c ON c.id = sw.city_id WHERE c.name = :'city_name') AS slot_windows,
    (SELECT count(*) FROM slot_window_rule r JOIN slot_window sw ON sw.id = r.slot_window_id JOIN city c ON c.id = sw.city_id WHERE c.name = :'city_name') AS slot_window_rules;
