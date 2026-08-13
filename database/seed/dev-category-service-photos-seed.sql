-- Wires real photography into category.banner_url and service.cover_image_url
-- for local development, companion to dev-admin-seed.sql / dev-customer-seed.sql
-- / dev-provider-seed.sql / dev-category-city-mapping-seed.sql.
--
-- Why this exists: neither column is populated by any EF Core migration
-- (schema only, no data), so a fresh database only ever has the flat category
-- icon (icon_url) to fall back on for both category banners and service cover
-- images - correct behavior, but it means the photography-led "Quiet Ground"
-- redesign (and the photo-populated ledger cards in "Service Ledger") render
-- with icon-only fallbacks anywhere this script hasn't been run.
--
-- Images themselves are committed under
-- frontend/customer-web/public/images/{categories,services}/photos/ on the
-- redesign/service-ledger and redesign/quiet-ground branches (free,
-- commercially-licensed stock photography, not AI-generated or fabricated).
-- This script only wires the existing files' relative paths into the rows
-- that reference them - it does not add or modify any image file.
--
-- Looked up by slug rather than a hardcoded id: ids are generated per
-- environment, so a literal uuid here would only work against the exact
-- database it was copied from.
--
-- Usage: psql "$DATABASE_URL" -f database/seed/dev-category-service-photos-seed.sql

UPDATE category SET banner_url = '/images/categories/photos/' || slug || '.webp'
WHERE slug IN (
    'home-cleaning', 'ac-repair-service', 'plumbing', 'electrical',
    'carpentry', 'pest-control', 'painting', 'appliance-repair',
    'salon-for-women', 'salon-for-men'
);

-- Services default to their category's photo (set first, below), then get
-- a distinct photo per service where one exists (set second, overriding the
-- category default) - so a service without its own dedicated photo still
-- shows real photography instead of an icon.

UPDATE service s SET cover_image_url = c.banner_url
FROM category c
WHERE s.category_id = c.id AND c.banner_url IS NOT NULL AND c.banner_url <> '';

UPDATE service SET cover_image_url = '/images/services/photos/' || slug || '.webp'
WHERE slug IN (
    'kitchen-deep-cleaning', 'bathroom-deep-cleaning', 'sofa-shampoo-cleaning',
    'ac-repair-visit', 'ac-installation', 'ac-service-gas-top-up',
    'tap-mixer-repair', 'pipe-leakage-repair', 'bathroom-fitting-installation',
    'switch-socket-repair', 'fan-installation', 'wiring-inspection',
    'furniture-assembly', 'door-repair',
    'general-pest-control', 'cockroach-control',
    'single-room-painting', 'full-home-painting',
    'refrigerator-repair', 'washing-machine-repair',
    'facial-and-cleanup', 'waxing-full-arms-legs', 'haircut-and-styling'
);
