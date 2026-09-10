using System.Globalization;
using System.Linq;

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

        /// <summary>Server-side price calculation results (task: catalog/pricing response-time fix).</summary>
        public const string Pricing = "pricing";

        /// <summary>Chat presence (task 190) - shared across consumer-api/admin-api/provider-api, each its own process.</summary>
        public const string ChatPresence = "chat-presence";

        /// <summary>Road travel estimates from an external routing provider (task 266).</summary>
        public const string RouteEstimate = "route-estimate";
    }

    /// <summary>
    /// Decimal places coordinates are rounded to before they become part of a
    /// route-estimate key (task 266). Four places is ~11 m of latitude (and
    /// ~10-11 m of longitude at Indian latitudes) - deliberately coarser than
    /// a raw GPS fix so a provider idling at a traffic light keeps hitting the
    /// same cached leg, and deliberately finer than the ~111 m that three
    /// places would give, which could snap a point across a divided road onto
    /// the wrong carriageway. The rounding error it introduces sits well
    /// inside the 5-20 m a phone fix is already uncertain by, so it costs no
    /// real accuracy.
    /// </summary>
    public const int RouteEstimateCoordinateDecimals = 4;

    private static readonly string CoordinateFormat =
        "F" + RouteEstimateCoordinateDecimals.ToString(CultureInfo.InvariantCulture);

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
    /// <summary>
    /// <paramref name="pincodeId"/> is folded into the key (not just cached
    /// per-city and filtered client-side): the same city, area-narrowed and
    /// not, are genuinely different result sets and must not collide on one
    /// cache entry.
    /// </summary>
    public static string CategoriesInCity(Guid cityId, Guid? pincodeId = null) =>
        pincodeId is null
            ? Compose(Areas.Catalog, "city", cityId.ToString("D"), "categories")
            : Compose(Areas.Catalog, "city", cityId.ToString("D"), "pincode", pincodeId.Value.ToString("D"), "categories");

    /// <summary>Whether a category is serviceable in a city (SRS 12.9.2).</summary>
    public static string CategoryServiceability(Guid categoryId, Guid cityId) =>
        Compose(Areas.Catalog, "serviceability", "category", categoryId.ToString("D"), "city", cityId.ToString("D"));

    /// <summary>Whether a service is serviceable in a pincode (SRS 12.9.2).</summary>
    public static string ServicePincodeServiceability(Guid serviceId, Guid pincodeId) =>
        Compose(Areas.Catalog, "serviceability", "service", serviceId.ToString("D"), "pincode", pincodeId.ToString("D"));

    /// <summary>
    /// One server-side price calculation result (task: catalog/pricing
    /// response-time fix). Every input the calculation actually reads -
    /// service, city, variant, quantity, and the exact add-on selection -
    /// is folded into the key: two requests that differ in any one of these
    /// can price out differently (a different city's visit charge/tax, a
    /// different variant's price, a different add-on mix), so collapsing
    /// them onto the same cache entry would risk serving one customer
    /// another's price. Add-on selections are sorted by id first so the same
    /// selection sent in a different order (a client re-ordering an object,
    /// not a different selection) still hits the same entry.
    /// </summary>
    public static string PriceCalculation(
        Guid serviceId,
        Guid cityId,
        Guid? serviceVariantId,
        int quantity,
        IEnumerable<(Guid AddOnId, int Quantity)> addOns)
    {
        string addOnsSegment = string.Join(
            '_',
            addOns
                .OrderBy(a => a.AddOnId)
                .Select(a => $"{a.AddOnId:D}-{a.Quantity.ToString(CultureInfo.InvariantCulture)}"));

        return Compose(
            Areas.Pricing,
            "service", serviceId.ToString("D"),
            "city", cityId.ToString("D"),
            "variant", serviceVariantId?.ToString("D") ?? "none",
            "qty", quantity.ToString(CultureInfo.InvariantCulture),
            "addons", addOnsSegment.Length == 0 ? "none" : addOnsSegment);
    }

    /// <summary>A customer's active session projection.</summary>
    public static string CustomerSession(Guid customerId) =>
        Compose(Areas.Session, "customer", customerId.ToString("D"));

    /// <summary>Set of live SignalR connection ids for one user, regardless of which API's hub they connected through (task 190).</summary>
    public static string ChatPresence(Guid userId) =>
        Compose(Areas.ChatPresence, "user", userId.ToString("D"));

    /// <summary>
    /// One origin-to-destination road leg from an external routing provider
    /// (task 266). Coordinates are rounded to
    /// <see cref="RouteEstimateCoordinateDecimals"/> places so that a moving
    /// provider's successive fixes collapse onto the same key instead of
    /// missing the cache on every ping.
    /// </summary>
    /// <remarks>
    /// The key is derived from coordinates only. The routing provider's API
    /// key is never part of it - a cache key travels to Redis, appears in
    /// <c>redis-cli</c> output and in this application's own cache-miss logs,
    /// none of which is a place a credential belongs.
    /// </remarks>
    public static string RouteEstimate(
        decimal originLatitude,
        decimal originLongitude,
        decimal destinationLatitude,
        decimal destinationLongitude) =>
        Compose(
            Areas.RouteEstimate,
            FormatCoordinate(originLatitude),
            FormatCoordinate(originLongitude),
            FormatCoordinate(destinationLatitude),
            FormatCoordinate(destinationLongitude));

    /// <summary>
    /// Rounds and formats one coordinate component for a cache key. Rounding
    /// mode and culture are both stated explicitly: a key computed on a
    /// <c>de-DE</c> host must be byte-identical to one computed on
    /// <c>en-IN</c>, or replicas would silently keep separate caches.
    /// </summary>
    private static string FormatCoordinate(decimal degrees) =>
        Math.Round(degrees, RouteEstimateCoordinateDecimals, MidpointRounding.AwayFromZero)
            .ToString(CoordinateFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Builds a key from pre-validated segments. Kept private so every key in
    /// the system originates from one of the named methods above.
    /// </summary>
    private static string Compose(params string[] segments) =>
        string.Join(':', segments.Prepend(Prefix));
}
