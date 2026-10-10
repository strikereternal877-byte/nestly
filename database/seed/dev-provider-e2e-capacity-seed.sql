-- Gives E2E Test Provider plus 5 already-Active providers (originally
-- Bengaluru-only demo data) real capacity to serve E2E City, companion to
-- dev-category-city-mapping-seed.sql.
--
-- E2E Test Provider is included because it is the only provider that exists
-- at all against a fresh database (ci.yml's `e2e` job, any newly-provisioned
-- dev database): without a ProviderServiceArea + ProviderSkillMapping row of
-- its own, ProviderMatchingService.FindCandidatesAsync never returns it as a
-- candidate, which means BookingService's own-eligibility gate
-- (Booking.NoProviderAvailable, see that class) rejects every booking
-- attempt against E2E City outright - not a downstream assignment failure,
-- a hard failure at booking creation, regardless of this seed's ordering
-- relative to anything else.
--
-- The other 5 ('Provider 1/2/5/6/7') are long-lived local/demo data that may
-- not exist in a fresh database (this DO block's own FOR loop simply finds
-- nothing for them there, which is fine) - kept for the reason below.
--
-- Why more than one provider: E2E City originally had exactly one capable
-- provider, which meant any two same-day bookings would always collide on
-- BookingProviderAssignmentService's unconditional double-booking guard
-- (task 288) - the second booking would simply never get an automatic
-- assignment, not because of a bug, but because there was genuinely only one
-- provider who could ever take it. Several providers gives enough real spare
-- capacity to test concurrent same-day bookings without hitting that
-- ceiling immediately.
--
-- Each matched provider gets:
--   - provider_service_area: E2E City's zone (zone-wide, not pincode-pinned)
--   - provider_skill_mapping: every category (whole-category, not
--     service-pinned) - mirrors dev-category-city-mapping-seed.sql's
--     "unlock everything for E2E City" scope
--   - provider_availability_window: all 7 days, 00:00-23:59 - the E2E test
--     slot window ("E2E Anytime") spans the full day, and
--     ProviderAssignmentEligibilityService.IsAvailableAsync requires the
--     provider's own window to fully contain the booking's slot window, so
--     a partial-day window (e.g. the original Monday 09:00-17:00 seed data)
--     silently fails eligibility for this slot even on the right day.
--
-- Looked up by name rather than a hardcoded id: provider/city ids are
-- generated per environment.
--
-- Usage: psql "$DATABASE_URL" -f database/seed/dev-provider-e2e-capacity-seed.sql

DO $$
DECLARE
    pid uuid;
BEGIN
    FOR pid IN
        SELECT id FROM provider
        WHERE display_name IN ('E2E Test Provider', 'Provider 1', 'Provider 2', 'Provider 5', 'Provider 6', 'Provider 7')
    LOOP
        INSERT INTO provider_service_area (id, provider_id, city_id, zone_id, pincode_id, is_active)
        SELECT gen_random_uuid(), pid, c.id, z.id, NULL, true
        FROM city c JOIN zone z ON z.city_id = c.id
        WHERE c.name = 'E2E City'
        ON CONFLICT DO NOTHING;

        INSERT INTO provider_skill_mapping (id, provider_id, category_id, service_id, is_active)
        SELECT gen_random_uuid(), pid, cat.id, NULL, true
        FROM category cat
        WHERE NOT EXISTS (
            SELECT 1 FROM provider_skill_mapping psm
            WHERE psm.provider_id = pid AND psm.category_id = cat.id
        );

        INSERT INTO provider_availability_window (id, provider_id, day_of_week, start_time, end_time, is_active)
        SELECT gen_random_uuid(), pid, dow, interval '00:00:00', interval '23:59:00', true
        FROM unnest(ARRAY['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']) AS dow
        WHERE NOT EXISTS (
            SELECT 1 FROM provider_availability_window w
            WHERE w.provider_id = pid AND w.day_of_week = dow
        );
    END LOOP;
END $$;
