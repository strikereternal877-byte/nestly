namespace Nestly.Application.Abstractions.Caching;

/// <summary>
/// Central cache-key vocabulary (T017). Keys are built here rather than
/// inlined at call sites so invalidation stays deterministic: the code that
/// writes an entry and the code that evicts it derive the same string from the
/// same method (docs/DOTNET.md — "Cache invalidation should be deterministic").
/// </summary>
/// <remarks>
/// Format is <c>nestly:{area}:{identifier}</c>. The shared prefix keeps Nestly
/// entries distinguishable when a Redis instance is shared with another
/// workload, and makes the key space greppable in redis-cli.
/// </remarks>
public static class CacheKeys
{
    private const string Prefix = "nestly";

    /// <summary>Cache areas, one per invalidation boundary.</summary>
    public static class Areas
    {
        public const string Catalog = "catalog";
        public const string Session = "session";

        /// <summary>Chat presence (task 190) - shared across consumer-api/admin-api/provider-api, each its own process.</summary>
        public const string ChatPresence = "chat-presence";
    }

    /// <summary>A single service's catalog projection.</summary>
    public static string Service(Guid serviceId) =>
        Compose(Areas.Catalog, "service", serviceId.ToString("D"));

    /// <summary>A single category's catalog projection.</summary>
    public static string Category(Guid categoryId) =>
        Compose(Areas.Catalog, "category", categoryId.ToString("D"));

    /// <summary>The list of services belonging to a category.</summary>
    public static string ServicesByCategory(Guid categoryId) =>
        Compose(Areas.Catalog, "category", categoryId.ToString("D"), "services");

    /// <summary>
    /// The list of categories serviceable in a city. Short-TTL only (task
    /// 49) - not invalidated by category/mapping events, since determining
    /// every city a changed category maps to would need a reverse scan this
    /// listing doesn't otherwise need. Bounded staleness is an acceptable
    /// trade for a browse-page listing; add precise invalidation if it
    /// becomes a measured problem.
    /// </summary>
    public static string CategoriesInCity(Guid cityId) =>
        Compose(Areas.Catalog, "city", cityId.ToString("D"), "categories");

    /// <summary>Whether a category is serviceable in a city (SRS 12.9.2).</summary>
    public static string CategoryServiceability(Guid categoryId, Guid cityId) =>
        Compose(Areas.Catalog, "serviceability", "category", categoryId.ToString("D"), "city", cityId.ToString("D"));

    /// <summary>Whether a service is serviceable in a pincode (SRS 12.9.2).</summary>
    public static string ServicePincodeServiceability(Guid serviceId, Guid pincodeId) =>
        Compose(Areas.Catalog, "serviceability", "service", serviceId.ToString("D"), "pincode", pincodeId.ToString("D"));

    /// <summary>A customer's active session projection.</summary>
    public static string CustomerSession(Guid customerId) =>
        Compose(Areas.Session, "customer", customerId.ToString("D"));

    /// <summary>Set of live SignalR connection ids for one user, regardless of which API's hub they connected through (task 190).</summary>
    public static string ChatPresence(Guid userId) =>
        Compose(Areas.ChatPresence, "user", userId.ToString("D"));

    /// <summary>
    /// Builds a key from pre-validated segments. Kept private so every key in
    /// the system originates from one of the named methods above.
    /// </summary>
    private static string Compose(params string[] segments) =>
        string.Join(':', segments.Prepend(Prefix));
}
