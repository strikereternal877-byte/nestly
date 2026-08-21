-- Gives every Active provider real capacity to serve the load harness's own
-- city/category (task #387). Directly modelled on
-- `database/seed/dev-provider-e2e-capacity-seed.sql`, which does the same job
-- for E2E City and explains the reasoning at length.
--
-- WHY THE CHECKOUT SCENARIO NEEDS THIS AT ALL: POST /payments/orders is gated
-- on real fulfillability, not just slot capacity - PaymentService refuses to
-- mint a gateway order for a booking no provider could take
-- (`Payment.NoProviderAvailable`), running the whole
-- ProviderAssignmentEligibilityService chain (schedule conflict, blackouts,
-- weekly availability window, capacity, travel feasibility) before money is
-- at stake. Without provider coverage in the load city every checkout would
-- stop one call short of the gateway, and the scenario would be measuring a
-- 422 path rather than the money path. That eligibility chain is also one of
-- the more expensive reads on checkout, so measuring it is the point - not
-- overhead to be seeded away.
--
-- Providers are selected by status rather than by name (unlike the E2E
-- script's five named demo rows): the harness wants whatever provider
-- coverage the environment actually has.
--
-- Availability is all-day, all-week because
-- ProviderAssignmentEligibilityService.IsAvailableAsync requires the
-- provider's own window to fully contain the booking's slot window, and the
-- harness's slot windows span 00:00-23:59.
--
-- Written as plain statements rather than a DO block on purpose: psql does
-- not interpolate `:variables` inside dollar-quoted bodies, so a DO block
-- could not be parameterised by city name.
--
-- Idempotent. Usage:
--   psql "$DATABASE_URL" -v city_name="Load Test City" -f load/lib/load-providers.sql

\if :{?city_name}
\else
\echo 'ERROR: -v city_name=... is required'
\quit 1
\endif

INSERT INTO provider_service_area (id, provider_id, city_id, zone_id, pincode_id, is_active)
SELECT gen_random_uuid(), p.id, tz.city_id, tz.zone_id, NULL, true
FROM provider p
CROSS JOIN (
    SELECT z.id AS zone_id, z.city_id
    FROM zone z JOIN city c ON c.id = z.city_id
    WHERE c.name = :'city_name'
    ORDER BY z.id
    LIMIT 1
) tz
WHERE p.status = 'Active'
  AND NOT EXISTS (
      SELECT 1 FROM provider_service_area psa
      WHERE psa.provider_id = p.id AND psa.zone_id = tz.zone_id
  );

INSERT INTO provider_skill_mapping (id, provider_id, category_id, service_id, is_active)
SELECT gen_random_uuid(), p.id, cat.id, NULL, true
FROM provider p
CROSS JOIN category cat
WHERE p.status = 'Active'
  AND NOT EXISTS (
      SELECT 1 FROM provider_skill_mapping psm
      WHERE psm.provider_id = p.id AND psm.category_id = cat.id
  );

INSERT INTO provider_availability_window (id, provider_id, day_of_week, start_time, end_time, is_active)
SELECT gen_random_uuid(), p.id, dow, interval '00:00:00', interval '23:59:00', true
FROM provider p
CROSS JOIN unnest(ARRAY['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']) AS dow
WHERE p.status = 'Active'
  AND NOT EXISTS (
      SELECT 1 FROM provider_availability_window w
      WHERE w.provider_id = p.id AND w.day_of_week = dow
  );
