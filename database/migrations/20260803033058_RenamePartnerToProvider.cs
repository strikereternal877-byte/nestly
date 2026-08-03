using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nestly.Infrastructure.Migrations
{
    /// <summary>
    /// Renames the "Partner" domain concept to "Provider" at the database level.
    /// EF's auto-scaffold produced a destructive drop+recreate (it cannot detect
    /// a rename once the CLR type name changes), so this is hand-written as a
    /// non-destructive rename: every table, column, index, and constraint whose
    /// name contains "partner" is renamed to "provider" via catalog-driven
    /// PL/pgSQL. Because the snake_case naming convention derives every object
    /// name from the table/column names, a consistent partner->provider string
    /// replace reproduces exactly the names EF's regenerated model snapshot
    /// expects. Data is preserved. Also migrates existing admin_permission rows
    /// (module/code) so authorization keeps working after the code-side rename.
    ///
    /// The OAuth "provider" column on *_auth_identity contains no "partner" and
    /// is untouched by the Up filter. Down is intentionally unsupported: a blind
    /// provider->partner reverse would wrongly rename that OAuth column; restore
    /// from a backup (database/scripts/backup-postgres.sh) to roll back.
    /// </summary>
    public partial class RenamePartnerToProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
DECLARE r RECORD;
BEGIN
    -- Tables first, so later column/constraint renames resolve new table names.
    FOR r IN SELECT tablename FROM pg_tables
             WHERE schemaname = 'public' AND tablename LIKE '%partner%' LOOP
        EXECUTE format('ALTER TABLE %I RENAME TO %I',
                       r.tablename, replace(r.tablename, 'partner', 'provider'));
    END LOOP;

    -- Columns (e.g. partner_id foreign keys). The OAuth 'provider' column is
    -- already 'provider' and never matches '%partner%'.
    FOR r IN SELECT table_name, column_name FROM information_schema.columns
             WHERE table_schema = 'public' AND column_name LIKE '%partner%' LOOP
        EXECUTE format('ALTER TABLE %I RENAME COLUMN %I TO %I',
                       r.table_name, r.column_name,
                       replace(r.column_name, 'partner', 'provider'));
    END LOOP;

    -- Constraints (PK / FK / unique / check).
    FOR r IN SELECT conname, conrelid::regclass::text AS tbl FROM pg_constraint
             WHERE connamespace = 'public'::regnamespace AND conname LIKE '%partner%' LOOP
        EXECUTE format('ALTER TABLE %s RENAME CONSTRAINT %I TO %I',
                       r.tbl, r.conname, replace(r.conname, 'partner', 'provider'));
    END LOOP;

    -- Any remaining standalone indexes not already renamed with their constraint.
    FOR r IN SELECT indexname FROM pg_indexes
             WHERE schemaname = 'public' AND indexname LIKE '%partner%' LOOP
        EXECUTE format('ALTER INDEX %I RENAME TO %I',
                       r.indexname, replace(r.indexname, 'partner', 'provider'));
    END LOOP;
END $$;

-- Existing databases seeded the admin permissions under the old 'partner'
-- module/codes; realign them so the renamed authorization checks resolve.
-- On a fresh replay these rows are already 'provider', so this affects 0 rows.
UPDATE admin_permission
   SET module = replace(module, 'partner', 'provider'),
       code   = replace(code,   'partner', 'provider')
 WHERE module LIKE '%partner%' OR code LIKE '%partner%';
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only: a generic provider->partner reverse would also rename
            // the unrelated OAuth 'provider' column. Restore from backup instead.
            migrationBuilder.Sql(
                "DO $$ BEGIN RAISE EXCEPTION " +
                "'RenamePartnerToProvider is forward-only; restore from a backup to roll back.'; END $$;");
        }
    }
}
