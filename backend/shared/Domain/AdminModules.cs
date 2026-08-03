namespace Nestly.Domain;

/// <summary>
/// The Phase 6 admin panel verticals (SRS section 12 — dashboard 12.3,
/// customers 12.4, and one module per remaining admin area). This is the
/// fixed set of modules the permission matrix (<see cref="AdminPermissionCatalog"/>,
/// SRS 12.2.3) is built from — every <see cref="AdminPermission.Module"/>
/// value is one of these constants, never a free-typed string.
/// </summary>
public static class AdminModules
{
    public const string Dashboard = "dashboard";
    public const string Customers = "customers";
    public const string Catalog = "catalog";
    public const string Pricing = "pricing";
    public const string Serviceability = "serviceability";
    public const string Slots = "slots";
    public const string Bookings = "bookings";
    public const string Coupons = "coupons";
    public const string Support = "support";
    public const string Reviews = "reviews";
    public const string Cms = "cms";
    public const string Notifications = "notifications";
    public const string Reports = "reports";
    public const string Audit = "audit";
    public const string Settings = "settings";

    /// <summary>
    /// Provider directory management: CRUD, KYC/background-check approval,
    /// suspend, performance view (PROVIDER.md RBAC ADDITIONS, task 150c).
    /// </summary>
    public const string Provider = "provider";

    /// <summary>Provider payout batches: view, process (mark processing/paid/failed), approve (PROVIDER.md RBAC ADDITIONS, task 150c).</summary>
    public const string Payout = "payout";

    /// <summary>
    /// Referral program config, referral list/fraud-review, and referral
    /// reports (REFERRAL.md RBAC ADDITIONS, task 173). REFERRAL.md asks for
    /// four permission tiers (View/Configure/Approve-Fraud/Export); this
    /// catalog only has two (Read/Write, see <see cref="AdminPermissionAction"/>'s
    /// doc comment, which explicitly anticipates exactly this situation and
    /// calls extending it "a mechanical, backward-compatible extension...
    /// once a controller actually needs that distinction" - no controller
    /// does yet, including this one, so Referral collapses to the existing
    /// two tiers like every other module: Read = View, Write = Configure +
    /// Approve-Fraud + Export, rather than introducing four-tier support for
    /// a single module speculatively.
    /// </summary>
    public const string Referral = "referral";

    /// <summary>
    /// Support-console access to chat threads (PRODUCT-ENHANCEMENTS.md IN-APP
    /// CHAT, RBAC ADDITIONS - the admin-facing console this gates is task 193).
    /// PRODUCT-ENHANCEMENTS.md lists only one tier for this module ("View"),
    /// unlike Subscription's "View / Configure" - this catalog still
    /// mechanically generates both chat.read and chat.write (every module
    /// does, see <see cref="AdminPermissionCatalog.BuildPermissions"/>), but
    /// no role below is granted chat.write and no controller checks it:
    /// replying in the support console is gated behind chat.read alone, the
    /// same "View" tier that grants access to the console at all. That is a
    /// deliberate departure from this catalog's own Read-views/Write-mutates
    /// convention (every other module gates its mutating actions behind
    /// Write, e.g. support.write for a ticket reply) - chosen because
    /// PRODUCT-ENHANCEMENTS.md is explicit that Chat has exactly one tier,
    /// and a customer sending their own chat message needs no elevated
    /// permission beyond thread access either, so treating an admin's reply
    /// the same way (rather than inventing a second tier the source doc
    /// never asked for) keeps the two sides of the same conversation
    /// consistent. See AdminChatController's doc comment for the
    /// controller-side half of this decision.
    /// </summary>
    public const string Chat = "chat";

    /// <summary>
    /// Nestly Coins program config (per audience) and coins-issued/clawed-
    /// back report (docs/NESTLY-COINS.md RBAC ADDITIONS, task 202). The doc
    /// asks for three tiers (View/Configure/Export); this catalog collapses
    /// to the existing two (Read/Write) for the same reason Referral and
    /// Chat already did - see <see cref="Referral"/>'s doc comment for the
    /// full reasoning, which applies unchanged here. No Approve-Fraud tier
    /// either (unlike Referral) - the doc is explicit clawback is automatic
    /// on cancellation, not a manual review queue.
    /// </summary>
    public const string NestlyCoins = "nestly-coins";

    /// <summary>Subscription plan config CRUD (PRODUCT-ENHANCEMENTS.md #1 RBAC ADDITIONS "View / Configure", task 180). Read = View plan list/detail, Write = create/update/activate/deactivate a plan - the catalog's standard two-tier split matches this module's "View / Configure" spec exactly, no collapsing needed.</summary>
    public const string Subscription = "subscription";

    /// <summary>Every module, in the order they appear in SRS section 12, followed by the Phase 7 Provider, Phase 9 Referral, Phase 10 Chat/Subscription, and Phase 11 Nestly Coins module additions (tasks 150c, 173, 194, 180, 202).</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Dashboard, Customers, Catalog, Pricing, Serviceability, Slots, Bookings,
        Coupons, Support, Reviews, Cms, Notifications, Reports, Audit, Settings,
        Provider, Payout, Referral, Chat, NestlyCoins, Subscription
    ];
}
