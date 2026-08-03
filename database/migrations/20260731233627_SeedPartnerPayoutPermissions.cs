using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore.Migrations;
using Nestly.Domain;

#nullable disable

namespace Nestly.Infrastructure.Migrations
{
    /// <summary>
    /// Task 150c: seeds the two new permission modules PARTNER.md's RBAC
    /// ADDITIONS calls for - Partner and Payout - added to
    /// <see cref="AdminModules.All"/> alongside this migration. Unlike task
    /// 96a's <c>AddAdminPermissionMatrix</c> (which seeded the entire
    /// original matrix in one shot against an empty table), this migration
    /// only inserts the *incremental* rows for the two new modules - the
    /// admin_permission/admin_role/role_permission_mapping tables already
    /// hold every other module's rows from 96a, and re-running that
    /// migration's InsertData for all of <see cref="AdminPermissionCatalog.Permissions"/>
    /// again here would collide on the existing primary keys.
    ///
    /// Uses the exact same deterministic-id scheme
    /// (<see cref="DeterministicId"/>) as 96a so <see cref="RoleId"/> here
    /// resolves to the identical row ids that migration already created -
    /// required for the role_permission_mapping foreign keys below to be
    /// valid without re-inserting admin_role.
    /// </summary>
    public partial class SeedPartnerPayoutPermissions : Migration
    {
        // Same fixed-timestamp rationale as 96a's AddAdminPermissionMatrix:
        // reference data, not a real event, so repeated fresh-database runs
        // stay byte-for-byte identical. A distinct value from 96a's so the
        // two migrations' rows are trivially distinguishable by created_at.
        private static readonly DateTime SeedTimestamp = new(2026, 7, 31, 23, 36, 0, DateTimeKind.Utc);

        private static readonly string[] NewModules = [AdminModules.Provider, AdminModules.Payout];

        private static Guid DeterministicId(string seed)
        {
            byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(seed));
            return new Guid(hash);
        }

        private static Guid PermissionId(string code) => DeterministicId($"admin_permission:{code}");

        private static Guid RoleId(string roleName) => DeterministicId($"admin_role:{roleName}");

        // Same rectangular-array conversion helper as 96a's AddAdminPermissionMatrix.
        private static object[,] AsRows(IReadOnlyList<object[]> rows)
        {
            var result = new object[rows.Count, rows[0].Length];
            for (int i = 0; i < rows.Count; i++)
            {
                for (int j = 0; j < rows[i].Length; j++)
                {
                    result[i, j] = rows[i][j];
                }
            }

            return result;
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var newPermissions = AdminPermissionCatalog.Permissions
                .Where(p => NewModules.Contains(p.Module))
                .ToList();

            migrationBuilder.InsertData(
                table: "admin_permission",
                columns: new[] { "id", "code", "module", "description", "created_at" },
                values: AsRows(newPermissions
                    .Select(p => new object[] { PermissionId(p.Code), p.Code, p.Module, p.Description, SeedTimestamp })
                    .ToArray()));

            // Every seeded role's grant for just the new partner.*/payout.*
            // codes - reads directly from AdminPermissionCatalog.RolePermissionCodes
            // (the single source of truth for who gets what) rather than
            // hand-listing roles here, so this migration can never drift out
            // of sync with AdminPermissionCatalog.BuildRoleModuleGrants().
            var newRoleGrants = AdminRoleNames.All
                .SelectMany(role => AdminPermissionCatalog.RolePermissionCodes[role]
                    .Where(code => newPermissions.Any(p => p.Code == code))
                    .Select(code => new object[]
                    {
                        DeterministicId($"role_permission_mapping:{role}:{code}"),
                        RoleId(role),
                        PermissionId(code),
                        SeedTimestamp
                    }))
                .ToList();

            migrationBuilder.InsertData(
                table: "role_permission_mapping",
                columns: new[] { "id", "role_id", "permission_id", "created_at" },
                values: AsRows(newRoleGrants));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DELETE FROM role_permission_mapping WHERE permission_id IN " +
                "(SELECT id FROM admin_permission WHERE module IN ('provider', 'payout'));");
            migrationBuilder.Sql("DELETE FROM admin_permission WHERE module IN ('provider', 'payout');");
        }
    }
}
