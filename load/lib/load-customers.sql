-- Load-harness customer pool (task #387).
--
-- WHY DIRECT INSERT, when every other fixture in this harness is built
-- through the real APIs: registration goes through POST /auth/registration/otp,
-- which generates a real random code and dispatches it through
-- SandboxNotificationProvider - a provider that deliberately never logs or
-- exposes the code anywhere retrievable, with no test-mode bypass. This is
-- the identical, already-established exception that
-- `database/seed/dev-customer-seed.sql` documents for the single E2E
-- customer; this script is that same bootstrap, parameterised to N accounts
-- because the contention scenario needs N *distinct* customers racing for one
-- slot (a promotion stampede is many customers, not one customer retrying,
-- and Booking's idempotency-key path would collapse the latter into a single
-- winner for reasons that have nothing to do with slot capacity).
--
-- Only the account bootstrap skips the OTP flow. Every one of these accounts
-- then authenticates through the real, unmodified POST /auth/login/password
-- endpoint, exactly like the E2E suite's customer does.
--
-- Password for every account: E2eCustomer!Passw0rd (local/dev only). The hash
-- is copied verbatim from database/seed/dev-customer-seed.sql - it is
-- Microsoft.AspNetCore.Identity.PasswordHasher<T>'s PBKDF2 output for that
-- password, which cannot be regenerated from psql alone.
--
-- Idempotent: re-running tops the pool up to :count without touching existing
-- rows. Ids are derived from the index so a re-run addresses the same
-- accounts rather than orphaning the previous run's.
--
-- Usage: psql "$DATABASE_URL" -v count=64 -f load/lib/load-customers.sql

INSERT INTO customer (id, mobile, email, name, date_of_birth, address, city, state, pincode, country, created_at, updated_at, status)
SELECT
    ('10adc057-0000-4000-8000-' || lpad(to_hex(i), 12, '0'))::uuid,
    '+9170' || lpad(i::text, 8, '0'),
    'load-customer-' || lpad(i::text, 4, '0') || '@nestly.local',
    'Load Customer ' || lpad(i::text, 4, '0'),
    '1990-01-01T00:00:00Z',
    '', '', '', '',
    'India',
    now(),
    now(),
    'Active'
FROM generate_series(1, :count) AS i
WHERE NOT EXISTS (
    SELECT 1 FROM customer c
    WHERE c.email = 'load-customer-' || lpad(i::text, 4, '0') || '@nestly.local'
);

INSERT INTO customer_auth_identity (id, customer_id, provider, identifier, password_hash, is_primary, created_at)
SELECT
    ('10adc057-1111-4000-8000-' || lpad(to_hex(i), 12, '0'))::uuid,
    ('10adc057-0000-4000-8000-' || lpad(to_hex(i), 12, '0'))::uuid,
    'EmailPassword',
    'load-customer-' || lpad(i::text, 4, '0') || '@nestly.local',
    'AQAAAAIAAYagAAAAEKwQUh5YlPiRBR1Sa8hcFTkvuPEXs+VwLzQ/bgXqYo91TbriZmcbw7WMVHiM++WnqA==',
    true,
    now()
FROM generate_series(1, :count) AS i
WHERE NOT EXISTS (
    SELECT 1 FROM customer_auth_identity cai
    WHERE cai.provider = 'EmailPassword'
      AND cai.identifier = 'load-customer-' || lpad(i::text, 4, '0') || '@nestly.local'
);
