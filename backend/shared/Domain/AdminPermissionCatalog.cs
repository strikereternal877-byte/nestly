namespace Nestly.Domain;

/// <summary>
/// The two permission tiers granted per module. SRS 12.2.3 lists finer-grained
/// actions (View / Create / Edit / Delete / Export / Approve / Refund /
/// Cancel / Reschedule / Publish) as what the matrix should support "at
/// least" by module/action — this catalog implements that with Read/Write,
/// the two tiers every module actually needs today (no controller anywhere
/// in the codebase yet distinguishes, say, "approve" from "edit" within a
/// module). Read covers View; Write covers Create/Edit/Delete/Approve/
/// Refund/Cancel/Reschedule/Publish for that module. Splitting Write further
/// is a mechanical, backward-compatible extension (task 96a's registry, not
/// a redesign) once a controller actually needs that distinction — adding it
/// now would be speculative (YAGNI).
/// </summary>
public enum AdminPermissionAction
{
    Read,
    Write
}

/// <summary>One row of the permission matrix: a grantable code within a module.</summary>
/// <param name="Code">Stable machine-checked key, e.g. "catalog.write" — matches <see cref="AdminPermission.Code"/>.</param>
/// <param name="Module">One of <see cref="AdminModules"/> — matches <see cref="AdminPermission.Module"/>.</param>
/// <param name="Action">Which tier this code grants.</param>
/// <param name="Description">Human-readable label for the admin permission-matrix UI.</param>
public sealed record AdminPermissionDefinition(string Code, string Module, AdminPermissionAction Action, string Description);

/// <summary>
/// The Phase 6 permission matrix (SRS 12.2.3): every grantable permission
/// code, and which of the SRS 12.2.2 default roles hold each one. This is the
/// single source of truth read by:
/// <list type="bullet">
/// <item>the seed migration that populates <c>admin_permission</c>, <c>admin_role</c>
/// and <c>role_permission_mapping</c> (task 96a),</item>
/// <item>authorization policy registration, one ASP.NET Core policy per code
/// (task 96b), and</item>
/// <item>the login-time lookup that turns an admin user's role into the
/// "permission" JWT claims issued at token generation (task 96c).</item>
/// </list>
/// Changing who can do what is a one-line edit here, not a hunt through
/// controllers.
/// </summary>
public static class AdminPermissionCatalog
{
    /// <summary>Builds the stable code for a module/action pair, e.g. ("catalog", Write) -&gt; "catalog.write".</summary>
    public static string BuildCode(string module, AdminPermissionAction action) =>
        $"{module}.{(action == AdminPermissionAction.Read ? "read" : "write")}";

    /// <summary>Every permission code: Read and Write for every module in <see cref="AdminModules.All"/>.</summary>
    public static readonly IReadOnlyList<AdminPermissionDefinition> Permissions = BuildPermissions();

    /// <summary>The default role -&gt; permission-code grants seeded for SRS 12.2.2's roles.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> RolePermissionCodes = BuildRoleGrants();

    private static IReadOnlyList<AdminPermissionDefinition> BuildPermissions()
    {
        var permissions = new List<AdminPermissionDefinition>();
        foreach (string module in AdminModules.All)
        {
            permissions.Add(new AdminPermissionDefinition(
                BuildCode(module, AdminPermissionAction.Read), module, AdminPermissionAction.Read,
                $"View {module}"));
            permissions.Add(new AdminPermissionDefinition(
                BuildCode(module, AdminPermissionAction.Write), module, AdminPermissionAction.Write,
                $"Create, edit, delete and take action within {module}"));
        }

        return permissions;
    }

    /// <summary>
    /// Module grants per role, expressed as (module, canWrite) — Read is
    /// implied whenever a role can Write (a role that may change a module
    /// can always view it too), so each row only needs to say whether the
    /// grant extends to Write.
    /// </summary>
    /// <remarks>
    /// A method, not a static field: <see cref="RolePermissionCodes"/>'s own
    /// field initializer calls <see cref="BuildRoleGrants"/> at class-init
    /// time, and static field initializers run strictly in declaration
    /// order — a field here would have still been null at that point.
    /// </remarks>
    private static IReadOnlyDictionary<string, (string Module, bool CanWrite)[]> BuildRoleModuleGrants() =>
        new Dictionary<string, (string, bool)[]>
        {
            // Super Admin (SRS 12.2.1): full control, including the settings
            // module (admin user/role/permission management itself) and the
            // audit trail — nothing else grants either of those two.
            [AdminRoleNames.SuperAdmin] = AdminModules.All.Select(m => (m, true)).ToArray(),

            // Operations Admin: day-to-day operational oversight across the
            // customer-facing pipeline, read-only into the specialist
            // modules (catalog/pricing/coupons/reviews) it depends on but
            // does not own.
            // Provider=true added (task 150c): assigning/managing the providers
            // who fulfil bookings is day-to-day operational work, same tier
            // as Bookings/Support. Payout is deliberately not granted here -
            // that is Finance Admin's territory below.
            // Chat=true added (task 193): this role already owns Support and
            // Bookings, the two contexts a chat thread is ever scoped to.
            [AdminRoleNames.OperationsAdmin] =
            [
                (AdminModules.Dashboard, true), (AdminModules.Customers, true),
                (AdminModules.Serviceability, true), (AdminModules.Slots, true),
                (AdminModules.Bookings, true), (AdminModules.Support, true),
                (AdminModules.Notifications, true), (AdminModules.Catalog, false),
                (AdminModules.Pricing, false), (AdminModules.Coupons, false),
                (AdminModules.Reviews, false), (AdminModules.Reports, false),
                (AdminModules.Audit, false), (AdminModules.Provider, true),
                (AdminModules.Chat, true)
            ],

            // Booking Admin: the booking lifecycle and the slot capacity it
            // depends on; read-only visibility into who and what is involved.
            [AdminRoleNames.BookingAdmin] =
            [
                (AdminModules.Dashboard, false), (AdminModules.Customers, false),
                (AdminModules.Bookings, true), (AdminModules.Slots, true),
                (AdminModules.Serviceability, false), (AdminModules.Support, false),
                (AdminModules.Reports, false)
            ],

            // Support Admin: tickets and the reviews customers leave;
            // read-only into the bookings a ticket usually references.
            // Chat=true added (task 193): the support console's reply view
            // is this role's day-to-day tool, same tier as Support.
            [AdminRoleNames.SupportAdmin] =
            [
                (AdminModules.Dashboard, false), (AdminModules.Customers, false),
                (AdminModules.Bookings, false), (AdminModules.Support, true),
                (AdminModules.Reviews, true), (AdminModules.Notifications, false),
                (AdminModules.Reports, false), (AdminModules.Chat, true)
            ],

            // Catalog Admin: the service catalog, the serviceability map that
            // gates where it can be sold, and the CMS content describing it.
            [AdminRoleNames.CatalogAdmin] =
            [
                (AdminModules.Dashboard, false), (AdminModules.Catalog, true),
                (AdminModules.Serviceability, true), (AdminModules.Slots, false),
                (AdminModules.Cms, true), (AdminModules.Reports, false)
            ],

            // Pricing Admin: city/category pricing and the coupons that
            // discount it; read-only into the catalog those prices attach to.
            [AdminRoleNames.PricingAdmin] =
            [
                (AdminModules.Dashboard, false), (AdminModules.Pricing, true),
                (AdminModules.Catalog, false), (AdminModules.Coupons, true),
                (AdminModules.Reports, false)
            ],

            // Marketing Admin: campaigns — coupons, push/SMS/email
            // notifications, and CMS content; read-only into reviews as
            // customer-sentiment context. Referral=true added (task 173):
            // the referral program (config, fraud review, funnel/cost
            // reports) is a growth campaign in the same cluster as coupons
            // and notifications, both of which this role already owns.
            // NestlyCoins=true added (task 202): same reasoning - a reorder/
            // repeat-activity incentive program sits in the same growth
            // cluster as Referral, just configured/reported differently.
            // Subscription=true added (task 180): plan config (price,
            // billing cycle, benefits) is a monetization campaign in the
            // same cluster as Coupons/Referral, both of which this role
            // already owns.
            [AdminRoleNames.MarketingAdmin] =
            [
                (AdminModules.Dashboard, false), (AdminModules.Coupons, true),
                (AdminModules.Notifications, true), (AdminModules.Cms, true),
                (AdminModules.Reviews, false), (AdminModules.Reports, false),
                (AdminModules.Referral, true), (AdminModules.NestlyCoins, true),
                (AdminModules.Subscription, true)
            ],

            // Finance Admin: financial reporting and the audit trail behind
            // it; read-only into bookings/coupons as the source of that data.
            // Payout=true added (task 150c): running/processing manual bank
            // transfer payouts (PROVIDER.md OPEN DECISIONS #3) is financial
            // operations work, the same tier as Reports; Provider is
            // read-only context (who is being paid), not full provider
            // management, which stays with Operations Admin above.
            // Referral=false added (task 173): read-only visibility into
            // program cost (the referral funnel/cost report is one of this
            // role's Reports), not configuration or fraud-review authority,
            // which stays with Marketing Admin above. NestlyCoins=false
            // added (task 202): same reasoning, read-only cost visibility
            // into the coins-issued/clawed-back report.
            // Subscription=false added (task 180): read-only visibility into
            // recurring subscription revenue as reporting context, not
            // plan-configuration authority, which stays with Marketing
            // Admin above - same treatment as Referral=false right above.
            [AdminRoleNames.FinanceAdmin] =
            [
                (AdminModules.Dashboard, false), (AdminModules.Bookings, false),
                (AdminModules.Coupons, false), (AdminModules.Reports, true),
                (AdminModules.Audit, false), (AdminModules.Provider, false),
                (AdminModules.Payout, true), (AdminModules.Referral, false),
                (AdminModules.NestlyCoins, false), (AdminModules.Subscription, false)
            ],

            // Read-only Analyst: visibility everywhere, authority nowhere —
            // generated below rather than listed module-by-module so it can
            // never silently drift out of sync with AdminModules.All.
            [AdminRoleNames.ReadOnlyAnalyst] = AdminModules.All.Select(m => (m, false)).ToArray()
        };

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> BuildRoleGrants()
    {
        var grants = new Dictionary<string, IReadOnlySet<string>>();
        foreach ((string role, (string Module, bool CanWrite)[] moduleGrants) in BuildRoleModuleGrants())
        {
            var codes = new HashSet<string>();
            foreach ((string module, bool canWrite) in moduleGrants)
            {
                codes.Add(BuildCode(module, AdminPermissionAction.Read));
                if (canWrite)
                {
                    codes.Add(BuildCode(module, AdminPermissionAction.Write));
                }
            }

            grants[role] = codes;
        }

        return grants;
    }
}
